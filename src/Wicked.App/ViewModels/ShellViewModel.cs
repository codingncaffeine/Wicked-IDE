using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Editor;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Settings;
using Wicked.Editor.Model;
using Wicked.App.Workbench;
using Bench = Wicked.App.Workbench.Workbench;

namespace Wicked.App.ViewModels;

/// <summary>The side panels the rail opens.</summary>
public enum SidePanel
{
    Explorer,
    Search,
    SourceControl,
    Models,
}

/// <summary>
/// The window's frame: which side panel is open, whether the assistant is showing, the footer's notice and the daemon's
/// state. Work that waits on anything (the daemon, the disk) runs on the thread pool and posts its result here.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<Action> _post;
    private readonly TimeSpan _daemonPoll;

    public ShellViewModel(WickedPaths paths, NoticeBoard? notices = null, Action<Action>? post = null, TimeSpan? daemonPoll = null)
    {
        Paths = paths;
        Notices = notices ?? new NoticeBoard();
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _daemonPoll = daemonPoll ?? TimeSpan.FromSeconds(5);
        Notices.Changed += () => _post(RefreshNotice);
        Workbench = new Bench(Notices);
        Workbench.ActiveEditorChanged += OnActiveEditorChanged;
        Workbench.FolderOpened += folder => _ = RememberFolderAsync(folder);
        Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Workbench.Root)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(WorkspaceName));
            OnPropertyChanged(nameof(CommandCentreText));
        };
        Commands = BuildCommands();
        QuickInput = new QuickInputViewModel(this);
        Models = new ModelsViewModel(paths, Notices);
        Snippets = new SnippetLibrary(Path.Combine(paths.Config, "snippets"));
        Workbench.Snippets = Snippets;
        Models.RouteRequested += route => Navigate(route);
        SettingsSheet = new SettingsViewModel(this);
        Keys = new KeyDispatcher(Commands);
        _ = Task.Run(() => WatchDaemonAsync(_stop.Token));
    }

    public Bench Workbench { get; }

    public IReadOnlyList<WorkbenchCommand> Commands { get; }

    public QuickInputViewModel QuickInput { get; }

    public ModelsViewModel Models { get; }

    public SettingsViewModel SettingsSheet { get; }

    public KeyDispatcher Keys { get; private set; }

    public WickedSettings Settings { get; private set; } = new();

    /// <summary>A notice's route to a file that is not open yet (settings.json with an error) opens it.</summary>
    private async Task OpenFileRouteAsync(string path)
    {
        if (File.Exists(path)) await Workbench.OpenAsync(path);
    }

    private SessionState _session = new();

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────

    private List<WorkbenchCommand> BuildCommands()
    {
        var bench = Workbench;
        Task Sync(Action a)
        {
            a();
            return Task.CompletedTask;
        }

        WickedSettingsView SaveSettings() => new(Settings.TrimTrailingWhitespace, Settings.InsertFinalNewline);
        return
        [
            new("workbench.action.files.newUntitledFile", "File", "New Text File", "Ctrl+N", () => Sync(bench.NewFile)),
            new("workbench.action.files.openFile", "File", "Open File...", "Ctrl+O", OpenFilesAsync),
            new("workbench.action.files.openFolder", "File", "Open Folder...", "Ctrl+K Ctrl+O", OpenFolderAsync),
            new("workbench.action.files.save", "File", "Save", "Ctrl+S", () => bench.ActiveTab is { } t ? bench.SaveAsync(t, SaveSettings()) : Task.CompletedTask),
            new("workbench.action.files.saveAs", "File", "Save As...", "Ctrl+Shift+S", () => bench.ActiveTab is { } t ? bench.SaveAsAsync(t, SaveSettings()) : Task.CompletedTask),
            new("workbench.action.files.saveAll", "File", "Save All", "Ctrl+K S", async () =>
            {
                foreach (var t in bench.AllTabs.Where(t => t.IsDirty).ToList()) await bench.SaveAsync(t, SaveSettings());
            }),
            new("workbench.action.closeActiveEditor", "View", "Close Editor", "Ctrl+W", () => bench.ActiveTab is { } t ? bench.CloseAsync(t, bench.ActiveGroup) : Task.CompletedTask),
            new("workbench.action.splitEditor", "View", "Split Editor Right", "Ctrl+OemPipe", () => Sync(bench.SplitRight)),
            new("workbench.action.focusFirstEditorGroup", "View", "Focus First Editor Group", "Ctrl+D1", () => Sync(() => bench.FocusGroup(0))),
            new("workbench.action.focusSecondEditorGroup", "View", "Focus Second Editor Group", "Ctrl+D2", () => Sync(() => bench.FocusGroup(1))),
            new("workbench.action.focusThirdEditorGroup", "View", "Focus Third Editor Group", "Ctrl+D3", () => Sync(() => bench.FocusGroup(2))),
            new("workbench.action.nextEditor", "View", "Next Editor", "Ctrl+PageDown", () => Sync(() => bench.ActiveGroup.Cycle(1))),
            new("workbench.action.previousEditor", "View", "Previous Editor", "Ctrl+PageUp", () => Sync(() => bench.ActiveGroup.Cycle(-1))),
            new("workbench.action.quickOpenNavigateNext", "View", "Switch Editor", "Ctrl+Tab", () => Sync(() => bench.ActiveGroup.Cycle(1))),
            new("workbench.action.toggleSidebarVisibility", "View", "Toggle Side Bar", "Ctrl+B", () => Sync(ToggleSidebar)),
            new("wicked.toggleAssistant", "View", "Toggle Assistant", "Ctrl+L", () => Sync(ToggleAssistant)),
            new("workbench.view.explorer", "View", "Show Explorer", "Ctrl+Shift+E", () => Sync(() => Navigate(ShellRoutes.Explorer))),
            new("workbench.view.search", "View", "Show Search", "Ctrl+Shift+F", () => Sync(() => Navigate(ShellRoutes.Search))),
            new("workbench.view.scm", "View", "Show Source Control", "Ctrl+Shift+G", () => Sync(() => Navigate(ShellRoutes.SourceControl))),
            new("wicked.showModels", "View", "Show Models", "Ctrl+Shift+M", () => Sync(() => Navigate(ShellRoutes.Models))),
            new("workbench.action.showCommands", "View", "Show All Commands", "Ctrl+Shift+P", () => Sync(() => OpenPalette(">"))),
            new("workbench.action.quickOpen", "Go", "Go to File...", "Ctrl+P", () => Sync(() => OpenPalette(""))),
            new("workbench.action.gotoLine", "Go", "Go to Line...", "Ctrl+G", () => Sync(() => OpenPalette(":"))),
            new("editor.action.fontZoomIn", "View", "Zoom In", "Ctrl+OemPlus", () => UpdateSettingsAsync(st => st with { FontSize = Math.Min(72, st.FontSize + 1) })),
            new("editor.action.fontZoomOut", "View", "Zoom Out", "Ctrl+OemMinus", () => UpdateSettingsAsync(st => st with { FontSize = Math.Max(6, st.FontSize - 1) })),
            new("editor.action.fontZoomReset", "View", "Reset Zoom", "Ctrl+D0", () => UpdateSettingsAsync(st => st with { FontSize = new WickedSettings().FontSize })),
            new("workbench.action.openSettings", "Preferences", "Open Settings", "Ctrl+OemComma", () => Sync(() => SettingsOpen = true)),
            new("workbench.action.openSettingsJson", "Preferences", "Open Settings (JSON)", null, OpenSettingsJsonAsync),
            new("workbench.action.selectTheme", "Preferences", "Code Colours", "Ctrl+K Ctrl+T", () => Sync(PickCodeColours)),
            new("editor.foldAll", "View", "Fold All", "Ctrl+K Ctrl+D0", () => Sync(() => Workbench.ActiveTab?.Editor?.FoldAll())),
            new("editor.unfoldAll", "View", "Unfold All", "Ctrl+K Ctrl+J", () => Sync(() => Workbench.ActiveTab?.Editor?.UnfoldAll())),
            new("editor.action.toggleWordWrap", "View", "Toggle Word Wrap", "Alt+Z", () => Sync(() => Workbench.ActiveTab?.Editor?.ToggleWordWrap())),
            new("editor.action.insertSnippet", "Snippets", "Insert Snippet", null, () => Sync(PickSnippet)),
            new("workbench.action.openSnippets", "Snippets", "Configure Snippets", null, ConfigureSnippetsAsync),
            new("workbench.action.openGlobalKeybindingsFile", "Preferences", "Open Keyboard Shortcuts (JSON)", null, OpenKeybindingsJsonAsync),
        ];
    }

    // ── Settings, live ───────────────────────────────────────────────────────────────────────────

    /// <summary>The Settings sheet (Ctrl+,).</summary>
    [ObservableProperty]
    private bool _settingsOpen;

    [RelayCommand]
    private void CloseSettings() => SettingsOpen = false;

    [RelayCommand]
    private void OpenSettings() => SettingsOpen = true;

    private string? _lastWritten;
    private FileSystemWatcher? _settingsWatcher;
    private DispatcherTimer? _settingsSettle;
    private DispatcherTimer? _keysSettle;

    /// <summary>Puts settings in force everywhere: the editor services (fonts, code colours), every editor, the workbench.</summary>
    public async Task ApplySettingsAsync(WickedSettings settings)
    {
        Settings = settings;
        Workbench.Settings = settings;
        Models.Configure(settings);
        OnPropertyChanged(nameof(Settings));
        var services = await EditorServices.Ready;
        services.Apply(settings);
        foreach (var tab in Workbench.AllTabs) tab.Editor?.ApplySettings(settings);
    }

    /// <summary>A change from the Settings sheet or a command: applied now, written to settings.json on a worker.</summary>
    public async Task UpdateSettingsAsync(Func<WickedSettings, WickedSettings> change)
    {
        var next = change(Settings);
        if (next == Settings) return;
        await ApplySettingsAsync(next);
        var text = System.Text.Json.JsonSerializer.Serialize(next, WickedSettings.Json) + "\n";
        _lastWritten = text;
        try
        {
            await Task.Run(() => Wicked.Core.Files.TextFile.WriteAsync(Paths.SettingsFile, text, Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Notices.Post(new Notice("settings", $"Settings could not be saved: {ex.Message}", NoticeKind.Error));
        }
    }

    /// <summary>settings.json edited by hand (the code layer, D16): read again a moment after it changes, and applied.</summary>
    private void WatchSettings()
    {
        _settingsWatcher?.Dispose();
        try
        {
            Directory.CreateDirectory(Paths.Config);
            _settingsWatcher = new FileSystemWatcher(Paths.Config, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, e) => _post(() =>
            {
                if (string.Equals(e.Name, "keybindings.json", StringComparison.Ordinal))
                {
                    _keysSettle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
                    {
                        _keysSettle!.Stop();
                        _ = ReloadKeybindingsAsync();
                    });
                    _keysSettle.Stop();
                    _keysSettle.Start();
                    return;
                }

                if (!string.Equals(e.Name, "settings.json", StringComparison.Ordinal)) return;
                _settingsSettle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) => _ = ReloadSettingsAsync());
                _settingsSettle.Stop();
                _settingsSettle.Start();
            });
            _settingsWatcher.Changed += changed;
            _settingsWatcher.Created += changed;
            _settingsWatcher.Renamed += (_, e) => changed(this, e);
            _settingsWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Cannot watch settings.json: {ex.Message}");
        }
    }

    /// <summary>keybindings.json read again and applied to both keymaps (the editor's and the workbench's).</summary>
    public async Task ReloadKeybindingsAsync()
    {
        var (bindings, problem) = await Wicked.Core.Settings.Keybindings.ReadAsync(Paths.KeybindingsFile, CancellationToken.None);
        if (problem is not null)
        {
            Notices.Post(new Notice("keybindings", problem, NoticeKind.Error, $"tab/{Paths.KeybindingsFile}"));
            return;
        }

        Notices.Clear("keybindings");
        EditorKeymap.Apply(bindings);
        Keys = new KeyDispatcher(Commands, bindings);
    }

    private async Task OpenKeybindingsJsonAsync()
    {
        var path = Paths.KeybindingsFile;
        if (!File.Exists(path))
        {
            const string Starter = "// Your key bindings, in VS Code's format. A binding adds keys to a command; \"-command\" removes a default.\n// For example: { \"key\": \"ctrl+shift+d\", \"command\": \"copyLinesDown\" }\n[\n]\n";
            await Task.Run(() => Wicked.Core.Files.TextFile.WriteAsync(path, Starter, Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None));
        }

        await Workbench.OpenAsync(path);
    }

    private async Task ReloadSettingsAsync()
    {
        _settingsSettle?.Stop();
        var path = Paths.SettingsFile;
        var text = await Task.Run(() => File.Exists(path) ? File.ReadAllText(path) : "");
        if (text == _lastWritten) return; // our own write coming back
        var (settings, problem) = await SettingsFile.ReadAsync(path, CancellationToken.None);
        if (problem is not null)
        {
            Notices.Post(new Notice("settings", problem, NoticeKind.Error, $"tab/{path}"));
            return;
        }

        Notices.Clear("settings");
        await ApplySettingsAsync(settings);
        Notices.Post(new Notice("settings-applied", "Settings applied", NoticeKind.Success));
    }

    /// <summary>Opens settings.json in an editor, writing the current settings into it first when it does not exist yet.</summary>
    private async Task OpenSettingsJsonAsync()
    {
        var path = Paths.SettingsFile;
        if (!File.Exists(path)) await Task.Run(() => Wicked.Core.Files.TextFile.WriteAsync(path, System.Text.Json.JsonSerializer.Serialize(Settings, WickedSettings.Json) + "\n", Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None));
        await Workbench.OpenAsync(path);
    }

    /// <summary>Ctrl+K Ctrl+T: the code colour palettes, each previewed as the selection moves, kept on Enter, undone on Escape.</summary>
    private void PickCodeColours()
    {
        var before = Settings;
        var names = new List<(string Value, string Label)> { ("", "Wicked Dark (the theme's own)") };
        names.AddRange(Wicked.Editor.Syntax.SyntaxService.Themes.Select(t => (t.ToString(), Friendly(t.ToString()))));
        var items = names.Select(n => new QuickItem(n.Label, n.Value == before.CodeColorTheme ? "current" : "", "",
            () => UpdateSettingsAsync(s => s with { CodeColorTheme = n.Value }),
            () => _ = ApplySettingsAsync(Settings with { CodeColorTheme = n.Value }))).ToList();
        var current = Math.Max(0, names.FindIndex(n => n.Value == before.CodeColorTheme));
        QuickInput.OpenList("Select a colour theme for code (arrows preview it)", items, current, () => _ = ApplySettingsAsync(before));
        PaletteRequested?.Invoke("");
    }

    /// <summary>The user's snippets, loaded at start and kept current.</summary>
    public SnippetLibrary Snippets { get; }

    /// <summary>Insert Snippet: the active language's snippets in the palette; the chosen one goes in at the caret.</summary>
    private void PickSnippet()
    {
        if (Workbench.ActiveTab?.Editor is not { } editor) return;
        var items = Snippets.For(editor.Language?.Id).Select(snippet => new QuickItem(snippet.Name, snippet.Prefixes.IsDefaultOrEmpty ? snippet.Description : $"{string.Join(", ", snippet.Prefixes)}  {snippet.Description}", "", () =>
        {
            editor.InsertSnippet(snippet);
            editor.Focus();
            return Task.CompletedTask;
        })).ToList();
        QuickInput.OpenList(items.Count == 0 ? "No snippets for this language yet: run Snippets: Configure Snippets" : "Select a snippet", items, 0, null);
        PaletteRequested?.Invoke("");
    }

    /// <summary>Configure Snippets: the active language's snippet file, started with an example when it does not exist.</summary>
    private async Task ConfigureSnippetsAsync()
    {
        var language = Workbench.ActiveTab?.Language?.Id ?? "global";
        var path = Path.Combine(Snippets.Folder, language == "global" ? "global.code-snippets" : $"{language}.json");
        if (!File.Exists(path))
        {
            var starter = "{\n  // Snippets for " + language + ", in VS Code's format. $1, $2 are the places Tab moves to; $0 is where it ends.\n" +
                "  \"Example\": {\n    \"prefix\": \"ex\",\n    \"body\": [\"example(${1:value});\", \"$0\"],\n    \"description\": \"An example snippet\"\n  }\n}\n";
            await Task.Run(() => Wicked.Core.Files.TextFile.WriteAsync(path, starter, Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None));
        }

        await Workbench.OpenAsync(path);
    }

    /// <summary>"DarkPlus" as "Dark+", "OneDark" as "One Dark".</summary>
    public static string Friendly(string themeName) =>
        System.Text.RegularExpressions.Regex.Replace(themeName.Replace("Plus", "+"), "(?<=[a-z])(?=[A-Z])", " ");

    /// <summary>Asks the window to show the palette with a prefix typed (">" commands, ":" a line, "" files).</summary>
    public event Action<string>? PaletteRequested;

    public void OpenPalette(string prefix)
    {
        QuickInput.Open(prefix);
        PaletteRequested?.Invoke(prefix);
    }

    private async Task OpenFolderAsync()
    {
        if (Workbench.Dialogs is { } dialogs && await dialogs.PickFolderAsync() is { } folder) await OpenFolderAndRememberAsync(folder);
    }

    private async Task OpenFilesAsync()
    {
        if (Workbench.Dialogs is not { } dialogs) return;
        foreach (var file in await dialogs.PickFilesAsync()) await Workbench.OpenAsync(file);
    }

    public Task OpenFolderAndRememberAsync(string folder) => Workbench.OpenFolderAsync(folder);

    /// <summary>A folder opened (from anywhere): first in the recent list, and the session written.</summary>
    private async Task RememberFolderAsync(string folder)
    {
        _session = _session.WithRecent(folder);
        ShowRecent();
        await SaveSessionAsync();
    }

    private void ShowRecent()
    {
        Workbench.RecentFolders.Clear();
        foreach (var folder in _session.RecentFolders.Take(6)) Workbench.RecentFolders.Add(folder);
    }

    // ── Settings and session ─────────────────────────────────────────────────────────────────────

    /// <summary>Reads settings and the last session on a worker, then reopens the last folder and its files.</summary>
    public async Task StartAsync()
    {
        var (settings, problem) = await SettingsFile.ReadAsync(Paths.SettingsFile, CancellationToken.None);
        await ApplySettingsAsync(settings);
        await ReloadKeybindingsAsync();
        await Snippets.LoadAsync();
        WatchSettings();
        if (problem is not null) Notices.Post(new Notice("settings", problem, NoticeKind.Warning, $"tab/{Paths.SettingsFile}"));
        _session = await SessionState.ReadAsync(Paths.SessionFile, CancellationToken.None);
        ShowRecent();
        _ = Models.StartAsync(allowNetwork: Environment.GetEnvironmentVariable("WICKED_OFFLINE") != "1");
        if (!Settings.RestoreSession || _session.LastFolder is not { } folder || !Directory.Exists(folder)) return;
        await Workbench.OpenFolderAsync(folder);
        if (_session.OpenFiles.TryGetValue(folder, out var files))
        {
            foreach (var file in files.Where(File.Exists)) await Workbench.OpenAsync(file);
        }
    }

    /// <summary>Remembers the open folder and files (on a worker).</summary>
    public async Task SaveSessionAsync()
    {
        if (Workbench.Root is { } root)
        {
            var files = Workbench.AllTabs.Where(t => t.Path is not null && t != Workbench.ActiveTab).Select(t => t.Path!).Distinct().ToList();
            if (Workbench.ActiveTab?.Path is { } active) files.Add(active);
            _session = _session with { OpenFiles = new Dictionary<string, List<string>>(_session.OpenFiles) { [root] = files } };
        }

        var session = _session;
        try
        {
            await Task.Run(() => session.WriteAsync(Paths.SessionFile, CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The session could not be saved: {ex.Message}");
        }
    }

    public IReadOnlyList<string> RecentFolders => _session.RecentFolders;

    // ── The active editor in the status bar ──────────────────────────────────────────────────────

    private Document? _watched;

    [ObservableProperty]
    private string _caretText = "";

    [ObservableProperty]
    private string _documentText = "";

    private void OnActiveEditorChanged()
    {
        if (_watched is not null) _watched.SelectionsChanged -= UpdateCaret;
        _watched = Workbench.ActiveTab?.Document;
        if (_watched is not null) _watched.SelectionsChanged += UpdateCaret;
        UpdateCaret();
        var tab = Workbench.ActiveTab;
        DocumentText = tab is null ? "" : string.Join("    ", new[]
        {
            tab.Editor?.Options is { } o ? (o.InsertSpaces ? $"Spaces: {o.TabSize}" : $"Tab Size: {o.TabSize}") : "Spaces: 4",
            tab.Encoding.Label,
            tab.Document.LineBreak switch { "\r\n" => "CRLF", "\r" => "CR", _ => "LF" },
            tab.Language?.Id ?? "Plain Text",
        });
    }

    /// <summary>"Ln 12, Col 5", and how many characters are selected or how many carets there are.</summary>
    private void UpdateCaret()
    {
        if (_watched is not { } doc)
        {
            CaretText = "";
            return;
        }

        var (line, column) = doc.Text.Position(doc.Primary.Active);
        var text = $"Ln {line + 1}, Col {column + 1}";
        if (doc.Selections.Length > 1) text += $" ({doc.Selections.Length} selections)";
        else if (!doc.Primary.IsEmpty) text += $" ({doc.Primary.Length} selected)";
        CaretText = text;
    }

    public WickedPaths Paths { get; }

    public NoticeBoard Notices { get; }

    public string Title => Workbench.Root is { } root ? $"{Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))} — Wicked" : "Wicked";

    public string WorkspaceName => Workbench.Root is { } root ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) : "";

    public string CommandCentreText => Workbench.Root is null ? "Search files, commands and models" : $"Search {WorkspaceName}";

    public string VersionText { get; } = "Wicked " + BuildInfo.Version.Split('+')[0];

    // ── Side panel and assistant ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorer), nameof(IsSearch), nameof(IsSourceControl), nameof(IsModels), nameof(PanelTitle), nameof(PanelEmptyTitle), nameof(PanelEmptyText), nameof(ShowsPanelPlaceholder))]
    private SidePanel _panel = SidePanel.Explorer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorer), nameof(IsSearch), nameof(IsSourceControl), nameof(IsModels))]
    private bool _sidebarVisible = true;

    [ObservableProperty]
    private bool _assistantVisible = true;

    public bool IsExplorer => SidebarVisible && Panel == SidePanel.Explorer;

    public bool IsSearch => SidebarVisible && Panel == SidePanel.Search;

    public bool IsSourceControl => SidebarVisible && Panel == SidePanel.SourceControl;

    public bool IsModels => SidebarVisible && Panel == SidePanel.Models;

    /// <summary>The panels not built yet (search, source control) show what they will hold.</summary>
    public bool ShowsPanelPlaceholder => Panel is SidePanel.Search or SidePanel.SourceControl;

    public string PanelTitle => Panel switch
    {
        SidePanel.Search => "SEARCH",
        SidePanel.SourceControl => "SOURCE CONTROL",
        SidePanel.Models => "MODELS",
        _ => "EXPLORER",
    };

    public string PanelEmptyTitle => Panel switch
    {
        SidePanel.Search => "Nothing to search yet",
        SidePanel.SourceControl => "No repository",
        SidePanel.Models => "No models installed",
        _ => "No folder open",
    };

    public string PanelEmptyText => Panel switch
    {
        SidePanel.Search => "Open a folder to search across its files.",
        SidePanel.SourceControl => "Open a folder that is a Git repository to see its changes.",
        SidePanel.Models => "Models you install appear here, with their updates.",
        _ => "Open a folder to see its files here.",
    };

    /// <summary>The rail's buttons: the open panel's button closes the side bar, any other opens its panel.</summary>
    [RelayCommand]
    private void ShowPanel(SidePanel panel)
    {
        if (SidebarVisible && Panel == panel)
        {
            SidebarVisible = false;
            return;
        }

        Panel = panel;
        SidebarVisible = true;
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    [RelayCommand]
    private void ToggleAssistant() => AssistantVisible = !AssistantVisible;

    /// <summary>Takes the user to <paramref name="route"/> (see <see cref="ShellRoutes"/>); false for a place this build does not know.</summary>
    public bool Navigate(string route)
    {
        if (Workbench.OpenRoute(route)) return true;
        if (route.StartsWith("tab/", StringComparison.Ordinal))
        {
            _ = OpenFileRouteAsync(route[4..]);
            return true;
        }

        switch (route)
        {
            case ShellRoutes.Explorer: Panel = SidePanel.Explorer; SidebarVisible = true; return true;
            case ShellRoutes.Search: Panel = SidePanel.Search; SidebarVisible = true; return true;
            case ShellRoutes.SourceControl: Panel = SidePanel.SourceControl; SidebarVisible = true; return true;
            case ShellRoutes.Models: Panel = SidePanel.Models; SidebarVisible = true; return true;
            case ShellRoutes.Assistant: AssistantVisible = true; return true;
            case ShellRoutes.SettingsModels: Models.Prompt = null; Models.Browse.Close(); SettingsOpen = true; SettingsSheet.RequestSection("models"); return true;
            default:
                Log.Warn($"No place called '{route}' to go to");
                return false;
        }
    }

    // ── The footer's notice ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private Notice? _notice;

    [ObservableProperty]
    private string _moreNotices = "";

    public bool IsProgressNotice => Notice?.Kind == NoticeKind.Progress;

    public string? NoticeTip => Notice?.Route is null ? null : "Go to where this is dealt with";

    partial void OnNoticeChanged(Notice? value)
    {
        OnPropertyChanged(nameof(IsProgressNotice));
        OnPropertyChanged(nameof(NoticeTip));
    }

    /// <summary>A click on the footer's notice: go where it is dealt with.</summary>
    [RelayCommand]
    private void OpenNotice()
    {
        if (Notice?.Route is { } route) Navigate(route);
    }

    private void RefreshNotice()
    {
        Notice = Notices.Top;
        var more = Notices.Count - 1;
        MoreNotices = more > 0 ? $"+{more}" : "";
    }

    // ── The daemon ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DaemonText))]
    private bool? _daemonRunning;

    public string DaemonText => DaemonRunning switch
    {
        true => "Daemon running",
        false => "Daemon not running",
        null => "Looking for the daemon…",
    };

    private async Task WatchDaemonAsync(CancellationToken ct)
    {
        using var client = new DaemonClient(Paths.DaemonSocket, TimeSpan.FromSeconds(3));
        using var timer = new PeriodicTimer(_daemonPoll);
        try
        {
            do
            {
                var running = await client.GetHealthAsync(ct).ConfigureAwait(false) is not null;
                _post(() => DaemonRunning = running);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _settingsWatcher?.Dispose();
        Snippets.Dispose();
        Workbench.Dispose();
        _ = Models.DisposeAsync().AsTask();
        _stop.Cancel();
        _stop.Dispose();
    }
}
