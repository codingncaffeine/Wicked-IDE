using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;

namespace Wicked.Core.Tasks;

/// <summary>How a run ended, as its output panel shows it.</summary>
public sealed record TaskOutcome(int? ExitCode, bool Terminated, string? Error, int Errors, int Warnings);

/// <summary>One task's output: lines from any thread, raw (colour codes kept, for a terminal that shows them), then Close.</summary>
public interface ITaskSink
{
    void Write(string line);

    void Close(TaskOutcome outcome);
}

/// <summary>
/// Where a task's output goes. Today it is a channel of the Output panel; when the terminal lands it is a terminal
/// session, and this interface is the one line that changes.
/// </summary>
public interface ITaskOutput
{
    /// <summary>A task is starting: its sink. Called on a worker.</summary>
    ITaskSink Open(string label, TaskPresentation presentation);
}

/// <summary>What a run needs from the window: the editor as it is now, and answers to the task's inputs.</summary>
public interface ITaskEnvironment
{
    /// <summary>The variables' context. Called on a worker; the implementation reads the editor on the UI thread.</summary>
    Task<VariableContext> SnapshotAsync();

    /// <summary>Asks the user for an input's value; null when they cancel.</summary>
    Task<string?> AskAsync(TaskInput input, CancellationToken ct);
}

/// <summary>Every task a folder offers: tasks.json first, then the detected ones that tasks.json does not name.</summary>
public sealed record TaskCatalog(string Root, ImmutableArray<TaskDefinition> Configured, ImmutableArray<TaskDefinition> Detected, ImmutableDictionary<string, TaskInput> Inputs, ImmutableArray<string> Problems)
{
    public static TaskCatalog Empty(string root) => new(root, [], [], ImmutableDictionary<string, TaskInput>.Empty, []);

    public IEnumerable<TaskDefinition> All => Configured.Concat(Detected);

    public TaskDefinition? Find(string label) =>
        Configured.FirstOrDefault(t => t.Label == label) ?? Detected.FirstOrDefault(t => t.Label == label);

    /// <summary>The tasks of a group, the default one first.</summary>
    public IEnumerable<TaskDefinition> InGroup(TaskGroup group) =>
        All.Where(t => t.Group == group).OrderByDescending(t => t.IsDefault);

    /// <summary>The group's default task: the one marked so, if exactly one is.</summary>
    public TaskDefinition? DefaultFor(TaskGroup group)
    {
        var marked = All.Where(t => t.Group == group && t.IsDefault).ToList();
        return marked.Count >= 1 ? marked[0] : null;
    }
}

/// <summary>A task that is running or has run: when it is ready, and how it ended.</summary>
public sealed class TaskRun
{
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<TaskRunResult> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal TaskRun(TaskDefinition definition, CancellationTokenSource cts)
    {
        Definition = definition;
        Cts = cts;
    }

    public TaskDefinition Definition { get; }

    /// <summary>
    /// True once a task is ready for whatever waits on it: a normal task ended with exit code 0, a background task printed
    /// its end line. False when it ended without getting there.
    /// </summary>
    public Task<bool> Ready => _ready.Task;

    public Task<TaskRunResult> Completion => _done.Task;

    public bool IsBackground => Definition.IsBackground;

    internal CancellationTokenSource Cts { get; }

    internal TaskProcess? Process { get; set; }

    internal void SetReady(bool ok) => _ready.TrySetResult(ok);

    internal void SetDone(TaskRunResult result)
    {
        _ready.TrySetResult(result.Succeeded);
        _done.TrySetResult(result);
    }
}

/// <summary>
/// Runs tasks: variables worked out, dependencies run first, a child process in its own process group, its output to the
/// task's sink, its problems to <see cref="ProblemsChanged"/>. Everything happens on the thread pool; events are raised
/// on whichever thread produced them, and the window marshals them.
/// </summary>
public sealed class TaskService : IDisposable
{
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(100);
    private readonly ITaskOutput _output;
    private readonly ITaskEnvironment _environment;
    private readonly ConcurrentDictionary<string, TaskRun> _running = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _env;
    private readonly bool _windows = OperatingSystem.IsWindows();

    public TaskService(ITaskOutput output, ITaskEnvironment environment, Func<string, string?>? env = null)
    {
        _output = output;
        _environment = environment;
        _env = env ?? Environment.GetEnvironmentVariable;
    }

    /// <summary>The folder tasks.json is read from and tasks run in.</summary>
    public string? WorkspaceFolder { get; set; }

    /// <summary>A task's problems changed (an empty list clears them). The owner is the task's label.</summary>
    public event Action<string, ImmutableArray<TaskProblem>>? ProblemsChanged;

    /// <summary>A task started or ended.</summary>
    public event Action? RunningChanged;

