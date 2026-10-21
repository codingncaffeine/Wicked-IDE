using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Core.Debug;

namespace Wicked.App.Debug;

/// <summary>
/// Debugging in the window: the sessions and what the views show of them (Variables, Watch, Call Stack, Breakpoints, the
/// Debug Console), the commands that run them, and the editor marks. Everything the adapters say arrives on workers and
/// is posted here; everything that waits on an adapter runs on a worker and posts its result.
/// </summary>
public sealed partial class DebugController : ObservableObject, IDisposable
{
    public const string ConsoleChannel = "Debug Adapters";

    private readonly ShellViewModel _shell;
    private readonly WickedPaths _paths;
    private readonly NoticeBoard _notices;
    private readonly Action<Action> _post;
    private readonly Dictionary<(DebugSession Session, int Thread), StackState> _stacks = [];
    private (DebugSession Session, int ThreadId, DapStackFrame Frame)? _current;
    private int _stopVersion;
    private bool _openedOnce;
    private bool _rebuilding;
    private string? _root;
    private LaunchFile _launchFile = LaunchFile.Empty;
    private Avalonia.Threading.DispatcherTimer? _saveTimer;

    private sealed class StackState(IReadOnlyList<DapStackFrame> frames, int total)
    {
        public List<DapStackFrame> Frames { get; } = [.. frames];

        public int Total { get; set; } = total;
    }

    public DebugController(ShellViewModel shell, WickedPaths paths, NoticeBoard notices, Action<Action> post)
    {
        _shell = shell;
        _paths = paths;
        _notices = notices;
        _post = post;
        Locator = new DebugAdapterLocator(AdaptersFolder);
        Adapters = new DebugAdapterProvider(Locator);
        Manager = new DebugManager(Breakpoints, new Late(this));
        Launcher = new DebugLauncher(Manager);
        Console = new DebugConsoleViewModel(post, EvaluateInConsoleAsync);
        Variables = new DebugTree(LoadChildrenAsync);
        WatchTree = new DebugTree(LoadChildrenAsync);
        WireManager();
        Breakpoints.Changed += OnBreakpointsChanged;
        WireEditors();
        Console.OpenSource = (path, line) => _shell.OpenAtAsync(path, Math.Max(0, line - 1), 0);
        shell.BottomPanel.Add(Shell.PanelIds.DebugConsole, "DEBUG CONSOLE", () => new DebugConsoleView { DataContext = Console });
    }

    /// <summary>Asks whatever provider is set now, so a test can swap in a stand-in after the window is up.</summary>
    private sealed class Late(DebugController owner) : IDebugAdapterProvider
    {
        public Task<AdapterEndpoint> ResolveAsync(string type, JsonElement configuration, CancellationToken ct) => owner.Adapters.ResolveAsync(type, configuration, ct);
    }

    public BreakpointStore Breakpoints { get; } = new();

    public DebugManager Manager { get; }

    public DebugLauncher Launcher { get; }

    public DebugAdapterLocator Locator { get; set; }

    /// <summary>Where adapters come from (the catalog and the machine); tests set a stand-in.</summary>
    public IDebugAdapterProvider Adapters { get; set; }

    public string AdaptersFolder => Path.Combine(_paths.Data, "adapters");

    public DebugConsoleViewModel Console { get; }

    public DebugTree Variables { get; }

    public DebugTree WatchTree { get; }

    public ObservableCollection<StackRow> CallStack { get; } = [];

    public ObservableCollection<BreakpointRow> BreakpointRows { get; } = [];

    public ObservableCollection<ConfigurationChoice> Configurations { get; } = [];

    public List<string> WatchExpressions { get; } = [];

    [ObservableProperty]
    private ConfigurationChoice? _selectedConfiguration;

    /// <summary>A session exists (starting, running or stopped): the toolbar shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCreateLaunch))]
    private bool _hasSession;

    /// <summary>The session being looked at is stopped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueOrPause), nameof(ContinueTip))]
    private bool _isStopped;

    /// <summary>The session being looked at is running (the toolbar offers Pause).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinueOrPause), nameof(ContinueTip))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>launch.json has something to start.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsCreateLaunch))]
    private bool _hasConfigurations;

    /// <summary>The toolbar's first button works: there is a stopped program to continue or a running one to pause.</summary>
    public bool CanContinueOrPause => IsStopped || IsRunning;

    public string ContinueTip => IsRunning ? "Pause (F6)" : "Continue (F5)";

    /// <summary>The panel offers to create a launch.json: there is none to run and nothing is running.</summary>
    public bool ShowsCreateLaunch => !HasConfigurations && !HasSession;

