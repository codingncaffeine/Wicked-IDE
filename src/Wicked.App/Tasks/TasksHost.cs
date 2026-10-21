using System.Collections.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Language;
using Wicked.App.Shell;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Core.Lsp;
using Wicked.Core.Tasks;

namespace Wicked.App.Tasks;

/// <summary>
/// Tasks in the window: the palette lists (Run Task, Run Build Task, Terminate…), the questions a task's inputs ask, the
/// editor's state handed to the variables, the problems sent to the Problems panel and the notices when a task fails.
/// The work itself is <see cref="TaskService"/>, on the thread pool; this class only talks to the window.
/// </summary>
public sealed partial class TasksHost : ObservableObject, ITaskEnvironment, IDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Action<Action> _post;
    private readonly OutputPanelTaskOutput _output;

    public TasksHost(ShellViewModel shell, Action<Action> post)
    {
        _shell = shell;
        _post = post;
        _output = new OutputPanelTaskOutput(shell.Output, name => shell.Navigate($"output/{name}"), post: post);
        Service = new TaskService(_output, this) { WorkspaceFolder = shell.Workbench.Root };
        Service.ProblemsChanged += OnProblems;
        Service.Finished += OnFinished;
        Service.RunningChanged += () => _post(ShowRunning);
        shell.Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Wicked.App.Workbench.Workbench.Root)) _post(() => Service.WorkspaceFolder = shell.Workbench.Root);
        };
    }

    public TaskService Service { get; }

    /// <summary>What the debug lane's preLaunchTask waits on (see <see cref="TaskService.RunTaskByLabelAsync"/>).</summary>
    public Task<bool> RunTaskByLabelAsync(string label, CancellationToken ct = default) => Service.RunTaskByLabelAsync(label, ct);

    public void Dispose()
    {
        _tick?.Stop();
        Service.Dispose();
    }

    // ── The status bar: what is running (a build's progress) ─────────────────────────────────────

    private DispatcherTimer? _tick;

    /// <summary>The running task's name and how long it has run ("build · 12 s"); empty when nothing runs.</summary>
    [ObservableProperty]
    private string _runningText = "";

    [ObservableProperty]
    private string _runningTip = "";

    /// <summary>Counted once a second while something runs (no animation: nothing ticks the UI thread every frame).</summary>
    private void ShowRunning()
    {
        var running = Service.Running.OrderBy(r => r.Started).ToList();
        if (running.Count == 0)
        {
            _tick?.Stop();
            RunningText = "";
            RunningTip = "";
            return;
        }

        var first = running[0];
        var seconds = (int)(DateTimeOffset.UtcNow - first.Started).TotalSeconds;
        var more = running.Count > 1 ? $" +{running.Count - 1}" : "";
        RunningText = first.IsBackground ? $"{first.Definition.Label}{more} · watching" : $"{first.Definition.Label}{more} · {seconds} s";
        RunningTip = "Running: " + string.Join(", ", running.Select(r => r.Definition.Label)) + ". Click to see the output.";
        _tick ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => ShowRunning());
        if (!_tick.IsEnabled) _tick.Start();
    }

    /// <summary>The status bar item: the oldest running task's output.</summary>
    [RelayCommand]
    private void ShowRunningOutput()
    {
        if (Service.Running.OrderBy(r => r.Started).FirstOrDefault() is { } run) _shell.Navigate($"output/{_output.ChannelName(run.Definition.Label)}");
    }

    // ── The window's side of a run ───────────────────────────────────────────────────────────────

    /// <summary>The editor as it is: its file, caret line and selection. Read on the UI thread.</summary>
    public Task<VariableContext> SnapshotAsync()
    {
        var done = new TaskCompletionSource<VariableContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            var root = _shell.Workbench.Root ?? "";
            var tab = _shell.Workbench.ActiveTab;
            if (tab?.Path is not { } path || tab.ViewFactory is not null)
            {
                done.SetResult(new VariableContext(root));
                return;
            }

            var doc = tab.Document;
            var line = doc.Text.Position(doc.Primary.Active).Line + 1;
            var selected = !doc.Primary.IsEmpty && doc.Primary.Length < 100_000 ? doc.Text.Substring(doc.Primary.Start, doc.Primary.Length) : "";
            done.SetResult(new VariableContext(root, path, line, selected));
        });
        return done.Task;
    }

    /// <summary>A task's input: a line typed or a choice picked in the palette; null when the user cancels.</summary>
    public Task<string?> AskAsync(TaskInput input, CancellationToken ct)
    {
        var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ct.Register(() => answer.TrySetResult(null));
        _ = answer.Task.ContinueWith(_ => registration.Dispose(), TaskScheduler.Default);
        _post(async () =>
        {
            try
            {
                if (input.Type == "pickString")
                {
                    var items = input.Options.Select(o => new QuickItem(o.Label, o.Label == o.Value ? "" : o.Value, "", () =>
                    {
                        answer.TrySetResult(o.Value);
                        return Task.CompletedTask;
                    })).ToList();
                    var selected = Math.Max(0, input.Options.ToList().FindIndex(o => o.Value == input.Default));
                    _shell.ShowChoices(input.Description, items, selected, () => answer.TrySetResult(null));
                }
                else if (_shell.Workbench.Dialogs is { } dialogs)
                {
                    answer.TrySetResult(await dialogs.AskNameAsync(input.Description, input.Default ?? ""));
                }
                else
                {
                    answer.TrySetResult(null);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Asking for the input '{input.Id}' failed: {ex.Message}");
                answer.TrySetResult(null);
            }
        });
        return answer.Task;
    }

    private void OnProblems(string owner, ImmutableArray<TaskProblem> problems)
    {
        // Items are built on the worker; the panel takes them on the UI thread.
        var items = problems.Select(p => new ProblemItem(p.File, p.Severity switch
        {
            ProblemSeverity.Warning => DiagnosticSeverity.Warning,
            ProblemSeverity.Info => DiagnosticSeverity.Information,
            _ => DiagnosticSeverity.Error,
        }, p.Message, p.Owner, p.Code, Math.Max(0, p.Line - 1), Math.Max(0, p.Column - 1))).ToList();
        _post(() => _shell.Language.Problems.SetOwner(owner, items));
    }

    private void OnFinished(TaskRun run, TaskRunResult result)
    {
        var label = run.Definition.Label;
        var route = $"output/{_output.ChannelName(label)}";
        var key = "task:" + label;
        _shell.Notices.Clear(key);
        if (result.Terminated) return;
        if (result.Error is not null) _shell.Notices.Post(new Notice(key, $"Task '{label}' could not run: {result.Error}", NoticeKind.Error, route));
        else if (result.ExitCode is not (null or 0)) _shell.Notices.Post(new Notice(key, $"Task '{label}' failed with exit code {result.ExitCode}", NoticeKind.Error, route));
        else if (!run.IsBackground) _shell.Notices.Post(new Notice(key, $"Task '{label}' finished", NoticeKind.Success, route));
    }

    // ── Commands ─────────────────────────────────────────────────────────────────────────────────

    public IEnumerable<WorkbenchCommand> Commands() =>
    [
        new("workbench.action.tasks.runTask", "Tasks", "Run Task...", null, RunTaskAsync),
        new("workbench.action.tasks.build", "Tasks", "Run Build Task...", "Ctrl+Shift+B", () => RunGroupAsync(TaskGroup.Build)),
        new("workbench.action.tasks.test", "Tasks", "Run Test Task...", null, () => RunGroupAsync(TaskGroup.Test)),
        new("workbench.action.tasks.terminate", "Tasks", "Terminate Task...", null, TerminateAsync),
        new("workbench.action.tasks.restartTask", "Tasks", "Restart Running Task...", null, RestartAsync),
        new("workbench.action.tasks.showTasks", "Tasks", "Show Running Tasks...", null, ShowRunningAsync),
        new("workbench.action.tasks.configureTaskRunner", "Tasks", "Configure Task...", null, ConfigureAsync),
        new("wicked.tasks.openFile", "Tasks", "Open tasks.json", null, OpenFileAsync),
    ];

    /// <summary>tasks.json and the detected tasks, with the file's problems posted as a notice; null with no folder open.</summary>
    private async Task<TaskCatalog?> LoadAsync()
    {
        if (_shell.Workbench.Root is not { } root)
        {
            _shell.Notices.Post(new Notice("tasks-folder", "Open a folder to run its tasks", NoticeKind.Info));
            return null;
        }

        Service.WorkspaceFolder = root;
        var catalog = await Service.LoadAsync();
        if (catalog.Problems.Length > 0) _shell.Notices.Post(new Notice("tasks-file", "tasks.json: " + catalog.Problems[0], NoticeKind.Warning, $"tab/{TasksFile.PathIn(root)}"));
        else _shell.Notices.Clear("tasks-file");
        return catalog;
    }

    private static string DetailOf(TaskDefinition t) => t.Detected ? $"{t.Source}: {t.Detail}" : t.DependsOn.Length > 0 && t.Command is null ? "depends on " + string.Join(", ", t.DependsOn) : t.CommandText;

    private QuickItem ItemFor(TaskDefinition task, TaskCatalog catalog) =>
        new(task.Label, DetailOf(task), "", () => StartAsync(task, catalog));

    /// <summary>Starts a task; one already running offers to stop or restart it instead.</summary>
    public Task StartAsync(TaskDefinition task, TaskCatalog catalog)
    {
        if (Service.IsRunning(task.Label))
        {
            var items = new List<QuickItem>
            {
                new("Terminate Task", $"'{task.Label}' is already running", "", () =>
                {
                    Service.Terminate(task.Label);
                    return Task.CompletedTask;
                }),
                new("Restart Task", "", "", async () => await Service.RestartAsync(task.Label)),
                new("Show Output", "", "", () =>
                {
                    _shell.Navigate($"output/{_output.ChannelName(task.Label)}");
                    return Task.CompletedTask;
                }),
            };
            _shell.ShowChoices($"The task '{task.Label}' is already running", items, 0, null);
            return Task.CompletedTask;
        }

        Service.Start(task, catalog);
        return Task.CompletedTask;
    }

    private async Task RunTaskAsync()
    {
        if (await LoadAsync() is not { } catalog) return;
        var tasks = catalog.All.ToList();
        if (tasks.Count == 0)
        {
            _shell.Notices.Post(new Notice("tasks-none", "No tasks found: add a .vscode/tasks.json (Tasks: Configure Task)", NoticeKind.Info));
            return;
        }

        _shell.ShowChoices("Select the task to run", [.. tasks.Select(t => ItemFor(t, catalog))], 0, null);
    }

    /// <summary>Ctrl+Shift+B / Run Test Task: the group's default task runs at once; without one, the group's tasks are listed.</summary>
    private async Task RunGroupAsync(TaskGroup group)
    {
        if (await LoadAsync() is not { } catalog) return;
        var name = group == TaskGroup.Build ? "build" : "test";
        if (catalog.DefaultFor(group) is { } chosen)
        {
            await StartAsync(chosen, catalog);
            return;
        }

        var inGroup = catalog.InGroup(group).ToList();
        if (inGroup.Count == 0)
        {
            var all = catalog.All.ToList();
            if (all.Count == 0)
            {
                _shell.Notices.Post(new Notice("tasks-none", $"No {name} task found: add a .vscode/tasks.json (Tasks: Configure Task)", NoticeKind.Info));
                return;
            }

            _shell.ShowChoices($"No {name} task is set; select the task to run", [.. all.Select(t => ItemFor(t, catalog))], 0, null);
            return;
        }

        _shell.ShowChoices($"Select the {name} task to run", [.. inGroup.Select(t => ItemFor(t, catalog))], 0, null);
    }

    private Task TerminateAsync()
    {
        var running = Service.Running;
        if (running.Count == 0)
        {
            _shell.Notices.Post(new Notice("tasks-running", "No task is running", NoticeKind.Info));
            return Task.CompletedTask;
        }

        if (running.Count == 1)
        {
            Service.Terminate(running[0].Definition.Label);
            return Task.CompletedTask;
        }

        var items = running.Select(r => new QuickItem(r.Definition.Label, "", "", () =>
        {
            Service.Terminate(r.Definition.Label);
            return Task.CompletedTask;
        })).ToList();
        items.Insert(0, new QuickItem("All running tasks", "", "", () =>
        {
            Service.TerminateAll();
            return Task.CompletedTask;
        }));
        _shell.ShowChoices("Select a task to terminate", items, 0, null);
        return Task.CompletedTask;
    }

    private async Task RestartAsync()
    {
        var running = Service.Running;
        if (running.Count == 0)
        {
            _shell.Notices.Post(new Notice("tasks-running", "No task is running", NoticeKind.Info));
            return;
        }

        if (running.Count == 1)
        {
            await Service.RestartAsync(running[0].Definition.Label);
            return;
        }

        _shell.ShowChoices("Select the task to restart", [.. running.Select(r => new QuickItem(r.Definition.Label, "", "", async () => await Service.RestartAsync(r.Definition.Label)))], 0, null);
    }

    private Task ShowRunningAsync()
    {
        var running = Service.Running;
        if (running.Count == 0)
        {
            _shell.Notices.Post(new Notice("tasks-running", "No task is running", NoticeKind.Info));
            return Task.CompletedTask;
        }

        _shell.ShowChoices("Running tasks", [.. running.Select(r => new QuickItem(r.Definition.Label, r.Definition.CommandText, "", () =>
        {
            _shell.Navigate($"output/{_output.ChannelName(r.Definition.Label)}");
            return Task.CompletedTask;
        }))], 0, null);
        return Task.CompletedTask;
    }

    /// <summary>Configure Task: a detected task written into tasks.json (the file is made when there is none) and opened.</summary>
    private async Task ConfigureAsync()
    {
        if (await LoadAsync() is not { } catalog) return;
        var candidates = catalog.Detected.ToList();
        if (candidates.Count == 0)
        {
            _shell.Notices.Post(new Notice("tasks-none", "No tasks were found to configure; Tasks: Open tasks.json starts an empty file", NoticeKind.Info));
            return;
        }

        _shell.ShowChoices("Select a task to configure", [.. candidates.Select(t => new QuickItem(t.Label, DetailOf(t), "", () => WriteTaskAsync(catalog.Root, t)))], 0, null);
    }

    private async Task WriteTaskAsync(string root, TaskDefinition task)
    {
        var path = TasksFile.PathIn(root);
        var problem = await Task.Run(async () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
            {
                await Wicked.Core.Files.TextFile.WriteAsync(path, TasksFile.Starter(task), Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None);
                return null;
            }

            var existing = await File.ReadAllTextAsync(path);
            if (TasksFile.Parse(existing).Tasks.Any(t => t.Label == task.Label)) return null;
            if (TasksFile.AddTask(existing, task) is not { } updated) return "tasks.json could not be read, so the task was not added";
            await Wicked.Core.Files.TextFile.WriteAsync(path, updated, Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None);
            return null;
        });
        if (problem is not null) _shell.Notices.Post(new Notice("tasks-file", problem, NoticeKind.Error, $"tab/{path}"));
        else await _shell.Workbench.OpenAsync(path);
    }

    private async Task OpenFileAsync()
    {
        if (_shell.Workbench.Root is not { } root)
        {
            _shell.Notices.Post(new Notice("tasks-folder", "Open a folder to run its tasks", NoticeKind.Info));
            return;
        }

        var path = TasksFile.PathIn(root);
        if (!await Task.Run(() => File.Exists(path)))
        {
            const string Empty = "{\n  // Tasks for this folder, in VS Code's format: run them from the palette (Tasks: Run Task) or with Ctrl+Shift+B.\n  \"version\": \"2.0.0\",\n  \"tasks\": [\n  ]\n}\n";
            await Task.Run(() =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                return Wicked.Core.Files.TextFile.WriteAsync(path, Empty, Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None);
            });
        }

        await _shell.Workbench.OpenAsync(path);
    }
}
