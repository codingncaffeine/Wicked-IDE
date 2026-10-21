using Wicked.Core;
using Wicked.Core.Files;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.App.Workbench;

/// <summary>
/// The File and tab basics: closing all, others, saved or those to the right, Reopen Closed Editor, Revert File, and Change
/// Language Mode. Questions about unsaved files are asked as one close asks them.
/// </summary>
public sealed partial class Workbench
{
    private const int ClosedLimit = 20;
    private readonly List<string> _closedPaths = [];

    /// <summary>A tab's language was changed (Change Language Mode): the language servers follow it.</summary>
    public event Action<EditorTab>? TabLanguageChanged;

    /// <summary>Remembers a closed file for Reopen Closed Editor (newest last).</summary>
    private void RememberClosed(EditorTab tab)
    {
        if (tab.Path is not { } path || tab.ViewFactory is not null) return;
        _closedPaths.Remove(path);
        _closedPaths.Add(path);
        if (_closedPaths.Count > ClosedLimit) _closedPaths.RemoveAt(0);
    }

    /// <summary>Ctrl+Shift+T: the file closed last (and still there, and not open again) opens again.</summary>
    public async Task ReopenClosedAsync()
    {
        while (_closedPaths.Count > 0)
        {
            var path = _closedPaths[^1];
            _closedPaths.RemoveAt(_closedPaths.Count - 1);
            if (AllTabs.Any(t => t.Path == path) || !await Task.Run(() => File.Exists(path))) continue;
            await OpenAsync(path);
            return;
        }
    }

    /// <summary>Closes the tabs <paramref name="which"/> picks from a group, stopping when the user cancels a question.</summary>
    private async Task CloseWhereAsync(EditorGroup group, Func<EditorTab, int, bool> which)
    {
        var tabs = group.Tabs.Select((tab, index) => (tab, index)).Where(t => which(t.tab, t.index)).Select(t => t.tab).ToList();
        foreach (var tab in tabs)
        {
            if (!await CloseAsync(tab, group)) return;
        }
    }

    public Task CloseOthersAsync(EditorTab keep, EditorGroup group) => CloseWhereAsync(group, (tab, _) => tab != keep);

    public Task CloseToTheRightAsync(EditorTab from, EditorGroup group)
    {
        var at = group.Tabs.IndexOf(from);
        return CloseWhereAsync(group, (_, index) => index > at);
    }

    /// <summary>Close Saved Editors (Ctrl+K U): every tab without unsaved changes.</summary>
    public Task CloseSavedAsync(EditorGroup group) => CloseWhereAsync(group, (tab, _) => !tab.IsDirty);

    /// <summary>Close All Editors (Ctrl+K Ctrl+W), in every group.</summary>
    public async Task CloseAllAsync()
    {
        foreach (var group in Groups.ToList())
        {
            await CloseWhereAsync(group, (_, _) => true);
            if (group.Tabs.Count > 0) return; // a question was cancelled
        }
    }

    /// <summary>Revert File: the file's text on disk replaces the tab's, unsaved changes dropped (undo brings them back).</summary>
    public async Task RevertAsync(EditorTab tab)
    {
        if (tab.Path is not { } path) return;
        LoadedText loaded;
        try
        {
            loaded = await TextFile.ReadAsync(path, true, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notices.Post(new Notice($"revert:{path}", $"{tab.Title} could not be read again: {ex.Message}", NoticeKind.Error));
            return;
        }

        if (loaded.Kind != FileKind.Text) return;
        var (rope, lineBreak) = await Task.Run(() => TextLoading.Normalise(loaded.Text));
        tab.Document.LineBreak = lineBreak;
        tab.Document.ReplaceAll(rope);
        tab.Document.MarkSaved();
        tab.LastWriteUtc = loaded.LastWriteUtc;
    }

    /// <summary>Reopen with Encoding: the file read again as <paramref name="encoding"/> (its byte-order mark skipped), unsaved changes dropped.</summary>
    public async Task ReopenWithEncodingAsync(EditorTab tab, FileEncoding encoding)
    {
        if (tab.Path is not { } path) return;
        string text;
        try
        {
            text = await Task.Run(async () =>
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var preamble = encoding.Encoding.GetPreamble();
                var skip = preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
                return encoding.Encoding.GetString(bytes, skip, bytes.Length - skip);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notices.Post(new Notice($"encoding:{path}", $"{tab.Title} could not be read again: {ex.Message}", NoticeKind.Error));
            return;
        }

        var (rope, lineBreak) = await Task.Run(() => TextLoading.Normalise(text));
        tab.Encoding = encoding;
        tab.Document.LineBreak = lineBreak;
        tab.Document.ReplaceAll(rope);
        tab.Document.MarkSaved();
    }

    /// <summary>The file's text as saved on disk ("" when it is new), for Compare with Saved.</summary>
    public static async Task<string> SavedTextAsync(string path)
    {
        if (!File.Exists(path)) return "";
        var loaded = await TextFile.ReadAsync(path, true, CancellationToken.None);
        return loaded.Kind == FileKind.Text ? TextLoading.Normalise(loaded.Text).Text.ToString() : "";
    }

    /// <summary>Close Folder (Ctrl+K F): every tab closed (asking about unsaved ones), then no folder. False when cancelled.</summary>
    public async Task<bool> CloseFolderAsync()
    {
        await CloseAllAsync();
        if (AllTabs.Any()) return false;
        _watcher?.Dispose();
        _watcher = null;
        Root = null;
        ExplorerRoot = null;
        Index = null;
        OnPropertyChanged(nameof(Index));
        return true;
    }

    /// <summary>Change Language Mode: the tab takes <paramref name="language"/>; its editor is made again in it.</summary>
    public void ChangeLanguage(EditorTab tab, LanguageInfo? language)
    {
        tab.ChangeLanguage(language);
        TabLanguageChanged?.Invoke(tab);
        foreach (var group in Groups.Where(g => g.Active == tab)) group.Reshow();
        ActiveEditorChanged?.Invoke();
    }
}
