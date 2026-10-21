using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.Shell;
using Wicked.App.Terminal;
using Wicked.App.ViewModels;
using Wicked.App.Workbench;
using Wicked.Core;
using Wicked.Core.Run;
using Wicked.Core.Tasks;

namespace Wicked.App.Debug;

/// <summary>
/// The Run / Debug / Stop / Build control in the title bar, as JetBrains' run widget and Visual Studio's Start button: the
/// picker's choice (a launch.json configuration, the current file, a program the folder builds) and what each button does
/// with it. F5, Ctrl+F5 and Shift+F5 go through here too, so the keys and the buttons always agree. The choice is the Run
/// and Debug panel's, shared.
/// </summary>
public sealed partial class RunControl : ObservableObject
{
    /// <summary>The terminal "Run Current File" types into, reused run after run.</summary>
    public const string RunTerminalName = "Run";

    private readonly ShellViewModel _shell;
    private readonly DebugController _debug;
    private readonly Action<Action> _post;
    private TerminalInstanceViewModel? _runTerminal;
    private string? _startedTask;

    public RunControl(ShellViewModel shell, DebugController debug, Action<Action> post)
    {
        _shell = shell;
        _debug = debug;
        _post = post;
        debug.ExtraTargets = ExtraTargetsAsync;
        debug.StartChosen = DebugAsync;
        debug.PropertyChanged += OnDebuggerChanged;
        shell.Tasks.Service.RunningChanged += () => _post(Refresh);
    }

    /// <summary>The picker's choice; null before a folder is read (Run then means the current file).</summary>
    public ConfigurationChoice? Target => _debug.SelectedConfiguration;

    /// <summary>The picker's words: the choice's name.</summary>
    public string TargetText => Target?.Label ?? ConfigurationChoice.CurrentFileName;

    /// <summary>Debug works for the choice: everything but a task, which runs without the debugger.</summary>
    public bool CanDebug => Target?.Kind != RunTargetKind.Task;

    /// <summary>Something the control started is running: a debug session, the current file in the Run terminal, a task.</summary>
    public bool CanStop => _debug.HasSession || _runTerminal is { IsCommandRunning: true, HasExited: false } || RunningTask() is not null;

    public string RunTip => $"Run {TargetText} (Ctrl+F5)";

    public string DebugTip => CanDebug ? $"Debug {TargetText} (F5)" : $"{TargetText} runs without the debugger";

