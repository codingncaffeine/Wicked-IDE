using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Wicked.App.Shell;
using Wicked.App.ViewModels;
using Wicked.App.Workbench;
using Wicked.Core.Extensions;
using Wicked.Core.Settings;
using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.App.Extensions;

/// <summary>
/// The extension host's window: what <see cref="IHostBridge"/> asks for done on the shell. Every call may come from any
/// thread; the ones that touch the window hop to the interface thread for the moment it takes to read or write a document
/// (an O(1) capture of the text, never a copy of it), and the work around that (building a snapshot string, sorting marks)
/// stays on the caller's worker.
/// </summary>
public sealed class WickedHostBridge : IHostBridge, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Dictionary<(string Document, string Layer), (DecorationSet Set, long Version)> _pending = [];
    private readonly Dictionary<string, HashSet<string>> _layersByDocument = [];
    private readonly Lock _gate = new();
    private string? _activeId;
    private JsonObject? _lastSettings;

    public WickedHostBridge(ShellViewModel shell)
    {
        _shell = shell;
        shell.Workbench.TabOpened += OnTabOpened;
        shell.Workbench.TabClosed += OnTabClosed;
        shell.Workbench.Saved += OnSaved;
        shell.Workbench.ActiveEditorChanged += OnActiveChanged;
        _activeId = shell.Workbench.ActiveTab is { } tab ? IdOf(tab) : null;
    }

    public ExtensionThemeFiles ThemeFiles { get; } = new();

    public void Dispose()
    {
        _shell.Workbench.TabOpened -= OnTabOpened;
        _shell.Workbench.TabClosed -= OnTabClosed;
        _shell.Workbench.Saved -= OnSaved;
        _shell.Workbench.ActiveEditorChanged -= OnActiveChanged;
    }

    private static Task<T> Ui<T>(Func<T> work) =>
        Dispatcher.UIThread.CheckAccess() ? Task.FromResult(work()) : Dispatcher.UIThread.InvokeAsync(work).GetTask();

    private static Task Ui(Action work) => Ui(() =>
    {
        work();
        return 0;
    });

    // ── Commands ──

    public IDisposable AddCommand(string id, string category, string title, string? keys, Func<Task> run)
    {
        var gestures = NormaliseKeys(keys);
        if (keys is not null && gestures is null) WriteOutput(WickedApi.ErrorChannel, $"The keys \"{keys}\" for {id} are not understood; the command has none");
        var command = new WorkbenchCommand(id, category, title, gestures, run);
        var undo = _shell.AddExtensionCommand(command);
        return undo;
    }

    public IDisposable AddKeybinding(string commandId, string keys)
    {
        var gestures = NormaliseKeys(keys) ?? throw new ArgumentException($"The keys \"{keys}\" are not understood", nameof(keys));
        return _shell.AddExtensionBinding(commandId, gestures);
    }

    /// <summary>Keys written as VS Code writes them ("ctrl+alt+h") or as Avalonia does ("Ctrl+Alt+H"), as Avalonia's, or null when they do not parse.</summary>
    internal static string? NormaliseKeys(string? keys)
    {
        if (string.IsNullOrWhiteSpace(keys)) return null;
        var candidate = Keybindings.Translate(keys) ?? keys;
        try
        {
            foreach (var part in candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)) _ = Avalonia.Input.KeyGesture.Parse(part);
            return candidate;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return null;
        }
    }

    public async Task<bool> RunCommandAsync(string id)
    {
        var command = await Ui(() => _shell.Commands.FirstOrDefault(c => c.Id == id)).ConfigureAwait(false);
        if (command is null) return false;
        await command.Run().ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<string> CommandIds() => [.. _shell.Commands.Select(c => c.Id)];

    // ── Output ──

    public void WriteOutput(string channel, string line) => _shell.Output.Write(channel, line);

    public void ClearOutput(string channel)
    {
        var target = _shell.Output.Channel(channel);
        _ = Ui(target.Clear);
    }

    public Task ShowOutputAsync(string channel) => Ui(() => _shell.Navigate("output/" + channel));

    // ── Settings ──

    public event Action<IReadOnlyList<string>>? SettingsChanged;

    public async Task<JsonElement?> GetSettingAsync(string key)
    {
        var settings = _shell.Settings;
        var json = await Task.Run(() => JsonSerializer.SerializeToNode(settings, WickedSettings.Json)?.AsObject()).ConfigureAwait(false);
        return json is not null && json.TryGetPropertyValue(key, out var node) && node is not null ? JsonSerializer.SerializeToElement(node) : null;
    }

    public async Task SetSettingAsync(string key, JsonElement value)
    {
        await _shell.UpdateSettingsAsync(current =>
        {
            var json = JsonSerializer.SerializeToNode(current, WickedSettings.Json)!.AsObject();
            json[key] = JsonNode.Parse(value.GetRawText());
            return json.Deserialize<WickedSettings>(WickedSettings.Json) ?? current;
        }).ConfigureAwait(false);
    }

    /// <summary>Settings were applied: work out which keys changed (on a worker) and tell the extensions.</summary>
    internal void SettingsApplied(WickedSettings settings)
    {
        _ = Task.Run(() =>
        {
            var now = JsonSerializer.SerializeToNode(settings, WickedSettings.Json)!.AsObject();
            List<string> changed;
            lock (_gate)
            {
                var before = _lastSettings;
                _lastSettings = now;
                if (before is null) return;
                changed = [.. now.Select(kv => kv.Key).Union(before.Select(kv => kv.Key)).Where(k => before[k]?.ToJsonString() != now[k]?.ToJsonString())];
            }

            if (changed.Count > 0) SettingsChanged?.Invoke(changed);
        });
    }

    // ── Workspace ──

    public Task<string?> GetRootAsync() => Ui(() => _shell.Workbench.Root);

    public Task OpenFileAsync(string path, int? line, int? column) =>
        Dispatcher.UIThread.InvokeAsync(() => _shell.OpenAtAsync(path, line ?? 0, column ?? 0));

    // ── Editors ──

    private static string IdOf(EditorTab tab) => tab.Path ?? $"untitled:{tab.Title}";

    private readonly record struct Capture(string Id, string? Path, string? Language, long Version, Rope Text, ImmutableArray<Selection> Selections, bool Dirty);

    private static Capture Take(EditorTab tab)
    {
        var doc = tab.Document;
        return new Capture(IdOf(tab), tab.Path, tab.Language?.Id, doc.Version, doc.Text, doc.Selections, doc.IsDirty);
    }

    private static EditorSnapshot Build(Capture c) =>
        new(c.Id, c.Path, c.Language, c.Version, c.Text.ToString(), [.. c.Selections.Select(s => new TextRange(s.Start, s.End))], c.Dirty);

    private EditorTab? FindTab(string id) => _shell.Workbench.AllTabs.FirstOrDefault(t => IdOf(t) == id);

    public async Task<EditorSnapshot?> GetActiveAsync()
    {
        var capture = await Ui(() => _shell.Workbench.ActiveTab is { } tab ? Take(tab) : (Capture?)null).ConfigureAwait(false);
        return capture is { } c ? await Task.Run(() => Build(c)).ConfigureAwait(false) : null;
    }

    public async Task<IReadOnlyList<EditorSnapshot>> GetOpenAsync()
    {
        var captures = await Ui(() => _shell.Workbench.AllTabs.Where(t => t.ViewFactory is null).Select(Take).DistinctBy(c => c.Id).ToList()).ConfigureAwait(false);
        return await Task.Run(() => (IReadOnlyList<EditorSnapshot>)[.. captures.Select(Build)]).ConfigureAwait(false);
    }

    public async Task<EditorSnapshot?> GetDocumentAsync(string documentId)
    {
        var capture = await Ui(() => FindTab(documentId) is { } tab ? Take(tab) : (Capture?)null).ConfigureAwait(false);
        return capture is { } c ? await Task.Run(() => Build(c)).ConfigureAwait(false) : null;
    }

    private static string Normalise(string text) => text.Contains('\r', StringComparison.Ordinal) ? text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : text;

    public Task<bool> ReplaceAsync(string documentId, TextRange range, string text, long? expectedVersion) => Ui(() =>
    {
        if (FindTab(documentId) is not { } tab) return false;
        var doc = tab.Document;
        if (doc.IsReadOnly || (expectedVersion is { } v && v != doc.Version)) return false;
        var start = Math.Clamp(Math.Min(range.Start, range.End), 0, doc.Text.Length);
        var end = Math.Clamp(Math.Max(range.Start, range.End), start, doc.Text.Length);
        var inserted = Normalise(text);
        int Map(int offset) => offset <= start ? offset : offset >= end ? offset - (end - start) + inserted.Length : start + inserted.Length;
        var after = doc.Selections.Select(s => s with { Anchor = Map(s.Anchor), Active = Map(s.Active), Column = -1 }).ToList();
        doc.BreakUndoGroup();
        var changed = doc.Apply([new TextChange(start, end - start, inserted)], after, EditKind.Other, doc.PrimaryIndex);
        doc.BreakUndoGroup();
        return changed;
    });

    public Task<bool> InsertAtCaretsAsync(string text) => Ui(() =>
    {
        if (_shell.Workbench.ActiveTab?.Document is not { IsReadOnly: false } doc) return false;
        var inserted = Normalise(text);
        var changes = new List<TextChange>();
        var after = new List<Selection>();
        var shift = 0;
        foreach (var s in doc.Selections)
        {
            changes.Add(new TextChange(s.Start, s.Length, inserted));
            after.Add(Selection.Caret(s.Start + shift + inserted.Length));
            shift += inserted.Length - s.Length;
        }

        doc.BreakUndoGroup();
        var changed = doc.Apply(changes, after, EditKind.Other, doc.PrimaryIndex);
        doc.BreakUndoGroup();
        return changed;
    });

    public Task SetSelectionsAsync(string documentId, IReadOnlyList<TextRange> selections) => Ui(() =>
    {
        if (selections.Count == 0 || FindTab(documentId) is not { } tab) return;
        tab.Document.SetSelections([.. selections.Select(r => new Selection(r.Start, r.End))], 0);
    });

    private static string Layer(string owner, string layer) => $"ext:{owner}:{layer}";

    public async Task SetDecorationsAsync(string owner, string documentId, string layer, Decorations marks, long? forVersion)
    {
        var capture = await Ui(() => FindTab(documentId) is { } tab ? Take(tab) : (Capture?)null).ConfigureAwait(false);
        if (capture is not { } c) return;
        var name = Layer(owner, layer);
        var version = forVersion ?? c.Version;
        var set = await Task.Run(() => ToSet(marks, c.Text)).ConfigureAwait(false);
        lock (_gate)
        {
            _pending[(documentId, name)] = (set, version);
            if (!_layersByDocument.TryGetValue(documentId, out var layers)) _layersByDocument[documentId] = layers = [];
            layers.Add(name);
        }

        await Ui(() => ApplyDecorations(documentId, name, set, version)).ConfigureAwait(false);
    }

    private void ApplyDecorations(string documentId, string layer, DecorationSet set, long? version)
    {
        foreach (var tab in _shell.Workbench.AllTabs.Where(t => IdOf(t) == documentId)) tab.Editor?.SetDecorations(layer, set, version ?? tab.Document.Version);
    }

    private static DecorationSet ToSet(Decorations marks, Rope text)
    {
        static RangeStyle Style(MarkStyle s) => s switch
        {
            MarkStyle.Error => RangeStyle.Error,
            MarkStyle.Warning => RangeStyle.Warning,
            MarkStyle.Info => RangeStyle.Info,
            MarkStyle.Hint => RangeStyle.Hint,
            MarkStyle.Unnecessary => RangeStyle.Unnecessary,
            MarkStyle.Deprecated => RangeStyle.Deprecated,
            MarkStyle.Highlight => RangeStyle.Highlight,
            MarkStyle.Inserted => RangeStyle.Inserted,
            MarkStyle.Removed => RangeStyle.Removed,
            _ => RangeStyle.Link,
        };
        int Clamp(int offset) => Math.Clamp(offset, 0, text.Length);
        var ranges = (marks.Ranges ?? []).Select(r => new RangeDecoration(Clamp(Math.Min(r.Start, r.End)), Clamp(Math.Max(r.Start, r.End)), Style(r.Style)));
        var lines = (marks.Lines ?? []).Select(l => new LineDecoration(DecorationSet.LineKey(text, l.Line), l.Style switch
        {
            LineMarkStyle.Inserted => LineStyle.Inserted,
            LineMarkStyle.Removed => LineStyle.Removed,
            _ => LineStyle.ConflictCurrent,
        }));
        var gutter = (marks.Gutter ?? []).Select(g => new GutterDecoration(DecorationSet.LineKey(text, g.Line), g.Kind switch
        {
            GutterMarkKind.Added => GutterGlyph.Added,
            GutterMarkKind.Modified => GutterGlyph.Modified,
            GutterMarkKind.Deleted => GutterGlyph.Deleted,
            _ => GutterGlyph.Bookmark,
        }));
        var inline = (marks.Inline ?? []).Select(i => new InlineDecoration(Clamp(i.Offset), i.Text, i.Style == InlineMarkStyle.Ghost ? InlineStyle.Ghost : InlineStyle.Hint));
        return new DecorationSet(ranges, lines, gutter, inline);
    }

    public Task ClearDecorationsAsync(string owner, string documentId, string? layer)
    {
        var prefix = $"ext:{owner}:";
        List<(string Document, string Layer)> doomed;
        lock (_gate)
        {
            doomed = [.. _pending.Keys.Where(k => (documentId == "*" || k.Document == documentId) && (layer is null ? k.Layer.StartsWith(prefix, StringComparison.Ordinal) : k.Layer == Layer(owner, layer)))];
            foreach (var key in doomed) _pending.Remove(key);
        }

        return Ui(() =>
        {
            foreach (var (document, name) in doomed) ApplyDecorations(document, name, DecorationSet.Empty, null);
        });
    }

    // ── Events ──

    public event Action<EditorSnapshot>? DocumentOpened;

    public event Action<EditorSnapshot>? DocumentSaved;

    public event Action<EditorSnapshot>? DocumentClosed;

    public event Action<EditorSnapshot?>? ActiveEditorChanged;

    private void Raise(Capture capture, Action<EditorSnapshot>? handlers)
    {
        if (handlers is null) return;
        _ = Task.Run(() => handlers(Build(capture)));
    }

    private void OnTabOpened(EditorTab tab)
    {
        if (tab.ViewFactory is not null) return;
        tab.EditorCreated += editor =>
        {
            // The editor exists now: marks that arrived before it did are drawn.
            List<KeyValuePair<(string Document, string Layer), (DecorationSet Set, long Version)>> waiting;
            lock (_gate) waiting = [.. _pending.Where(p => p.Key.Document == IdOf(tab))];
            foreach (var p in waiting) editor.SetDecorations(p.Key.Layer, p.Value.Set, p.Value.Version);
        };
        // The same document in a second view (a split) is not opened again.
        if (_shell.Workbench.AllTabs.Any(t => !ReferenceEquals(t, tab) && ReferenceEquals(t.Document, tab.Document))) return;
        Raise(Take(tab), DocumentOpened);
    }

    private void OnTabClosed(EditorTab tab)
    {
        if (tab.ViewFactory is not null) return;
        if (_shell.Workbench.AllTabs.Any(t => ReferenceEquals(t.Document, tab.Document))) return;
        var capture = Take(tab);
        lock (_gate)
        {
            foreach (var key in _pending.Keys.Where(k => k.Document == capture.Id).ToList()) _pending.Remove(key);
            _layersByDocument.Remove(capture.Id);
        }

        Raise(capture, DocumentClosed);
    }

    private void OnSaved(EditorTab tab)
    {
        if (tab.ViewFactory is null) Raise(Take(tab), DocumentSaved);
    }

    private void OnActiveChanged()
    {
        var tab = _shell.Workbench.ActiveTab is { ViewFactory: null } t ? t : null;
        var id = tab is null ? null : IdOf(tab);
        lock (_gate)
        {
            if (id == _activeId) return;
            _activeId = id;
        }

        var handlers = ActiveEditorChanged;
        if (handlers is null) return;
        var capture = tab is null ? (Capture?)null : Take(tab);
        _ = Task.Run(() => handlers(capture is { } c ? Build(c) : null));
    }

    // ── Panels ──

    private static string PanelId(string owner, string id) => $"ext:{owner}:{id}";

    private static Control AsControl(object created, string id) =>
        created as Control ?? throw new InvalidOperationException($"The view for \"{id}\" must be an Avalonia Control, not {created.GetType().Name}");

    public Task AddBottomTabAsync(string owner, string id, string title, Func<object> createView) => Ui(() =>
    {
        var full = PanelId(owner, id);
        _shell.BottomPanel.Add(full, title.ToUpperInvariant(), () => AsControl(createView(), id));
    });

    public Task AddSidePanelAsync(string owner, string id, string title, string icon, Func<object> createView) => Ui(() =>
    {
        var full = PanelId(owner, id);
        if (_shell.CustomPanels.Any(p => p.Id == full)) return;
        var key = "Icon." + string.Concat(icon.Split('-', ' ').Where(p => p.Length > 0).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        if (Avalonia.Application.Current?.TryFindResource(key, out _) != true) key = "Icon.Puzzle";
        _shell.CustomPanels.Add(new CustomPanel(full, owner, title, key, () => AsControl(createView(), id)));
    });

    public Task ShowPanelAsync(string owner, string id) => Ui(() =>
    {
        var full = PanelId(owner, id);
        if (_shell.BottomPanel[full] is not null) _shell.BottomPanel.Show(full);
        else if (_shell.CustomPanels.FirstOrDefault(p => p.Id == full) is { } panel) _shell.ShowCustomPanel(panel);
    });

    public Task RemovePanelAsync(string owner, string id) => Ui(() =>
    {
        var full = PanelId(owner, id);
        _shell.BottomPanel.Remove(full);
        _shell.RemoveCustomPanel(full);
    });

    // ── Registries ──

    public IDisposable AddSnippetFile(string path, string? languageId) => _shell.Snippets.AddFile(path, languageId);

    public IDisposable AddThemeFile(string path, string? label) => ThemeFiles.Add(path, label);

    public IDisposable AddGrammar(GrammarSpec grammar) =>
        Wicked.Editor.Syntax.AddedGrammars.Add(grammar.LanguageId, grammar.Scope, grammar.Path, grammar.Extensions, grammar.FileNames, grammar.Configuration);

    private sealed class Undo(Action undo) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) undo();
        }
    }
}
