using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace Wicked.Core.Debug;

public enum DebugSessionState
{
    /// <summary>Initialising, launching or attaching.</summary>
    Starting,

    Running,

    /// <summary>At least one thread is stopped.</summary>
    Stopped,

    /// <summary>The program and the adapter are gone.</summary>
    Ended,
}

/// <summary>What to start: <c>launch</c> or <c>attach</c>, the arguments for it (the configuration, variables resolved), and its name and type.</summary>
public sealed record DebugStartRequest(string Name, string Type, string Request, JsonElement Arguments);

/// <summary>A program started on the adapter's behalf, in a terminal of the client's making (the <c>runInTerminal</c> request).</summary>
public sealed record RunInTerminalArguments(string Kind, string? Title, string Cwd, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string?>? Env, bool ArgsCanBeInterpretedByShell = false);

/// <summary>What starting that program came to: its process id, and what ends it when the session does.</summary>
public sealed record RunInTerminalResult(int? ProcessId, IDisposable? Handle = null);

/// <summary>
/// One debug adapter and the program it debugs: the protocol's flow (initialise, launch or attach, send the breakpoints,
/// configuration done), the program's state as the adapter reports it (running, stopped on which threads), and the requests the
/// views make while it is stopped (stack, scopes, variables, evaluate). Its events fire on the adapter's reader task or a
/// worker, never on the UI thread; whoever shows them marshals them.
/// </summary>
public sealed class DebugSession : IAsyncDisposable
{
    /// <summary>How many frames a stack request asks for at a time.</summary>
    public const int FramePage = 20;

    private readonly DapConnection _connection;
    private readonly BreakpointStore _store;
    private readonly IAsyncDisposable? _lifetime;
    private readonly object _gate = new();
    private readonly HashSet<int> _stopped = [];
    private readonly List<DapThread> _threads = [];
    private readonly ConcurrentDictionary<int, DapBreakpoint> _results = new();
    private readonly ConcurrentDictionary<int, int> _adapterToOurs = new();
    private readonly Channel<BreakpointsChanged> _changes = Channel.CreateUnbounded<BreakpointsChanged>();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _lifeCts = new();
    private DapCapabilities _capabilities = new();
    private DebugStartRequest? _request;
    private DapStop? _stop;
    private int _state;
    private Task? _endTask;
    private int? _exitCode;
    private Task? _syncLoop;

    public DebugSession(int id, string name, string type, DapConnection connection, BreakpointStore store, IAsyncDisposable? lifetime = null, DebugSession? parent = null)
    {
        Id = id;
        Name = name;
        Type = type;
        Parent = parent;
        _connection = connection;
        _store = store;
        _lifetime = lifetime;
        connection.Closed += (_, _) => _ = EndAsync();
        Subscribe();
    }

    public int Id { get; }

    public string Name { get; }

    /// <summary>The configuration's <c>type</c> (coreclr, lldb, python, …): which adapter this is.</summary>
    public string Type { get; }

    public DebugSession? Parent { get; }

    public DebugSessionState State => (DebugSessionState)Volatile.Read(ref _state);

    public DapCapabilities Capabilities => _capabilities;

    /// <summary>The configuration this session was started with (kept so a restart can use it).</summary>
    public DebugStartRequest? Request => _request;

    public int? ExitCode => _exitCode;

    /// <summary>The last <c>stopped</c> event.</summary>
    public DapStop? Stop => _stop;

    /// <summary>The thread the user is looking at: the one that stopped last.</summary>
    public int? StoppedThreadId { get; private set; }

    public IReadOnlyList<DapThread> Threads
    {
        get
        {
            lock (_gate) return [.. _threads];
        }
    }

    public bool IsThreadStopped(int threadId)
    {
        lock (_gate) return _stopped.Contains(threadId);
    }

    /// <summary>The adapter's report on a source breakpoint of ours (verified, moved, why not), once it has sent one.</summary>
    public DapBreakpoint? ResultFor(int breakpointId) => _results.TryGetValue(breakpointId, out var r) ? r : null;

    public event Action? StateChanged;

    public event Action<DapStop>? Stopped;

    /// <summary>Execution went on (a thread id, or null for all threads).</summary>
    public event Action<int?>? Continued;

    public event Action? ThreadsChanged;

    public event Action<DapOutput>? Output;

    /// <summary>The adapter verified, moved or dropped a breakpoint (the id is ours, 0 for a function breakpoint).</summary>
    public event Action? BreakpointsResolved;