    /// <summary>A task ended (after its sink closed).</summary>
    public event Action<TaskRun, TaskRunResult>? Finished;

    public IReadOnlyList<TaskRun> Running => [.. _running.Values];

    public bool IsRunning(string label) => _running.ContainsKey(label);

    public TaskRun? RunningTask(string label) => _running.GetValueOrDefault(label);

    /// <summary>tasks.json and the detected tasks of the workspace folder, read on the thread pool.</summary>
    public async Task<TaskCatalog> LoadAsync(CancellationToken ct = default)
    {
        if (WorkspaceFolder is not { Length: > 0 } root) return TaskCatalog.Empty("");
        var file = await TasksFile.ReadAsync(root, ct).ConfigureAwait(false);
        var detected = await TaskDetector.DetectAsync(root, ct).ConfigureAwait(false);
        var names = file.Tasks.Select(t => t.Label).ToHashSet(StringComparer.Ordinal);
        return new TaskCatalog(root, file.Tasks, [.. detected.Where(d => !names.Contains(d.Label))], file.Inputs, file.Problems);
    }

    /// <summary>Starts a task (its dependencies first) and returns at once; a task already running is returned as it is.</summary>
    public TaskRun Start(TaskDefinition definition, TaskCatalog catalog)
    {
        var scope = new Scope(catalog);
        return StartIn(definition, scope, null, new HashSet<string>(StringComparer.Ordinal) { definition.Label });
    }