    private void OnDebuggerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DebugController.SelectedConfiguration) or nameof(DebugController.HasSession)) Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(Target));
        OnPropertyChanged(nameof(TargetText));
        OnPropertyChanged(nameof(CanDebug));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(RunTip));
        OnPropertyChanged(nameof(DebugTip));
    }

    private string? RunningTask() => _startedTask is { } label && _shell.Tasks.Service.IsRunning(label) ? label : null;

    /// <summary>The choices beyond launch.json: the current file always, then the programs the detectors found.</summary>
    private async Task<IReadOnlyList<ConfigurationChoice>> ExtraTargetsAsync()
    {
        var programs = await _shell.Tasks.TasksWithRoleAsync(TaskRole.Run);
        return [ConfigurationChoice.CurrentFile, .. programs.Select(t => new ConfigurationChoice(t.Label, t.Source, false, RunTargetKind.Task))];
    }

    // ── The buttons ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+F5 / ▶: the choice without the debugger.</summary>
    public async Task RunAsync()
    {
        switch (Target)
        {
            case { Kind: RunTargetKind.Task } task:
                await StartTaskAsync(task.Name);
                break;
            case { IsLaunch: true } launch:
                await _debug.StartAsync(launch.Name, noDebug: true);
                break;
            default:
                await RunCurrentFileAsync();
                break;
        }
    }

    /// <summary>F5 / the bug: the choice under the debugger, or with a session going, continue or pause it.</summary>
    public async Task DebugAsync()
    {
        if (_debug.HasSession)
        {
            await _debug.StartOrContinueAsync();
            return;
        }

        switch (Target)
        {
            case { Kind: RunTargetKind.Task } task:
                _shell.Notices.Post(new Notice("run:nodebug", $"{task.Name} is a task: it runs without the debugger", NoticeKind.Info));
                await StartTaskAsync(task.Name);
                break;
            case { IsLaunch: true } launch:
                await _debug.StartAsync(launch.Name);
                break;
            default:
                await DebugCurrentFileAsync();
                break;
        }
    }

    /// <summary>Shift+F5 / ■: the debug session ends, or the program in the Run terminal is interrupted, or the task stops.</summary>
    public async Task StopAsync()
    {
        if (_debug.HasSession)
        {
            await _debug.StopAsync();
            return;
        }

        if (_runTerminal is { IsCommandRunning: true, HasExited: false, Session: { } session })
        {
            session.Input("\u0003");
            return;
        }

        if (RunningTask() is { } label) _shell.Tasks.Service.Terminate(label);
    }

    /// <summary>The hammer: the folder's build (Ctrl+Shift+B).</summary>
    public Task BuildAsync() => _shell.RunCommandAsync("workbench.action.tasks.build");

    /// <summary>The picker: every choice, then the ways to add more.</summary>
    public async Task PickAsync()
    {
        await _debug.ReloadLaunchFileAsync();
        var items = _debug.Configurations.Select(c => new QuickItem(c.Label, c.Detail, "", () =>
        {
            _debug.SelectedConfiguration = c;
            return Task.CompletedTask;
        })).ToList();
        items.Add(new QuickItem("Add Configuration…", "launch.json", "", _debug.OpenLaunchJsonAsync));
        items.Add(new QuickItem("Run Task…", "tasks.json and the folder's tasks", "", () => _shell.RunCommandAsync("workbench.action.tasks.runTask")));
        var selected = Math.Max(0, _debug.Configurations.IndexOf(_debug.SelectedConfiguration!));
        _shell.ShowChoices("Select what Run and Debug start", items, selected, null);
    }

    private async Task StartTaskAsync(string label)
    {
        _startedTask = label;
        if (!await _shell.Tasks.StartByLabelAsync(label)) _shell.Notices.Post(new Notice("run:task", $"The task '{label}' is no longer there", NoticeKind.Warning));
        Refresh();
    }

    // ── The current file ─────────────────────────────────────────────────────────────────────────

    /// <summary>The active file with its path, or null (a notice says why) when there is no file to run.</summary>
    private async Task<EditorTab?> FileToRunAsync()
    {
        if (_shell.Workbench.ActiveTab is not { Path: not null, ViewFactory: null } tab)
        {
            _shell.Notices.Post(new Notice("run:file", "Open a file to run it", NoticeKind.Info));
            return null;
        }

        // Saved first, as VS Code saves before it runs the file.
        if (tab.IsDirty) await _shell.RunCommandAsync("workbench.action.files.save");
        return tab;
    }

    /// <summary>"Run Current File": the file's interpreter (or dotnet run, cargo run, a compile) typed into the Run terminal.</summary>
    public async Task RunCurrentFileAsync()
    {
        if (await FileToRunAsync() is not { Path: { } path } tab) return;
        var root = _shell.Workbench.Root;
        var language = tab.Language?.Id;
        var plan = await Task.Run(() => FileRunner.Plan(path, language, root, OperatingSystem.IsWindows()));
        if (plan is null)
        {
            _shell.Notices.Post(new Notice("run:file", $"Wicked cannot run {Path.GetFileName(path)} on its own: a launch.json or a task can (Run > Add Configuration)", NoticeKind.Info));
            return;
        }

        _shell.BottomPanel.Show(PanelIds.Terminal);
        var terminal = await _shell.Terminal.RunInNamedTerminal(RunTerminalName, shell => FileRunner.Format(plan, shell), root ?? Path.GetDirectoryName(path));
        if (terminal != _runTerminal)
        {
            if (_runTerminal is not null) _runTerminal.PropertyChanged -= OnRunTerminalChanged;
            _runTerminal = terminal;
            if (terminal is not null) terminal.PropertyChanged += OnRunTerminalChanged;
        }

        Refresh();
    }

    private void OnRunTerminalChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalInstanceViewModel.IsCommandRunning) or nameof(TerminalInstanceViewModel.ExitCode)) Refresh();
    }

    /// <summary>"Debug Current File": Python and JavaScript start under their debugger with no launch.json; other languages get one written.</summary>
    public async Task DebugCurrentFileAsync()
    {
        if (await FileToRunAsync() is not { Path: { } path } tab) return;
        if (FileRunner.LanguageOf(path, tab.Language?.Id) is { } language && await _debug.DebugCurrentFileAsync(language)) return;
        await _debug.CreateLaunchJsonAsync();
    }
}