    /// <summary>Variables or stacks the adapter says are out of date (an <c>invalidated</c> event).</summary>
    public event Action? Invalidated;

    /// <summary>The session ended: the program exited or was stopped, or the adapter went away.</summary>
    public event Action? Ended;

    /// <summary>The adapter asks for another session (js-debug's workers, a compound's children): the manager starts it.</summary>
    public Func<DebugStartRequest, Task>? StartChild { get; set; }

    /// <summary>The adapter asks for a program to be run in a terminal; the manager answers.</summary>
    public Func<RunInTerminalArguments, CancellationToken, Task<RunInTerminalResult>>? RunInTerminal { get; set; }

    // ── Starting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The protocol's opening: <c>initialize</c>, then <c>launch</c> or <c>attach</c> (whose answer many adapters hold back until
    /// the end), then, when the adapter says it is <c>initialized</c>, every breakpoint, then <c>configurationDone</c>.
    /// </summary>
    public async Task StartAsync(DebugStartRequest request, CancellationToken ct)
    {
        _request = request;
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _connection.OnEvent("initialized", _ => initialized.TrySetResult());
        _connection.Start();
        try
        {
            var body = await _connection.RequestAsync("initialize", new
            {
                clientID = "wicked",
                clientName = "Wicked IDE",
                adapterID = request.Type,
                locale = "en-US",
                linesStartAt1 = true,
                columnsStartAt1 = true,
                pathFormat = "path",
                supportsVariableType = true,
                supportsVariablePaging = true,
                supportsRunInTerminalRequest = true,
                supportsMemoryReferences = true,
                supportsProgressReporting = true,
                supportsInvalidatedEvent = true,
                supportsStartDebuggingRequest = true,
                supportsArgsCanBeInterpretedByShell = false,
                supportsMemoryEvent = false,
            }, ct).ConfigureAwait(false);
            _capabilities = DapJson.Read(body, new DapCapabilities());
            _connection.SupportsCancel = _capabilities.SupportsCancelRequest;
            if (_capabilities.ExceptionBreakpointFilters is { Count: > 0 } filters) _store.OfferFilters(filters);

            var launched = _connection.RequestAsync(request.Request, request.Arguments, ct);
            var first = await Task.WhenAny(initialized.Task, launched).ConfigureAwait(false);
            if (first == launched) await launched.ConfigureAwait(false);
            await initialized.Task.WaitAsync(ct).ConfigureAwait(false);

            await SendAllBreakpointsAsync(ct).ConfigureAwait(false);
            if (_capabilities.SupportsConfigurationDoneRequest) await _connection.RequestAsync("configurationDone", null, ct).ConfigureAwait(false);
            _ready.TrySetResult();
            _syncLoop = Task.Run(SyncLoopAsync);
            _store.Changed += OnStoreChanged;
            await launched.ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _state, (int)DebugSessionState.Running, (int)DebugSessionState.Starting) == (int)DebugSessionState.Starting) StateChanged?.Invoke();
        }
        catch
        {
            await EndAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void Subscribe()
    {
        _connection.OnEvent("stopped", body =>
        {
            var stop = new DapStop(
                Text(body, "reason") ?? "",
                Text(body, "description"),
                Text(body, "text"),
                body.ValueKind == JsonValueKind.Object && body.TryGetProperty("threadId", out var t) && t.TryGetInt32(out var tid) ? tid : null,
                body.ValueKind == JsonValueKind.Object && body.TryGetProperty("allThreadsStopped", out var all) && all.ValueKind == JsonValueKind.True,
                body.ValueKind == JsonValueKind.Object && body.TryGetProperty("hitBreakpointIds", out var hits) && hits.ValueKind == JsonValueKind.Array ? [.. hits.EnumerateArray().Select(h => h.GetInt32())] : []);
            lock (_gate)
            {
                if (stop.AllThreadsStopped) _stopped.UnionWith(_threads.Select(x => x.Id));
                if (stop.ThreadId is { } thread)
                {
                    _stopped.Add(thread);
                    StoppedThreadId = thread;
                }
                else if (stop.AllThreadsStopped && _threads.Count > 0) StoppedThreadId ??= _threads[0].Id;
                _stop = stop;
            }

            SetState(DebugSessionState.Stopped);
            Stopped?.Invoke(stop);
        });
        _connection.OnEvent("continued", body =>
        {
            int? thread = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("threadId", out var t) && t.TryGetInt32(out var tid) ? tid : null;
            var all = !(body.ValueKind == JsonValueKind.Object && body.TryGetProperty("allThreadsContinued", out var a) && a.ValueKind == JsonValueKind.False);
            MarkContinued(all ? null : thread);
            Continued?.Invoke(all ? null : thread);
        });
        _connection.OnEvent("thread", body =>
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("threadId", out var t) || !t.TryGetInt32(out var id)) return;
            var reason = Text(body, "reason");
            lock (_gate)
            {
                if (reason == "exited")
                {
                    _threads.RemoveAll(x => x.Id == id);
                    _stopped.Remove(id);
                }
                else if (_threads.All(x => x.Id != id)) _threads.Add(new DapThread(id, $"Thread {id}"));
            }

            ThreadsChanged?.Invoke();
        });
        _connection.OnEvent("output", body =>
        {
            Output?.Invoke(DapOutput.From(body));
        });
        _connection.OnEvent("exited", body =>
        {
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("exitCode", out var code) && code.TryGetInt32(out var c)) _exitCode = c;
            Output?.Invoke(new DapOutput("console", $"The program exited with code {_exitCode ?? 0}.\n"));
        });
        _connection.OnEvent("terminated", ignored => _ = EndAsync());
        _connection.OnEvent("capabilities", body =>
        {
            if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("capabilities", out var c)) _capabilities = _capabilities.Merge(c);
            _connection.SupportsCancel = _capabilities.SupportsCancelRequest;
        });
        _connection.OnEvent("breakpoint", body =>
        {
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("breakpoint", out var element)) return;
            var breakpoint = DapJson.Read(element, new DapBreakpoint());
            if (breakpoint.Id is not { } adapterId || !_adapterToOurs.TryGetValue(adapterId, out var ours)) return;
            _results[ours] = _results.TryGetValue(ours, out var old) ? Merge(old, breakpoint) : breakpoint;
            BreakpointsResolved?.Invoke();
        });
        _connection.OnEvent("invalidated", _ => Invalidated?.Invoke());

        _connection.OnRequest("runInTerminal", async (args, ct) =>
        {
            var request = DapJson.Read(args, new RunInTerminalArguments("integrated", null, "", [], null));
            var handler = RunInTerminal ?? throw new DapException("This client cannot run a program in a terminal", "runInTerminal");
            var result = await handler(request, ct).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(new { processId = result.ProcessId, shellProcessId = (int?)null }, DapJson.Options);
        });
        _connection.OnRequest("startDebugging", async (args, ct) =>
        {
            var handler = StartChild ?? throw new DapException("This client cannot start another debug session", "startDebugging");
            var request = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("request", out var r) ? r.GetString() ?? "launch" : "launch";
            var configuration = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("configuration", out var c) ? c.Clone() : default;
            var name = configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : Name;
            var type = configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString()! : Type;
            ct.ThrowIfCancellationRequested();
            await handler(new DebugStartRequest(name, type, request, configuration)).ConfigureAwait(false);
            return default;
        });
    }

    private static DapBreakpoint Merge(DapBreakpoint old, DapBreakpoint update) =>
        update with { Message = update.Message ?? old.Message, Line = update.Line ?? old.Line, Source = update.Source ?? old.Source };

    private static string? Text(JsonElement body, string name) =>
        body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private void MarkContinued(int? thread)
    {
        lock (_gate)
        {
            if (thread is { } t) _stopped.Remove(t);
            else _stopped.Clear();
        }

        SetState(_stopped.Count > 0 ? DebugSessionState.Stopped : DebugSessionState.Running);
    }

    private void SetState(DebugSessionState state)
    {
        if (State == DebugSessionState.Ended) return;
        if (Interlocked.Exchange(ref _state, (int)state) != (int)state) StateChanged?.Invoke();
    }

    // ── Breakpoints ──────────────────────────────────────────────────────────────────────────────

    private async Task SendAllBreakpointsAsync(CancellationToken ct)
    {
        foreach (var path in _store.Source.Select(b => b.Path).Distinct()) await SendFileAsync(path, ct).ConfigureAwait(false);
        await SendFunctionsAsync(ct).ConfigureAwait(false);
        await SendExceptionsAsync(ct).ConfigureAwait(false);
    }

    private async Task SendFileAsync(string path, CancellationToken ct)
    {
        var wanted = _store.ForFile(path).Where(b => b.Enabled).ToList();
        var body = await _connection.RequestAsync("setBreakpoints", new
        {
            source = new DapSource(System.IO.Path.GetFileName(path), path),
            breakpoints = wanted.Select(b => new { line = b.Line, condition = b.Condition, hitCondition = b.HitCondition, logMessage = b.LogMessage }),
            lines = wanted.Select(b => b.Line),
            sourceModified = false,
        }, ct).ConfigureAwait(false);
        var answered = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("breakpoints", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => DapJson.Read(e, new DapBreakpoint())).ToList() : [];
        var live = new HashSet<int>(wanted.Select(b => b.Id));
        foreach (var gone in _store.Source.Where(b => b.Path == path && !b.Enabled).Select(b => b.Id)) _results.TryRemove(gone, out _);
        for (var i = 0; i < wanted.Count; i++)
        {
            var result = i < answered.Count ? answered[i] : new DapBreakpoint(Verified: false, Message: "The debugger did not answer for this breakpoint");
            _results[wanted[i].Id] = result;
            if (result.Id is { } adapterId) _adapterToOurs[adapterId] = wanted[i].Id;
        }

        foreach (var stale in _results.Keys.Where(id => !live.Contains(id) && _store.Find(id) is null)) _results.TryRemove(stale, out _);
        BreakpointsResolved?.Invoke();
    }

    private async Task SendFunctionsAsync(CancellationToken ct)
    {
        if (!_capabilities.SupportsFunctionBreakpoints) return;
        var wanted = _store.Functions.Where(f => f.Enabled).ToList();
        await _connection.RequestAsync("setFunctionBreakpoints", new { breakpoints = wanted.Select(f => new { name = f.Name, condition = f.Condition, hitCondition = f.HitCondition }) }, ct).ConfigureAwait(false);
    }

    private async Task SendExceptionsAsync(CancellationToken ct)
    {
        var on = _store.Filters.Where(f => f.Enabled && _capabilities.ExceptionBreakpointFilters?.Any(o => o.Filter == f.Filter) == true).ToList();
        var withConditions = _capabilities.SupportsExceptionFilterOptions && on.Any(f => f.Condition is not null);
        await _connection.RequestAsync("setExceptionBreakpoints", withConditions
            ? new { filters = Array.Empty<string>(), filterOptions = on.Select(f => new { filterId = f.Filter, condition = f.Condition }) }
            : new { filters = on.Select(f => f.Filter) }, ct).ConfigureAwait(false);
    }

    private void OnStoreChanged(BreakpointsChanged change) => _changes.Writer.TryWrite(change);

    /// <summary>Edits to the breakpoints reach a running adapter: what changed in the next moments is sent once.</summary>
    private async Task SyncLoopAsync()
    {
        var ct = _lifeCts.Token;
        try
        {
            while (await _changes.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                await Task.Delay(80, ct).ConfigureAwait(false);
                var files = new HashSet<string>();
                bool functions = false, exceptions = false;
                while (_changes.Reader.TryRead(out var change))
                {
                    files.UnionWith(change.Files);
                    functions |= change.Functions;
                    exceptions |= change.Exceptions;
                }

                if (State == DebugSessionState.Ended) return;
                try
                {
                    foreach (var file in files) await SendFileAsync(file, ct).ConfigureAwait(false);
                    if (functions) await SendFunctionsAsync(ct).ConfigureAwait(false);
                    if (exceptions) await SendExceptionsAsync(ct).ConfigureAwait(false);
                }
                catch (DapException ex)
                {
                    Output?.Invoke(new DapOutput("stderr", $"The breakpoints could not be sent: {ex.Message}\n"));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ── Running ──────────────────────────────────────────────────────────────────────────────────

    private int ThreadOf(int? threadId) => threadId ?? StoppedThreadId ?? _threads.FirstOrDefault()?.Id ?? 1;

    public async Task ContinueAsync(int? threadId = null, CancellationToken ct = default)
    {
        var thread = ThreadOf(threadId);
        MarkContinued(null);
        await _connection.RequestAsync("continue", new { threadId = thread }, ct).ConfigureAwait(false);
    }

    public Task StepOverAsync(int? threadId = null, CancellationToken ct = default) => StepAsync("next", threadId, ct);

    public Task StepIntoAsync(int? threadId = null, CancellationToken ct = default) => StepAsync("stepIn", threadId, ct);

    public Task StepOutAsync(int? threadId = null, CancellationToken ct = default) => StepAsync("stepOut", threadId, ct);

    private async Task StepAsync(string command, int? threadId, CancellationToken ct)
    {
        var thread = ThreadOf(threadId);
        MarkContinued(thread);
        await _connection.RequestAsync(command, _capabilities.SupportsSteppingGranularity ? new { threadId = thread, granularity = "statement" } : new { threadId = thread }, ct).ConfigureAwait(false);
    }

    public Task PauseAsync(int? threadId = null, CancellationToken ct = default) => _connection.RequestAsync("pause", new { threadId = ThreadOf(threadId) }, ct);

    /// <summary>Asks the adapter to restart the program; false when it cannot (the manager then stops and starts again).</summary>
    public async Task<bool> RestartAsync(CancellationToken ct = default)
    {
        if (!_capabilities.SupportsRestartRequest || _request is null) return false;
        lock (_gate) _stopped.Clear();
        SetState(DebugSessionState.Running);
        await _connection.RequestAsync("restart", new { arguments = _request.Arguments }, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Stops debugging: a launched program is terminated (politely when the adapter has <c>terminate</c>, then <c>disconnect</c>), an
    /// attached one is let go. Whatever the adapter says, the connection and its process are gone afterwards.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (State != DebugSessionState.Ended)
        {
            var launched = _request?.Request != "attach";
            try
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                limit.CancelAfter(TimeSpan.FromSeconds(3));
                if (launched && _capabilities.SupportsTerminateRequest)
                {
                    await _connection.RequestAsync("terminate", new { restart = false }, limit.Token).ConfigureAwait(false);
                    await WaitEndedAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }

                if (State != DebugSessionState.Ended) await _connection.RequestAsync("disconnect", new { restart = false, terminateDebuggee = launched }, limit.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is DapException or OperationCanceledException or IOException)
            {
                // Whatever the adapter says, it is going away.
            }
        }

        await EndAsync().ConfigureAwait(false);
    }

    private async Task WaitEndedAsync(TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (State != DebugSessionState.Ended && DateTime.UtcNow < until) await Task.Delay(20).ConfigureAwait(false);
    }

    // ── Looking ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The threads the adapter has now (also kept as <see cref="Threads"/>).</summary>
    public async Task<IReadOnlyList<DapThread>> RefreshThreadsAsync(CancellationToken ct = default)
    {
        var body = await _connection.RequestAsync("threads", null, ct).ConfigureAwait(false);
        var list = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("threads", out var t) ? DapJson.Read<List<DapThread>>(t, []) : [];
        lock (_gate)
        {
            _threads.Clear();
            _threads.AddRange(list);
        }

        ThreadsChanged?.Invoke();
        return list;
    }

    /// <summary>A page of a thread's stack: the frames and how many there are in all (0 when the adapter does not say).</summary>
    public async Task<(IReadOnlyList<DapStackFrame> Frames, int Total)> StackTraceAsync(int threadId, int start = 0, int levels = FramePage, CancellationToken ct = default)
    {
        var body = await _connection.RequestAsync("stackTrace", new { threadId, startFrame = start, levels }, ct).ConfigureAwait(false);
        var frames = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("stackFrames", out var f) ? DapJson.Read<List<DapStackFrame>>(f, []) : [];
        var total = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("totalFrames", out var n) && n.TryGetInt32(out var count) ? count : 0;
        return (frames, total);
    }

    public async Task<IReadOnlyList<DapScope>> ScopesAsync(int frameId, CancellationToken ct = default)
    {
        var body = await _connection.RequestAsync("scopes", new { frameId }, ct).ConfigureAwait(false);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("scopes", out var s) ? DapJson.Read<List<DapScope>>(s, []) : [];
    }

    /// <summary>The children of a variables reference; <paramref name="filter"/> is <c>indexed</c> or <c>named</c> to ask for one kind, with <paramref name="start"/> and <paramref name="count"/> for a page.</summary>
    public async Task<IReadOnlyList<DapVariable>> VariablesAsync(int reference, string? filter = null, int? start = null, int? count = null, CancellationToken ct = default)
    {
        var body = await _connection.RequestAsync("variables", new { variablesReference = reference, filter, start, count }, ct).ConfigureAwait(false);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("variables", out var v) ? DapJson.Read<List<DapVariable>>(v, []) : [];
    }

    /// <summary>Evaluates an expression in a frame (or globally). <paramref name="context"/> is <c>repl</c>, <c>watch</c>, <c>hover</c> or <c>clipboard</c>.</summary>
    public async Task<DapEvaluation> EvaluateAsync(string expression, int? frameId, string context, CancellationToken ct = default)
    {
        var body = await _connection.RequestAsync("evaluate", new { expression, frameId, context }, ct).ConfigureAwait(false);
        return DapJson.Read(body, new DapEvaluation(""));
    }

    public async Task<DapVariable> SetVariableAsync(int reference, string name, string value, CancellationToken ct = default)
    {
        var body = await _connection.RequestAsync("setVariable", new { variablesReference = reference, name, value }, ct).ConfigureAwait(false);
        if (body.ValueKind != JsonValueKind.Object) return new DapVariable(name, value);
        string? Text(string property) => body.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        int? Number(string property) => body.TryGetProperty(property, out var p) && p.TryGetInt32(out var n) ? n : null;
        return new DapVariable(name, Text("value") ?? value, Text("type"), Number("variablesReference") ?? 0, Number("namedVariables"), Number("indexedVariables"));
    }

    /// <summary>Assigns to an expression (a watch, or the REPL's <c>a = 3</c>) when the adapter can; null when it cannot.</summary>
    public async Task<DapEvaluation?> SetExpressionAsync(string expression, string value, int? frameId, CancellationToken ct = default)
    {
        if (!_capabilities.SupportsSetExpression) return null;
        var body = await _connection.RequestAsync("setExpression", new { expression, value, frameId }, ct).ConfigureAwait(false);
        return DapJson.Read(body, new DapEvaluation(value));
    }

    public async Task<IReadOnlyList<DapSource>> LoadedSourcesAsync(CancellationToken ct = default)
    {
        if (!_capabilities.SupportsLoadedSourcesRequest) return [];
        var body = await _connection.RequestAsync("loadedSources", null, ct).ConfigureAwait(false);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("sources", out var s) ? DapJson.Read<List<DapSource>>(s, []) : [];
    }

    /// <summary>The text of a source the adapter serves itself (one with a source reference and no path).</summary>
    public async Task<string> SourceAsync(DapSource source, CancellationToken ct = default)
    {
        var body = await _connection.RequestAsync("source", new { source, sourceReference = source.SourceReference ?? 0 }, ct).ConfigureAwait(false);
        return body.ValueKind == JsonValueKind.Object && body.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
    }

    public async Task<DapExceptionInfo?> ExceptionInfoAsync(int threadId, CancellationToken ct = default)
    {
        if (!_capabilities.SupportsExceptionInfoRequest) return null;
        var body = await _connection.RequestAsync("exceptionInfo", new { threadId }, ct).ConfigureAwait(false);
        if (body.ValueKind != JsonValueKind.Object) return null;
        var details = body.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
        return new DapExceptionInfo(Text(body, "exceptionId") ?? "", Text(body, "description"), Text(body, "breakMode"), Text(details, "typeName"), Text(details, "fullTypeName"), Text(details, "stackTrace"));
    }

    /// <summary>Text for the Debug Console from the client itself (a message that is not the program's).</summary>
    public void Say(string category, string text) => Output?.Invoke(new DapOutput(category, text));

    // ── Ending ───────────────────────────────────────────────────────────────────────────────────

    private Task EndAsync()
    {
        lock (_gate) return _endTask ??= Task.Run(EndCoreAsync);
    }

    private async Task EndCoreAsync()
    {
        Interlocked.Exchange(ref _state, (int)DebugSessionState.Ended);
        _store.Changed -= OnStoreChanged;
        _ready.TrySetCanceled();
        _changes.Writer.TryComplete();
        try
        {
            await _lifeCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }

        lock (_gate)
        {
            _stopped.Clear();
        }

        StateChanged?.Invoke();
        Ended?.Invoke();
        try
        {
            if (!_connection.IsClosed)
            {
                // The adapter may still be waiting for its goodbye; it is given a moment, not a say.
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await _connection.RequestAsync("disconnect", new { restart = false, terminateDebuggee = _request?.Request != "attach" }, limit.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is DapException or OperationCanceledException or IOException)
                {
                }
            }
        }
        finally
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
            if (_lifetime is not null) await _lifetime.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        if (_syncLoop is not null)
        {
            try
            {
                await _syncLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifeCts.Dispose();
    }
}