    /// <summary>The frame whose variables are shown, as the Call Stack selects it.</summary>
    public (DebugSession Session, int ThreadId, DapStackFrame Frame)? CurrentFrame => _current;

    /// <summary>Runs a task from tasks.json by its label and waits; null when it succeeded, otherwise why not. Set by the tasks lane's wiring.</summary>
    public Func<string, CancellationToken, Task<string?>>? RunTask
    {
        get => Launcher.RunTask;
        set => Launcher.RunTask = value;
    }

    /// <summary>The program an adapter asked to have run in a terminal is started by this; the terminal lane may replace the default.</summary>
    public Func<RunInTerminalArguments, Action<string, string>?, CancellationToken, Task<RunInTerminalResult>> RunInTerminal
    {
        get => Manager.RunInTerminal;
        set => Manager.RunInTerminal = value;
    }

    // ── Sessions ─────────────────────────────────────────────────────────────────────────────────

    private void WireManager()
    {
        Manager.SessionStarted += session => _post(() => OnSessionStarted(session));
        Manager.SessionEnded += session => _post(() => OnSessionEnded(session));
        Manager.ActiveChanged += _ => _post(UpdateState);
        Manager.SessionChanged += session => _post(() => OnSessionChanged(session));
        Manager.SessionStopped += (session, stop) => _ = Task.Run(() => HandleStopAsync(session, stop));
        Manager.Output += (session, output) => OnOutput(session, output);
    }

    private void OnSessionStarted(DebugSession session)
    {
        session.BreakpointsResolved += () => _post(() =>
        {
            RefreshBreakpointRows();
            RefreshEditorMarks();
        });
        session.ThreadsChanged += () => _post(RebuildCallStack);
        session.Invalidated += () => _post(() => _ = RefreshFrameAsync());
        if (Manager.Sessions.Count == 1) OpenViewsOnStart();
        UpdateState();
        RebuildCallStack();
    }

    /// <summary>The Run and Debug panel and the Debug Console come up when a session starts, as the setting says.</summary>
    private void OpenViewsOnStart()
    {
        var mode = _shell.Settings.DebugOpenDebug;
        var open = mode == "openOnSessionStart" || (mode == "openOnFirstSessionStart" && !_openedOnce);
        var first = !_openedOnce;
        _openedOnce = true;
        if (open) _shell.Navigate(Shell.ShellRoutes.Debug);
        if (first) _shell.BottomPanel.Show(Shell.PanelIds.DebugConsole);
    }

    private void OnSessionEnded(DebugSession session)
    {
        foreach (var key in _stacks.Keys.Where(k => k.Session == session).ToList()) _stacks.Remove(key);
        if (_current is { } c && c.Session == session) _current = null;
        if (Manager.Sessions.Count == 0)
        {
            _stopVersion++;
            Variables.Clear();
            ClearInline();
            _ = ReevaluateWatchesAsync(null);
        }

        UpdateState();
        RebuildCallStack();
        RefreshBreakpointRows();
        RefreshEditorMarks();
        Console.Write("console", $"{session.Name} ended.\n");
    }

    private void OnSessionChanged(DebugSession session)
    {
        if (session.State == DebugSessionState.Running)
        {
            if (_current is { } c && c.Session == session)
            {
                _current = null;
                _stopVersion++;
                Variables.Clear();
                ClearInline();
                RefreshEditorMarks();
            }

            foreach (var key in _stacks.Keys.Where(k => k.Session == session).ToList()) _stacks.Remove(key);
        }

        UpdateState();
        RebuildCallStack();
        RefreshBreakpointRows();
    }

    private void UpdateState()
    {
        var active = Manager.Active;
        HasSession = Manager.Sessions.Count > 0;
        IsStopped = active?.State == DebugSessionState.Stopped;
        IsRunning = active?.State == DebugSessionState.Running;
        StatusText = active is null ? "" : active.State switch
        {
            DebugSessionState.Starting => "Starting…",
            DebugSessionState.Stopped => active.Stop?.Title ?? "Paused",
            DebugSessionState.Running => "Running",
            _ => "",
        };
    }

    private void OnOutput(DebugSession session, DapOutput output)
    {
        var path = output.Source?.Path;
        Console.Write(DebugConsoleViewModel.CategoryOf(output), output.Text, path, output.Line ?? 0);
    }

    // ── Rows ─────────────────────────────────────────────────────────────────────────────────────

    private void OnBreakpointsChanged(BreakpointsChanged change) => _post(() =>
    {
        RefreshBreakpointRows();
        RefreshEditorMarks(change.Files);
        ScheduleSave();
    });

