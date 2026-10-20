using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using Wicked.Core;
using Wicked.Core.Files;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.App.Workbench;

/// <summary>What the user chose when asked about unsaved changes.</summary>
public enum SaveChoice
{
    Save,
    DontSave,
    Cancel,
}

/// <summary>The dialogs the workbench needs; the window provides them.</summary>
public interface IWorkbenchDialogs
{
    Task<SaveChoice> AskSaveAsync(string message, string detail);

    Task<string?> PickFolderAsync();

    Task<IReadOnlyList<string>> PickFilesAsync();

    Task<string?> PickSaveFileAsync(string suggestedName);
}

/// <summary>
/// The open folder, its files, and the editor groups with their tabs. Lives on the UI thread; every file read, write,
/// listing and scan is awaited from a worker, so nothing here waits on the disk.
/// </summary>
public sealed partial class Workbench : ObservableObject, IDisposable
{
    private readonly NoticeBoard _notices;
    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _changedPaths = [];
    private DispatcherTimer? _settle;
    private CancellationTokenSource? _indexing;

    public Workbench(NoticeBoard notices)
    {
        _notices = notices;
        var first = new EditorGroup(this);
        Groups.Add(first);
        ActiveGroup = first;
        first.IsFocused = true;
    }

    public IWorkbenchDialogs? Dialogs { get; set; }

    public ObservableCollection<EditorGroup> Groups { get; } = [];

    [ObservableProperty]
    private EditorGroup _activeGroup;

    [ObservableProperty]
    private string? _root;

    [ObservableProperty]
    private ExplorerNode? _explorerRoot;

    /// <summary>The workspace's files for quick open, rebuilt on a worker after changes.</summary>
    public FileIndex? Index { get; private set; }

    public EditorTab? ActiveTab => ActiveGroup.Active;

    public IEnumerable<EditorTab> AllTabs => Groups.SelectMany(g => g.Tabs);

    /// <summary>Raised when the active tab, or the active tab's carets, change (the status bar listens).</summary>
    public event Action? ActiveEditorChanged;

    internal void ActiveTabChanged()
    {
        OnPropertyChanged(nameof(ActiveTab));
        ActiveEditorChanged?.Invoke();
    }

    public void Focus(EditorGroup group)
    {
        foreach (var g in Groups) g.IsFocused = g == group;
        ActiveGroup = group;
        ActiveTabChanged();
    }

    // ── Folders ──────────────────────────────────────────────────────────────────────────────────

