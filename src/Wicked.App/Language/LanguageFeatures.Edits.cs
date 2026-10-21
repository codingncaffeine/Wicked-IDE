using System.Text.Json;
using Wicked.App.Workbench;
using Wicked.Core;
using Wicked.Core.Files;
using Wicked.Core.Lsp;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.App.Language;

public sealed partial class LanguageFeatures
{
    /// <summary>
    /// Applies a server's workspace edit (a rename, a quick fix, a refactoring): files created or renamed first, then the
    /// text edits — an open file is changed in its editor as one undoable step and saved when it had no unsaved changes
    /// before (VS Code's files.refactoring.autoSave), a closed file is read, changed and written on a worker — then files
    /// deleted. UI thread; true when every part applied.
    /// </summary>
    public async Task<bool> ApplyWorkspaceEditAsync(WorkspaceEdit edit)
    {
        var ok = true;
        var deletes = new List<JsonElement>();
        foreach (var op in edit.FileOperations)
        {
            var kind = op.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind == "delete")
            {
                deletes.Add(op);
                continue;
            }

            ok &= await Task.Run(() => FileOperation(op, kind));
        }

        foreach (var (uri, edits) in edit.Changes)
        {
            if (LspPaths.ToPath(uri) is not { } path) continue;
            var open = _bench.AllTabs.FirstOrDefault(t => t.Path is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(path), StringComparison.Ordinal));
            if (open is not null)
            {
                var wasClean = !open.IsDirty;
                ApplyEdits(open.Document, edits);
                if (wasClean) await _bench.SaveAsync(open, auto: true);
                continue;
            }

            ok &= await Task.Run(() => EditClosedFileAsync(path, edits));
        }

        foreach (var op in deletes) ok &= await Task.Run(() => FileOperation(op, "delete"));
        return ok;
    }

    /// <summary>A server's edits to an open document as one change, the selections carried through it.</summary>
    public static void ApplyEdits(Document document, IReadOnlyList<TextEdit> edits)
    {
        var changes = LspText.ChangesFrom(document.Text, edits);
        if (changes.Count == 0) return;
        document.Apply(changes, LspText.MapSelections(document.Selections, changes), EditKind.Other, document.PrimaryIndex);
    }

    private static async Task<bool> EditClosedFileAsync(string path, IReadOnlyList<TextEdit> edits)
    {
        try
        {
            var loaded = await TextFile.ReadAsync(path, true, CancellationToken.None).ConfigureAwait(false);
            var (rope, lineBreak) = TextLoading.Normalise(loaded.Text);
            var changes = LspText.ChangesFrom(rope, edits);
            var after = ChangeSet.Apply(rope, ChangeSet.Normalise(changes));
            var text = after.ToString();
            if (lineBreak != "\n") text = text.Replace("\n", lineBreak, StringComparison.Ordinal);
            await TextFile.WriteAsync(path, text, loaded.Encoding, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"A refactoring could not change {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>create, rename or delete, with the protocol's overwrite / ignoreIfExists / recursive options.</summary>
    private static bool FileOperation(JsonElement op, string? kind)
    {
        bool Option(string name) => op.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        string? PathOf(string member) => op.TryGetProperty(member, out var u) && u.GetString() is { } s ? LspPaths.ToPath(s) : null;
        try
        {
            switch (kind)
            {
                case "create" when PathOf("uri") is { } path:
                    if (File.Exists(path) && !Option("overwrite")) return Option("ignoreIfExists");
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "");
                    return true;
                case "rename" when PathOf("oldUri") is { } from && PathOf("newUri") is { } to:
                    if ((File.Exists(to) || Directory.Exists(to)) && !Option("overwrite")) return Option("ignoreIfExists");
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to, overwrite: true);
                    return true;
                case "delete" when PathOf("uri") is { } path:
                    if (Directory.Exists(path)) Directory.Delete(path, Option("recursive"));
                    else if (File.Exists(path)) File.Delete(path);
                    else return Option("ignoreIfNotExists");
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"A refactoring's file {kind} failed: {ex.Message}");
            return false;
        }
    }

    // ── Formatting ───────────────────────────────────────────────────────────────────────────────

    private FormattingOptions FormattingFor(EditorTab tab)
    {
        var options = tab.Editor?.Options ?? Wicked.Editor.Model.EditOptions.Default;
        return new FormattingOptions(options.TabSize, options.InsertSpaces,
            tab.EditorConfig.TrimTrailingWhitespace ?? _settings.TrimTrailingWhitespace,
            tab.EditorConfig.InsertFinalNewline ?? _settings.InsertFinalNewline);
    }

    /// <summary>Format Document (Shift+Alt+F), or the selection (Ctrl+K Ctrl+F) when <paramref name="selection"/>.</summary>
    public async Task<bool> FormatAsync(EditorTab tab, bool selection = false, CancellationToken ct = default)
    {
        if (DocumentFor(tab) is not { } doc) return false;
        if (await doc.Ready is not { } client) return false;
        var capability = selection ? "documentRangeFormattingProvider" : "documentFormattingProvider";
        if (!client.Supports(capability))
        {
            _notices.Post(new Notice($"format:{tab.Path}", $"{client.ServerName} cannot format {(selection ? "a selection" : "this file")}", NoticeKind.Info));
            return false;
        }

        var options = FormattingFor(tab);
        var primary = tab.Document.Primary;
        var range = selection && !primary.IsEmpty ? (primary.Start, primary.End) : ((int, int)?)null;
        var answer = await doc.RequestAsync(async (c, text, version) =>
        {
            var lspRange = range is { } r ? LspText.RangeOf(text, r.Item1, r.Item2) : null;
            return (Edits: await c.FormatAsync(doc.Uri, options, lspRange, ct).ConfigureAwait(false), Version: version);
        }, ct);
        // An edit made while the server formatted wins: its answer is for text that is gone.
        if (answer.Edits is not { Count: > 0 } edits || answer.Version != tab.Document.Version) return false;
        ApplyEdits(tab.Document, edits);
        return true;
    }

    /// <summary>editor.formatOnSave: the file formatted before it is written, given at most a second and a half.</summary>
    private async Task BeforeSaveAsync(EditorTab tab)
    {
        if (!_settings.FormatOnSave || DocumentFor(tab) is null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
        try
        {
            await FormatAsync(tab, ct: cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            Log.Warn($"Format on save skipped for {tab.Title}: {ex.Message}");
        }
    }
}