    /// <summary>The Breakpoints list rebuilt from the store (and what the sessions say about each).</summary>
    public void RefreshBreakpointRows()
    {
        _rebuilding = true;
        try
        {
            BreakpointRows.Clear();
            foreach (var f in Breakpoints.Filters)
            {
                BreakpointRows.Add(new BreakpointRow(BreakpointRowKind.Exception, f.Label) { Filter = f.Filter, Detail = f.Condition is { } c ? "when " + c : "", IsEnabled = f.Enabled });
            }

            foreach (var f in Breakpoints.Functions)
            {
                BreakpointRows.Add(new BreakpointRow(BreakpointRowKind.Function, f.Name) { Id = f.Id, Detail = string.Join(", ", new[] { f.Condition, f.HitCondition is { } h ? "hit count " + h : null }.Where(s => !string.IsNullOrEmpty(s))), IsEnabled = f.Enabled });
            }

            foreach (var b in Breakpoints.Source.OrderBy(b => b.Path, StringComparer.Ordinal).ThenBy(b => b.Line))
            {
                var result = ResultOf(b.Id);
                BreakpointRows.Add(new BreakpointRow(BreakpointRowKind.Source, Path.GetFileName(b.Path))
                {
                    Id = b.Id,
                    Path = b.Path,
                    Line = b.Line,
                    Detail = $"{b.Line}" + (b.Summary.Length > 0 ? "  " + b.Summary : ""),
                    Style = b.Kind,
                    IsEnabled = b.Enabled,
                    Unverified = b.Enabled && result is { Verified: false },
                    Problem = result is { Verified: false } ? result.Message : null,
                });
            }
        }
        finally
        {
            _rebuilding = false;
        }

        foreach (var row in BreakpointRows) row.PropertyChanged += OnBreakpointRowChanged;
    }

    private void OnBreakpointRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_rebuilding || sender is not BreakpointRow row || e.PropertyName != nameof(BreakpointRow.IsEnabled)) return;
        switch (row.Kind)
        {
            case BreakpointRowKind.Source: Breakpoints.Update(row.Id, b => b with { Enabled = row.IsEnabled }); break;
            case BreakpointRowKind.Function: Breakpoints.UpdateFunction(row.Id, f => f with { Enabled = row.IsEnabled }); break;
            default: Breakpoints.SetFilter(row.Filter!, row.IsEnabled); break;
        }
    }

    /// <summary>What the sessions say about a breakpoint: the verdict of the one that has an answer (a verified one wins).</summary>
    public DapBreakpoint? ResultOf(int id)
    {
        DapBreakpoint? found = null;
        foreach (var session in Manager.Sessions)
        {
            if (session.ResultFor(id) is not { } r) continue;
            if (r.Verified) return r;
            found ??= r;
        }

        return found;
    }

    // ── Remembering ──────────────────────────────────────────────────────────────────────────────

    private void ScheduleSave()
    {
        if (_root is null) return;
        _saveTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick -= SaveNow;
        _saveTimer.Tick += SaveNow;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveNow(object? sender, EventArgs e)
    {
        _saveTimer?.Stop();
        SaveState();
    }

    /// <summary>The breakpoints, watches and chosen configuration written into the session state for this folder.</summary>
    public void SaveState()
    {
        if (_root is null) return;
        _shell.RememberDebugState(_root, Breakpoints.Snapshot(WatchExpressions, SelectedConfiguration?.Name));
    }

    /// <summary>A folder opened: its remembered breakpoints and watches come back, its launch.json is read.</summary>
    public void FolderChanged(string? root)
    {
        if (root == _root) return;
        if (_root is not null) SaveState();
        _root = root;
        WatchExpressions.Clear();
        Breakpoints.RemoveAll();
        Breakpoints.OfferFilters([]);
        string? chosen = null;
        if (root is not null && _shell.DebugStateFor(root) is { } state)
        {
            Breakpoints.Restore(state);
            WatchExpressions.AddRange(state.Watches);
            chosen = state.Configuration;
        }
        else Breakpoints.Restore(new DebugWorkspaceState());

        RefreshBreakpointRows();
        _ = ReloadLaunchFileAsync(chosen);
        _ = ReevaluateWatchesAsync(_current);
    }

    /// <summary>Routes the debugger handles itself: the Debug Console, an adapter to install.</summary>
    public bool TryNavigate(string route)
    {
        if (route == Shell.ShellRoutes.DebugConsole)
        {
            _shell.BottomPanel.Show(Shell.PanelIds.DebugConsole);
            return true;
        }

        if (!route.StartsWith("debug/install/", StringComparison.Ordinal)) return false;
        _ = OfferInstallAsync(route["debug/install/".Length..]);
        return true;
    }

    public void Dispose()
    {
        _saveTimer?.Stop();
        _ = Manager.DisposeAsync().AsTask();
    }
}
