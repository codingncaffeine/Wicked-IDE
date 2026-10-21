using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Chat;
using Wicked.App.Editor;
using Wicked.App.Language;
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
    Outline,
    Testing,
    Debug,
    Extensions,

    /// <summary>A panel an extension added.</summary>
    Custom,
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
        SourceControl = new Git.SourceControlViewModel(this, paths, Notices, _post);
        Workbench.ActiveEditorChanged += OnActiveEditorChanged;
        Workbench.FolderOpened += folder => _ = RememberFolderAsync(folder);
        Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(Workbench.Root)) return;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(WorkspaceName));
            OnPropertyChanged(nameof(CommandCentreText));
        };
        Output = new OutputLog();
        Language = new LanguageFeatures(Workbench, paths, Output, Notices);
        Search = new Wicked.App.Search.SearchViewModel(this);
        Outline = new OutlineViewModel(Language, Workbench);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(IsOutline)) return;
            Outline.Active = IsOutline;
            if (IsOutline) _ = Outline.RefreshAsync();
        };
        Language.Problems.Opened += p => _ = OpenAtAsync(p.Path, p.Line, p.Column);
        Language.Problems.CountsChanged += () =>
        {
            if (BottomPanel[PanelIds.Problems] is { } tab) tab.Badge = Language.Problems.Total;
            OnPropertyChanged(nameof(ProblemsText));
            OnPropertyChanged(nameof(ProblemsTip));
        };
        BottomPanel.Add(PanelIds.Problems, "PROBLEMS", () => new Views.ProblemsView { DataContext = Language.Problems });
        BottomPanel.Add(PanelIds.Output, "OUTPUT", () => new Views.OutputView(Output, () => Settings));
        BottomPanel.IsVisible = false;
        WireLanguage();
        WireSourceControl();
        WireAssistant();
        WireTerminal();
        WireTasks();
        Debugger = new Wicked.App.Debug.DebugController(this, paths, Notices, _post);
        // preLaunchTask: the task runs (or is found running) and the launch waits until it is ready, as in VS Code.
        Debugger.RunTask = async (label, ct) => await Tasks.RunTaskByLabelAsync(label, ct)
            ? null
            : $"The preLaunchTask \"{label}\" is not in tasks.json or did not finish successfully (its output is in the Output panel).";
        WireExtensions();
        Commands = _builtInCommands = BuildCommands();
        QuickInput = new QuickInputViewModel(this);
        Models = new ModelsViewModel(paths, Notices);
        Assistant = AssistantServices.Resolve(paths, Models.Services);
        Chat = new ChatViewModel(Assistant, new ShellChatHost(this));
        Models.AttachRuntime(Chat.Runtime);
        Snippets = new SnippetLibrary(Path.Combine(paths.Config, "snippets"));
        Workbench.Snippets = Snippets;
        Models.RouteRequested += route => Navigate(route);
        SettingsSheet = new SettingsViewModel(this);
        Notifications = new NotificationsViewModel(this);
        WhatsNew = new WhatsNewViewModel(this);
        ApiLog = new ApiLogViewModel(paths);
        // The server's request log follows the daemon only while its tab is in view.
        BottomPanel.Add(PanelIds.ApiLog, "SERVER LOG", () => new Views.ApiLogView { DataContext = ApiLog });
        BottomPanel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(Shell.BottomPanel.Active) or nameof(Shell.BottomPanel.IsVisible))) return;
            if (BottomPanel.IsVisible && BottomPanel.Active?.Id == PanelIds.ApiLog) ApiLog.Start();
            else ApiLog.Stop();
        };
        Models.WhatsNewRequested += WhatsNew.Open;
        Keys = new KeyDispatcher(Commands);
        _ = Task.Run(() => WatchDaemonAsync(_stop.Token));
    }

    public Bench Workbench { get; }

    public IReadOnlyList<WorkbenchCommand> Commands { get; private set; }

    public QuickInputViewModel QuickInput { get; }

    /// <summary>The Outline panel: the active file's symbols.</summary>
    public OutlineViewModel Outline { get; }

    /// <summary>The Search panel.</summary>
    public Wicked.App.Search.SearchViewModel Search { get; }

    /// <summary>Asks the window to put the keyboard in the Search panel's query box.</summary>
    public event Action? SearchFocusRequested;

    /// <summary>The Output panel's channels (language servers, git, tasks, …).</summary>
    public OutputLog Output { get; }

    /// <summary>The language servers: problems, completion, hover, go to, rename, format.</summary>
    public LanguageFeatures Language { get; }

    /// <summary>The status bar's problem counts ("⊗ 2  ⚠ 5"), a click opens Problems.</summary>
    public string ProblemsText => $"{Language.Problems.Errors}  ·  {Language.Problems.Warnings}";

    public string ProblemsTip => $"{Language.Problems.Errors} errors, {Language.Problems.Warnings} warnings, {Language.Problems.Infos} infos — show Problems";

    [RelayCommand]
    private void ShowProblems() => BottomPanel.Toggle(PanelIds.Problems);

    /// <summary>Opens a file with the caret at a line and column (0-based), from Problems, a reference or a definition.</summary>
    public async Task OpenAtAsync(string path, int line, int column, int? endLine = null, int? endColumn = null)
    {
        var tab = await Workbench.OpenAsync(path);
        if (tab is null) return;
        var rope = tab.Document.Text;
        var start = LspText.OffsetOf(rope, new Wicked.Core.Lsp.Position(line, column));
        var end = endLine is int el ? LspText.OffsetOf(rope, new Wicked.Core.Lsp.Position(el, endColumn ?? 0)) : start;
        tab.Document.SetSelections([new Selection(start, end)]);
        if (tab.Editor is { } editor) editor.Select(start, end);
        else _post(() => tab.Editor?.Select(start, end));
    }

    /// <summary>The panel under the editors: Problems, Output, Terminal and the rest.</summary>
    public BottomPanel BottomPanel { get; } = new();

    public ModelsViewModel Models { get; }

    // ── Source Control ──────────────────────────────────────────────────────────────────────────

    /// <summary>The Source Control panel: repositories, changes, commit box, graph, stashes.</summary>
    public Git.SourceControlViewModel SourceControl { get; }

    /// <summary>Shows a list of choices in the palette (the branch picker); typing filters it, Enter chooses.</summary>
    public void ShowList(string placeholder, IReadOnlyList<QuickItem> items, int selected)
    {
        QuickInput.OpenList(placeholder, items, selected, null);
        PaletteRequested?.Invoke("");
    }

    /// <summary>The Assistant panel.</summary>
    public ChatViewModel Chat { get; }

    /// <summary>Puts text on the clipboard; the window sets it (the clipboard belongs to a window).</summary>
    public Func<string, Task>? Clipboard { get; set; }

    /// <summary>Whether the model half may reach the network at start (off under <c>WICKED_OFFLINE</c>; a test world on loopback turns it on).</summary>
    public bool AllowNetwork { get; set; } = !Wicked.Core.Http.WickedHttp.IsOffline;

    public SettingsViewModel SettingsSheet { get; }

    /// <summary>The list behind the footer's "+N", and "What's new in your models".</summary>
    public NotificationsViewModel Notifications { get; }

    public WhatsNewViewModel WhatsNew { get; }

    /// <summary>The server's request log, for the bottom panel to mount (<c>ApiLogView</c>); it follows the daemon once <c>Start()</c> is called.</summary>
    public ApiLogViewModel ApiLog { get; }

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
            new("workbench.action.togglePanel", "View", "Toggle Panel", "Ctrl+J", () => Sync(() => BottomPanel.Toggle())),
            new("workbench.actions.view.problems", "View", "Show Problems", null, () => Sync(() => BottomPanel.Toggle(PanelIds.Problems))),
            new("workbench.action.output.toggleOutput", "View", "Show Output", "Ctrl+Shift+U", () => Sync(() => BottomPanel.Toggle(PanelIds.Output))),
            new("workbench.action.toggleMaximizedPanel", "View", "Maximise Panel", null, () => Sync(() => BottomPanel.IsMaximized = !BottomPanel.IsMaximized)),
            new("wicked.toggleAssistant", "View", "Toggle Assistant", "Ctrl+L", () => Sync(ToggleAssistant)),
            new("workbench.view.explorer", "View", "Show Explorer", "Ctrl+Shift+E", () => Sync(() => Navigate(ShellRoutes.Explorer))),
            new("workbench.view.search", "View", "Show Search", "Ctrl+Shift+F", () => Sync(ShowSearch)),
            new("workbench.action.replaceInFiles", "Search", "Replace in Files", "Ctrl+Shift+H", () => Sync(() =>
            {
                Search.ShowReplace = true;
                ShowSearch();
            })),
            new("workbench.view.scm", "View", "Show Source Control", "Ctrl+Shift+G", () => Sync(() => Navigate(ShellRoutes.SourceControl))),
            new("wicked.showModels", "View", "Show Models", "Ctrl+Shift+M", () => Sync(() => Navigate(ShellRoutes.Models))),
            new("outline.focus", "View", "Show Outline", null, () => Sync(() => Navigate(ShellRoutes.Outline))),
            new("workbench.action.showCommands", "View", "Show All Commands", "Ctrl+Shift+P", () => Sync(() => OpenPalette(">"))),
            new("workbench.action.quickOpen", "Go", "Go to File...", "Ctrl+P", () => Sync(() => OpenPalette(""))),
            new("workbench.action.gotoLine", "Go", "Go to Line...", "Ctrl+G", () => Sync(() => OpenPalette(":"))),
            new("editor.action.fontZoomIn", "View", "Zoom In", "Ctrl+OemPlus", () => UpdateSettingsAsync(st => st with { FontSize = Math.Min(72, st.FontSize + 1) })),
            new("editor.action.fontZoomOut", "View", "Zoom Out", "Ctrl+OemMinus", () => UpdateSettingsAsync(st => st with { FontSize = Math.Max(6, st.FontSize - 1) })),
            new("editor.action.fontZoomReset", "View", "Reset Zoom", "Ctrl+D0", () => UpdateSettingsAsync(st => st with { FontSize = new WickedSettings().FontSize })),
            new("workbench.action.openSettings", "Preferences", "Open Settings", "Ctrl+OemComma", () => Sync(() => SettingsOpen = true)),
            new("workbench.action.openSettingsJson", "Preferences", "Open Settings (JSON)", null, OpenSettingsJsonAsync),
            new("workbench.action.selectTheme", "Preferences", "Color Theme", "Ctrl+K Ctrl+T", () => Sync(PickTheme)),
            new("wicked.selectCodeColours", "Preferences", "Code Colours", null, () => Sync(PickCodeColours)),
            new("wicked.openStylePack", "Preferences", "Open Style Pack (AXAML)", null, OpenStylePackAsync),
            new("workbench.action.focusNextPart", "View", "Focus Next Part", "F6", () => Sync(() => FocusPartRequested?.Invoke(1))),
            new("workbench.action.focusPreviousPart", "View", "Focus Previous Part", "Shift+F6", () => Sync(() => FocusPartRequested?.Invoke(-1))),
            new("workbench.action.importTheme", "Preferences", "Import a VS Code Color Theme...", null, ImportThemeAsync),
            new("editor.foldAll", "View", "Fold All", "Ctrl+K Ctrl+D0", () => Sync(() => Workbench.ActiveTab?.Editor?.FoldAll())),
            new("editor.unfoldAll", "View", "Unfold All", "Ctrl+K Ctrl+J", () => Sync(() => Workbench.ActiveTab?.Editor?.UnfoldAll())),
            new("editor.action.toggleWordWrap", "View", "Toggle Word Wrap", "Alt+Z", () => Sync(() => Workbench.ActiveTab?.Editor?.ToggleWordWrap())),
            new("editor.action.insertSnippet", "Snippets", "Insert Snippet", null, () => Sync(PickSnippet)),
            .. LanguageCommands(),
            .. TerminalCommands(),
            .. AssistantCommands(),
            .. ExtensionCommands(),
            new("workbench.action.openSnippets", "Snippets", "Configure Snippets", null, ConfigureSnippetsAsync),
            new("workbench.action.openGlobalKeybindingsFile", "Preferences", "Open Keyboard Shortcuts (JSON)", null, OpenKeybindingsJsonAsync),
            .. SourceControl.Commands,
            .. TaskCommands(),
            .. DebugCommands(),
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
        Language.ApplySettings(settings);
        Terminal?.ApplySettings(settings);
        Chat.ApplySettings(settings);
        Extensions.Bridge.SettingsApplied(settings);
        OnPropertyChanged(nameof(Settings));
        var services = await EditorServices.Ready;
        ApplyTheme(settings.ColorTheme, services);
        services.Apply(settings);
        Language.ApplySettings(settings); // again, now the code colours are known: the semantic colours follow them
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
        SetUserBindings(bindings);
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

    /// <summary>Code colours: the code colour palettes, each previewed as the selection moves, kept on Enter, undone on Escape.</summary>
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
        await Themes.LoadUserThemesAsync();
        await ApplySettingsAsync(settings);
        await StartStylePacksAsync();
        await ReloadKeybindingsAsync();
        await Snippets.LoadAsync();
        WatchSettings();
        StartExtensions();
        if (problem is not null) Notices.Post(new Notice("settings", problem, NoticeKind.Warning, $"tab/{Paths.SettingsFile}"));
        _session = await SessionState.ReadAsync(Paths.SessionFile, CancellationToken.None);
        RestorePanel();
        ShowRecent();
        _ = Models.StartAsync(AllowNetwork);
        _ = Chat.StartAsync();
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

        _session = _session with { PanelVisible = BottomPanel.IsVisible, PanelTab = BottomPanel.Active?.Id, PanelHeight = BottomPanel.Height };
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
    [NotifyPropertyChangedFor(nameof(IsExplorer), nameof(IsSearch), nameof(IsSourceControl), nameof(IsModels), nameof(IsOutline), nameof(IsTesting), nameof(IsDebug), nameof(PanelTitle), nameof(PanelEmptyTitle), nameof(PanelEmptyText), nameof(ShowsPanelPlaceholder))]
    private SidePanel _panel = SidePanel.Explorer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorer), nameof(IsSearch), nameof(IsSourceControl), nameof(IsModels), nameof(IsOutline), nameof(IsTesting), nameof(IsDebug))]
    private bool _sidebarVisible = true;

    [ObservableProperty]
    private bool _assistantVisible = true;

    public bool IsExplorer => SidebarVisible && Panel == SidePanel.Explorer;

    public bool IsSearch => SidebarVisible && Panel == SidePanel.Search;

    public bool IsSourceControl => SidebarVisible && Panel == SidePanel.SourceControl;

    public bool IsModels => SidebarVisible && Panel == SidePanel.Models;

    public bool IsOutline => SidebarVisible && Panel == SidePanel.Outline;
    public bool IsTesting => SidebarVisible && Panel == SidePanel.Testing;
    public bool IsDebug => SidebarVisible && Panel == SidePanel.Debug;

    /// <summary>Every side panel has its view now; kept for a panel added before its view is.</summary>
    public bool ShowsPanelPlaceholder => false;

    /// <summary>Ctrl+Shift+F: the Search panel with the editor's selection (a single line of it) as the query.</summary>
    private void ShowSearch()
    {
        if (Workbench.ActiveTab?.Document is { } doc && !doc.Primary.IsEmpty && doc.Primary.Length < 200)
        {
            var selected = doc.Text.Substring(doc.Primary.Start, doc.Primary.Length);
            if (!selected.Contains('\n')) Search.Query = selected;
        }

        Navigate(ShellRoutes.Search);
        SearchFocusRequested?.Invoke();
    }

    public string PanelTitle => Panel switch
    {
        SidePanel.Search => "SEARCH",
        SidePanel.SourceControl => "SOURCE CONTROL",
        SidePanel.Models => "MODELS",
        SidePanel.Outline => "OUTLINE",
        SidePanel.Testing => "TESTING",
        SidePanel.Debug => "RUN AND DEBUG",
        SidePanel.Extensions => "EXTENSIONS",
        SidePanel.Custom => ActiveCustomPanel?.Title.ToUpperInvariant() ?? "EXTENSION",
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

    /// <summary>The title bar's panel button (Ctrl+J): Problems, Output (a build's output), Terminal, Debug Console.</summary>
    [RelayCommand]
    private void TogglePanel() => BottomPanel.Toggle();

    /// <summary>The panel as the last session left it (layout, so whatever the restore setting says).</summary>
    private void RestorePanel()
    {
        if (_session.PanelHeight >= Shell.BottomPanel.MinHeight) BottomPanel.Height = _session.PanelHeight;
        if (!_session.PanelVisible) return;
        if (_session.PanelTab is { } tab && BottomPanel.Tabs.Any(t => t.Id == tab)) BottomPanel.Show(tab);
        else BottomPanel.IsVisible = true;
    }

    /// <summary>The footer's model status: the Models panel, where models are loaded and set up.</summary>
    [RelayCommand]
    private void ShowModelsStatus() => Navigate(ShellRoutes.Models);

    /// <summary>The footer's "+N": every waiting notice, with its buttons.</summary>
    [RelayCommand]
    private void OpenNotifications() => Notifications.IsOpen = true;

    /// <summary>Takes the user to <paramref name="route"/> (see <see cref="ShellRoutes"/>); false for a place this build does not know.</summary>
    public bool Navigate(string route)
    {
        if (Workbench.OpenRoute(route)) return true;
        if (route.StartsWith("tab/", StringComparison.Ordinal))
        {
            _ = OpenFileRouteAsync(route[4..]);
            return true;
        }

        if (Debugger.TryNavigate(route)) return true;

        if (route.StartsWith("lsp/install/", StringComparison.Ordinal))
        {
            OfferServerInstall(route["lsp/install/".Length..]);
            return true;
        }

        if (route.StartsWith("output/", StringComparison.Ordinal))
        {
            Output.Shown = Output.Channel(route["output/".Length..]);
            BottomPanel.Show(PanelIds.Output);
            return true;
        }

        switch (route)
        {
            case ShellRoutes.Explorer: Panel = SidePanel.Explorer; SidebarVisible = true; return true;
            case ShellRoutes.Search: Panel = SidePanel.Search; SidebarVisible = true; return true;
            case ShellRoutes.SourceControl: Panel = SidePanel.SourceControl; SidebarVisible = true; return true;
            case ShellRoutes.Models: Panel = SidePanel.Models; SidebarVisible = true; return true;
            case ShellRoutes.Outline: Panel = SidePanel.Outline; SidebarVisible = true; return true;
            case ShellRoutes.Testing: Panel = SidePanel.Testing; SidebarVisible = true; return true;
            case ShellRoutes.Debug: Panel = SidePanel.Debug; SidebarVisible = true; return true;
            case ShellRoutes.Extensions: Panel = SidePanel.Extensions; SidebarVisible = true; return true;
            case ShellRoutes.Assistant: AssistantVisible = true; return true;
            case ShellRoutes.SettingsModels: Models.Prompt = null; Models.Browse.Close(); SettingsOpen = true; SettingsSheet.RequestSection("models"); return true;
            case ShellRoutes.SettingsRuntime: Models.Prompt = null; Models.Browse.Close(); SettingsOpen = true; SettingsSheet.RequestSection("runtime"); return true;
            case ShellRoutes.SettingsServer: Models.Prompt = null; Models.Browse.Close(); SettingsOpen = true; SettingsSheet.RequestSection("server"); return true;
            case ShellRoutes.Notifications: Notifications.IsOpen = true; return true;
            case ShellRoutes.WhatsNew: WhatsNew.Open(); return true;
            case var url when url.StartsWith(ShellRoutes.UrlPrefix, StringComparison.Ordinal): return OpenWebAddress(url[ShellRoutes.UrlPrefix.Length..]);
            default:
                Log.Warn($"No place called '{route}' to go to");
                return false;
        }
    }

    /// <summary>Opens a web address in the browser; anything but http and https is refused (a notice's route is data, not a command).</summary>
    private bool OpenWebAddress(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        Models.OpenExternal(uri.AbsoluteUri);
        return true;
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
        Notifications.Refresh();
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
        Debugger.Dispose();
        Snippets.Dispose();
        SourceControl.Dispose();
        DisposeTasks();
        Workbench.Dispose();
        _ = Language.DisposeAsync().AsTask();
        Terminal.Dispose();
        ApiLog.Dispose();
        _ = Models.DisposeAsync().AsTask();
        _ = Extensions.DisposeAsync().AsTask();
        _stop.Cancel();
        _stop.Dispose();
    }
}