    /// <summary>
    /// What the debug lane's preLaunchTask waits on: the task named <paramref name="label"/> is started (or found running)
    /// and this returns when it is ready: a normal task ended with exit code 0, a background task printed its end line.
    /// False when there is no such task or it ended without getting ready.
    /// </summary>
    public async Task<bool> RunTaskByLabelAsync(string label, CancellationToken ct = default)
    {
        var catalog = await LoadAsync(ct).ConfigureAwait(false);
        if (catalog.Find(label) is not { } definition) return false;
        var run = _running.GetValueOrDefault(label) ?? Start(definition, catalog);
        return await run.Ready.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stops a running task and everything it started; false when nothing by that label is running.</summary>
    public bool Terminate(string label)
    {
        if (!_running.TryGetValue(label, out var run)) return false;
        run.Cts.Cancel();
        run.Process?.Terminate();
        return true;
    }

    public void TerminateAll()
    {
        foreach (var label in _running.Keys) Terminate(label);
    }

    /// <summary>Stops a running task, waits for it to end, and starts it again.</summary>
    public async Task<TaskRun?> RestartAsync(string label, CancellationToken ct = default)
    {
        if (_running.TryGetValue(label, out var run))
        {
            Terminate(label);
            await run.Completion.WaitAsync(ct).ConfigureAwait(false);
        }

        var catalog = await LoadAsync(ct).ConfigureAwait(false);
        return catalog.Find(label) is { } definition ? Start(definition, catalog) : null;
    }

    public void Dispose() => TerminateAll();

    // ── Running ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One user action's runs: the tasks started so far (so a task two others depend on runs once) and the answers to inputs.</summary>
    private sealed class Scope(TaskCatalog catalog)
    {
        public TaskCatalog Catalog { get; } = catalog;

        public ConcurrentDictionary<string, TaskRun> Started { get; } = new(StringComparer.Ordinal);

        public ConcurrentDictionary<string, string> Answers { get; } = new(StringComparer.Ordinal);

        public Lazy<Task<VariableContext>>? Context { get; set; }
    }

    private TaskRun StartIn(TaskDefinition definition, Scope scope, CancellationTokenSource? parent, HashSet<string> chain)
    {
        if (_running.TryGetValue(definition.Label, out var existing)) return existing;
        var cts = parent is null ? new CancellationTokenSource() : CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var run = new TaskRun(definition, cts);
        if (!_running.TryAdd(definition.Label, run))
        {
            cts.Dispose();
            return _running[definition.Label];
        }

        scope.Started[definition.Label] = run;
        scope.Context ??= new Lazy<Task<VariableContext>>(() => _environment.SnapshotAsync());
        RunningChanged?.Invoke();
        _ = Task.Run(async () =>
        {
            TaskRunResult result;
            try
            {
                result = await RunAsync(run, scope, chain).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = new TaskRunResult(null, true, null, []);
            }
            catch (Exception ex)
            {
                Log.Warn($"Task '{definition.Label}' failed unexpectedly: {ex.Message}");
                result = new TaskRunResult(null, false, ex.Message, []);
            }

            _running.TryRemove(new KeyValuePair<string, TaskRun>(definition.Label, run));
            run.SetDone(result);
            RunningChanged?.Invoke();
            Finished?.Invoke(run, result);
        });
        return run;
    }

    private async Task<TaskRunResult> RunAsync(TaskRun run, Scope scope, HashSet<string> chain)
    {
        var def = run.Definition;
        var ct = run.Cts.Token;
        if (def.DependsOn.Length > 0)
        {
            var failure = await RunDependenciesAsync(def, scope, run, chain, ct).ConfigureAwait(false);
            if (failure is not null) return failure;
        }

        if (def.Command is null) return new TaskRunResult(0, false, null, []);
        return await ExecuteAsync(run, scope, ct).ConfigureAwait(false);
    }

    private async Task<TaskRunResult?> RunDependenciesAsync(TaskDefinition def, Scope scope, TaskRun run, HashSet<string> chain, CancellationToken ct)
    {
        var deps = new List<TaskDefinition>();
        foreach (var label in def.DependsOn)
        {
            if (chain.Contains(label)) return new TaskRunResult(null, false, $"Task '{def.Label}' depends on '{label}', which depends on it", []);
            if (scope.Catalog.Find(label) is not { } dep) return new TaskRunResult(null, false, $"Task '{def.Label}' depends on '{label}', which does not exist", []);
            deps.Add(dep);
        }

        async Task<string?> FailureAsync(TaskDefinition dep)
        {
            // A dependency that is a dependency of two tasks, or is already running (a watcher), is waited for, not started twice.
            if (!scope.Started.TryGetValue(dep.Label, out var started) && !_running.TryGetValue(dep.Label, out started))
            {
                started = StartIn(dep, scope, run.Cts, new HashSet<string>(chain, StringComparer.Ordinal) { dep.Label });
            }

            if (await started.Ready.WaitAsync(ct).ConfigureAwait(false)) return null;
            var why = (await started.Completion.WaitAsync(ct).ConfigureAwait(false)).Error;
            return $"The task '{dep.Label}' that '{def.Label}' depends on did not succeed" + (why is null ? "" : $": {why}");
        }

        if (def.DependsOrder == DependsOrder.Sequence)
        {
            foreach (var dep in deps)
            {
                if (await FailureAsync(dep).ConfigureAwait(false) is { } failure) return new TaskRunResult(null, false, failure, []);
            }
        }
        else
        {
            var all = await Task.WhenAll(deps.Select(FailureAsync)).ConfigureAwait(false);
            if (all.FirstOrDefault(x => x is not null) is { } failure) return new TaskRunResult(null, false, failure, []);
        }

        return null;
    }

    private async Task<TaskRunResult> ExecuteAsync(TaskRun run, Scope scope, CancellationToken ct)
    {
        var def = run.Definition;
        var context = await scope.Context!.Value.ConfigureAwait(false);
        if (WorkspaceFolder is { Length: > 0 } folder && context.WorkspaceFolder.Length == 0) context = context with { WorkspaceFolder = folder };
        context = context with { Environment = _env };

        // Ask for every input the task mentions before anything starts.
        var texts = Mentioned(def).ToList();
        foreach (var id in texts.SelectMany(TaskVariables.InputsIn).Distinct(StringComparer.Ordinal))
        {
            if (scope.Answers.ContainsKey(id)) continue;
            if (!scope.Catalog.Inputs.TryGetValue(id, out var input)) return new TaskRunResult(null, false, $"The input '{id}' is not defined in tasks.json", []);
            var answer = await _environment.AskAsync(input, ct).ConfigureAwait(false);
            if (answer is null) return new TaskRunResult(null, true, null, []);
            scope.Answers[id] = answer;
        }

        context = context.WithInputs(scope.Answers.ToImmutableDictionary());
        var unresolved = new List<string>();
        string Expand(string text)
        {
            var e = TaskVariables.Expand(text, context);
            unresolved.AddRange(e.Unresolved);
            return e.Text;
        }

        var command = Expand(def.Command!.Value);
        var args = def.Args.Select(a => a with { Value = Expand(a.Value) }).ToImmutableArray();
        var cwd = def.Cwd is null ? context.WorkspaceFolder : Expand(def.Cwd);
        var env = def.Env.ToDictionary(kv => kv.Key, kv => Expand(kv.Value), StringComparer.Ordinal);
        var matchers = def.Matchers.Select(m => m.FilePrefix is null ? m : m with { FilePrefix = Expand(m.FilePrefix) }).ToImmutableArray();
        if (unresolved.Count > 0)
        {
            var needsEditor = unresolved.Any(u => u.Contains("file", StringComparison.OrdinalIgnoreCase) || u.Contains("line", StringComparison.OrdinalIgnoreCase) || u.Contains("selected", StringComparison.OrdinalIgnoreCase));
            return await FailAsync(def, $"{string.Join(", ", unresolved)} could not be worked out{(needsEditor ? ": open a file in an editor first" : "")}").ConfigureAwait(false);
        }

        if (cwd.Length == 0) cwd = Directory.GetCurrentDirectory();
        else if (!Path.IsPathRooted(cwd)) cwd = Path.GetFullPath(Path.Combine(context.WorkspaceFolder, cwd));
        if (!Directory.Exists(cwd)) return await FailAsync(def, $"The folder {cwd} does not exist").ConfigureAwait(false);

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in env) environment[k] = v;
        ProcessSpec spec;
        string shown;
        if (def.Type == TaskType.Process)
        {
            var path = environment.TryGetValue("PATH", out var p) ? p : _env("PATH");
            var program = TaskProcess.Find(command, path, cwd);
            if (program is null) return await FailAsync(def, $"The program '{command}' was not found").ConfigureAwait(false);
            spec = new ProcessSpec(program, [.. args.Select(a => a.Value)], cwd, environment);
            shown = TaskCommandLine.Join(command, args, _windows);
        }
        else
        {
            shown = TaskCommandLine.Join(command, args, _windows);
            var (file, shellArgs) = TaskCommandLine.Shell(shown, def.Shell, _env, _windows);
            spec = new ProcessSpec(file, shellArgs, cwd, environment);
        }

        var sink = _output.Open(def.Label, def.Presentation);
        if (def.Presentation.Echo)
        {
            sink.Write($"> Executing task: {shown} <");
            sink.Write("");
        }

        var collector = new ProblemCollector(matchers, context.WorkspaceFolder, cwd);
        ProblemsChanged?.Invoke(def.Label, []);
        TaskProcess process;
        try
        {
            process = TaskProcess.Start(spec);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            var outcome = new TaskOutcome(null, false, ex.Message, 0, 0);
            sink.Write($"The task could not start: {ex.Message}");
            sink.Close(outcome);
            return new TaskRunResult(null, false, ex.Message, []);
        }

        using (process)
        {
            run.Process = process;
            using var registration = ct.Register(process.Terminate);
            if (ct.IsCancellationRequested) process.Terminate();
            // A watcher without begin/end lines is ready as soon as it runs; one with them waits for its end line.
            if (def.IsBackground && !collector.HasBackground) run.SetReady(true);
            var published = Stopwatch.StartNew();
            var dirty = false;
            await foreach (var (raw, _) in process.Lines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                sink.Write(raw);
                if (matchers.IsDefaultOrEmpty) continue;
                var e = collector.Feed(AnsiText.Strip(raw));
                if (e.Began)
                {
                    ProblemsChanged?.Invoke(def.Label, []);
                    dirty = false;
                }

                if (e.Problem is not null) dirty = true;
                if (e.Ended)
                {
                    ProblemsChanged?.Invoke(def.Label, [.. collector.Problems]);
                    dirty = false;
                    run.SetReady(true);
                }
                else if (dirty && published.Elapsed >= PublishEvery)
                {
                    ProblemsChanged?.Invoke(def.Label, [.. collector.Problems]);
                    published.Restart();
                    dirty = false;
                }
            }

            var exit = await process.WaitForExitAsync().ConfigureAwait(false);
            var problems = collector.Problems.ToImmutableArray();
            if (!matchers.IsDefaultOrEmpty) ProblemsChanged?.Invoke(def.Label, problems);
            var terminated = process.WasTerminated;
            var errors = problems.Count(x => x.Severity == ProblemSeverity.Error);
            var warnings = problems.Count(x => x.Severity == ProblemSeverity.Warning);
            sink.Write("");
            sink.Write(terminated ? "> Task terminated <" : exit == 0 ? "> Task finished <" : $"> Task failed with exit code {exit} <");
            sink.Close(new TaskOutcome(terminated ? null : exit, terminated, null, errors, warnings));
            return new TaskRunResult(terminated ? null : exit, terminated, null, problems);
        }
    }

    private async Task<TaskRunResult> FailAsync(TaskDefinition def, string message)
    {
        var sink = _output.Open(def.Label, def.Presentation);
        sink.Write(message);
        sink.Close(new TaskOutcome(null, false, message, 0, 0));
        await Task.CompletedTask.ConfigureAwait(false);
        return new TaskRunResult(null, false, message, []);
    }

    private static IEnumerable<string> Mentioned(TaskDefinition def)
    {
        if (def.Command is not null) yield return def.Command.Value;
        foreach (var a in def.Args) yield return a.Value;
        if (def.Cwd is not null) yield return def.Cwd;
        foreach (var v in def.Env.Values) yield return v;
        foreach (var m in def.Matchers)
        {
            if (m.FilePrefix is not null) yield return m.FilePrefix;
        }
    }
}