    public async Task OpenFolderAsync(string folder)
    {
        folder = Path.GetFullPath(folder);
        if (!Directory.Exists(folder)) return;
        Root = folder;
        var node = new ExplorerNode(Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)), folder, true, null);
        ExplorerRoot = node;
        node.IsExpanded = true;
        Watch(folder);
        await RebuildIndexAsync();
    }

    private async Task RebuildIndexAsync()
    {
        if (Root is not { } root) return;
        _indexing?.Cancel();
        var cts = _indexing = new CancellationTokenSource();
        try
        {
            Index = await Task.Run(() => FileIndex.Build(root, cts.Token), cts.Token);
            Log.Info($"Indexed {Index.Files.Length} files under {root}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Watches the folder; changes are gathered for a quarter second, then acted on together.</summary>
    private void Watch(string folder)
    {
        _watcher?.Dispose();
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, e) => Changed(e.FullPath);
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Changed += changed;
            _watcher.Renamed += (_, e) =>
            {
                Changed(e.OldFullPath);
                Changed(e.FullPath);
            };
            _watcher.Error += (_, e) => Log.Warn($"Watching {folder} failed: {e.GetException().Message}");
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Cannot watch {folder}: {ex.Message}");
            _notices.Post(new Notice("watch", "Wicked cannot watch this folder for changes; refresh by reopening it", NoticeKind.Warning));
        }
    }

    // On the watcher's thread: note the path, settle on the UI thread.
    private void Changed(string path)
    {
        if (path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return;
        lock (_changedPaths) _changedPaths.Add(path);
        Dispatcher.UIThread.Post(() =>
        {
            _settle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => _ = SettleAsync());
            _settle.Stop();
            _settle.Start();
        });
    }

    private async Task SettleAsync()
    {
        _settle?.Stop();
        string[] paths;
        lock (_changedPaths)
        {
            paths = [.. _changedPaths];
            _changedPaths.Clear();
        }

        if (ExplorerRoot is { } root)
        {
            foreach (var folder in paths.Select(p => Path.GetDirectoryName(p)!).Distinct())
            {
                if (root.Find(folder) is { } node) await node.RefreshAsync();
            }
        }

        foreach (var tab in AllTabs.Where(t => t.Path is not null && paths.Contains(t.Path)).ToList()) await ReloadIfChangedAsync(tab);
        await RebuildIndexAsync();
    }

    /// <summary>A file changed on disk: an unedited tab follows it; an edited one keeps the user's text and says so.</summary>
    private async Task ReloadIfChangedAsync(EditorTab tab)
    {
        var path = tab.Path!;
        if (!File.Exists(path)) return;
        var stamp = await Task.Run(() => File.GetLastWriteTimeUtc(path));
        if (stamp == tab.LastWriteUtc) return;
        if (tab.IsDirty)
        {
            _notices.Post(new Notice($"conflict:{path}", $"{tab.Title} changed on disk while you were editing it", NoticeKind.Warning, $"tab/{path}"));
            return;
        }

        var loaded = await TextFile.ReadAsync(path, true, CancellationToken.None);
        if (loaded.Kind != FileKind.Text) return;
        var (rope, lineBreak) = await Task.Run(() => TextLoading.Normalise(loaded.Text));
        tab.Document.LineBreak = lineBreak;
        tab.Document.ReplaceAll(rope);
        tab.Document.MarkSaved();
        tab.LastWriteUtc = loaded.LastWriteUtc;
    }

    // ── Opening ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Opens a file in the active group (or brings its tab forward); <paramref name="preview"/> for a single click in the explorer.</summary>
    public async Task<EditorTab?> OpenAsync(string path, bool preview = false, EditorGroup? group = null)
    {
        group ??= ActiveGroup;
        path = Path.GetFullPath(path);
        if (group.Tabs.FirstOrDefault(t => t.Path == path) is { } open)
        {
            if (!preview) open.IsPreview = false;
            group.Activate(open);
            return open;
        }

        LoadedText loaded;
        try
        {
            loaded = await TextFile.ReadAsync(path, false, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notices.Post(new Notice($"open:{path}", $"{Path.GetFileName(path)} could not be opened: {ex.Message}", NoticeKind.Error));
            return null;
        }

        if (loaded.Kind == FileKind.Binary)
        {
            _notices.Post(new Notice($"open:{path}", $"{Path.GetFileName(path)} is a binary file and is not opened as text", NoticeKind.Info));
            return null;
        }

        if (loaded.Kind == FileKind.TooLarge)
        {
            var choice = Dialogs is null ? SaveChoice.Cancel : await Dialogs.AskSaveAsync(
                $"{Path.GetFileName(path)} is {loaded.Length / (1024 * 1024)} MB", "Large files take more memory. Open it anyway?");
            if (choice != SaveChoice.Save) return null;
            loaded = await TextFile.ReadAsync(path, true, CancellationToken.None);
        }

        var (document, language) = await Task.Run(() =>
        {
            var (rope, lineBreak) = TextLoading.Normalise(loaded.Text);
            return (new Document(rope, lineBreak), Languages.ForFile(path));
        });

        var tab = new EditorTab(path, document, language, loaded.Encoding, loaded.LastWriteUtc) { IsPreview = preview };
        group.Add(tab);
        return tab;
    }

    public void NewFile()
    {
        var tab = new EditorTab(null, new Document(Rope.Empty), null, FileEncoding.Utf8, default);
        ActiveGroup.Add(tab);
    }

    public bool OpenRoute(string route)
    {
        if (!route.StartsWith("tab/", StringComparison.Ordinal)) return false;
        var path = route[4..];
        foreach (var group in Groups)
        {
            if (group.Tabs.FirstOrDefault(t => t.Path == path) is { } tab)
            {
                group.Activate(tab);
                return true;
            }
        }

        return false;
    }

    // ── Saving and closing ───────────────────────────────────────────────────────────────────────

    /// <summary>Saves a tab; one never saved asks for a name. Returns false when nothing was saved.</summary>
    public async Task<bool> SaveAsync(EditorTab tab, WickedSettingsView? settings = null)
    {
        if (tab.Path is null) return await SaveAsAsync(tab, settings);
        var doc = tab.Document;
        if (settings?.TrimTrailingWhitespace == true) Edits.TrimTrailingWhitespace(doc);
        if (settings?.InsertFinalNewline == true && doc.Text.Length > 0 && doc.Text[doc.Text.Length - 1] != '\n')
        {
            var keep = doc.Selections;
            doc.Apply([new TextChange(doc.Text.Length, 0, "\n")], keep);
        }

        // The text is captured now; edits made while it is written count as unsaved.
        var snapshot = new Document(doc.Text, doc.LineBreak);
        var path = tab.Path;
        var text = await Task.Run(() => Edits.ForSaving(snapshot));
        try
        {
            tab.LastWriteUtc = await TextFile.WriteAsync(path, text, tab.Encoding, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notices.Post(new Notice($"save:{path}", $"{tab.Title} could not be saved: {ex.Message}", NoticeKind.Error, $"tab/{path}"));
            return false;
        }

        if (ReferenceEquals(doc.Text, snapshot.Text)) doc.MarkSaved();
        _notices.Clear($"conflict:{path}");
        _notices.Post(new Notice($"saved:{path}", $"Saved {tab.Title}", NoticeKind.Success));
        return true;
    }

    public async Task<bool> SaveAsAsync(EditorTab tab, WickedSettingsView? settings = null)
    {
        if (Dialogs is null) return false;
        var path = await Dialogs.PickSaveFileAsync(tab.Title);
        if (path is null) return false;
        tab.Renamed(path);
        return await SaveAsync(tab, settings);
    }

    /// <summary>Closes a tab, asking about unsaved changes. Returns false when the user cancelled.</summary>
    public async Task<bool> CloseAsync(EditorTab tab, EditorGroup group)
    {
        if (tab.IsDirty)
        {
            var choice = Dialogs is null ? SaveChoice.Cancel : await Dialogs.AskSaveAsync($"Do you want to save the changes to {tab.Title}?", "Your changes will be lost if you don't save them.");
            if (choice == SaveChoice.Cancel) return false;
            if (choice == SaveChoice.Save && !await SaveAsync(tab)) return false;
        }

        group.Remove(tab);
        if (group.Tabs.Count == 0 && Groups.Count > 1)
        {
            Groups.Remove(group);
            Focus(Groups[^1]);
        }

        ActiveTabChanged();
        return true;
    }

    /// <summary>Before the window closes: one question for every unsaved tab. False when the user cancelled.</summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        var dirty = AllTabs.Where(t => t.IsDirty).ToList();
        if (dirty.Count == 0) return true;
        if (Dialogs is null) return false;
        var names = string.Join(", ", dirty.Take(5).Select(t => t.Title)) + (dirty.Count > 5 ? $" and {dirty.Count - 5} more" : "");
        var choice = await Dialogs.AskSaveAsync($"Do you want to save the changes to {dirty.Count} file{(dirty.Count == 1 ? "" : "s")}?", names);
        if (choice == SaveChoice.Cancel) return false;
        if (choice == SaveChoice.Save)
        {
            foreach (var tab in dirty)
            {
                if (!await SaveAsync(tab)) return false;
            }
        }

        return true;
    }

    // ── Groups ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+\: a new group to the right, showing the same document.</summary>
    public void SplitRight()
    {
        var group = new EditorGroup(this);
        Groups.Insert(Groups.IndexOf(ActiveGroup) + 1, group);
        if (ActiveTab is { } tab)
        {
            var copy = new EditorTab(tab.Path, tab.Document, tab.Language, tab.Encoding, tab.LastWriteUtc);
            group.Add(copy);
        }

        Focus(group);
    }

    public void FocusGroup(int index)
    {
        if (index < Groups.Count) Focus(Groups[index]);
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _indexing?.Cancel();
    }
}

/// <summary>The save-time settings the workbench reads (kept narrow so tests need no full settings object).</summary>
public sealed record WickedSettingsView(bool TrimTrailingWhitespace, bool InsertFinalNewline);
