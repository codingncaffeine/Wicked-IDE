namespace Wicked.Core.Debug;

/// <summary>
/// Every debug session of the window at once — the one the user started, the children its adapter asked for, the sessions of a
/// compound. It finds each session's adapter, connects, starts the session and keeps the list; the session the user is
/// looking at (<see cref="Active"/>) is the last one started or the last one to stop. Events fire on workers: whoever shows
/// them marshals them to the UI thread.
/// </summary>
public sealed class DebugManager(BreakpointStore store, IDebugAdapterProvider adapters) : IAsyncDisposable
{
    /// <summary>How long a session may take to start (initialise, launch, breakpoints) before it is given up on.</summary>
    public static readonly TimeSpan StartLimit = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private readonly List<DebugSession> _sessions = [];
    private readonly Dictionary<DebugSession, (AdapterEndpoint Endpoint, List<IDisposable> Programs)> _owned = [];
    private readonly Dictionary<AdapterEndpoint, int> _users = [];
    private int _nextId;
    private DebugSession? _active;

    public BreakpointStore Breakpoints { get; } = store;

    public IReadOnlyList<DebugSession> Sessions
    {
        get
        {
            lock (_gate) return [.. _sessions];
        }
    }

    public DebugSession? Active
    {
        get
        {
            lock (_gate) return _active;
        }
    }

    public event Action<DebugSession>? SessionStarted;

    public event Action<DebugSession>? SessionEnded;

    /// <summary>The session the user is looking at changed (null when none is left).</summary>
    public event Action<DebugSession?>? ActiveChanged;

    /// <summary>A session's program or adapter wrote something for the Debug Console.</summary>
    public event Action<DebugSession, DapOutput>? Output;

    /// <summary>A session's state changed (starting, running, stopped, ended).</summary>
    public event Action<DebugSession>? SessionChanged;

    /// <summary>A session stopped: the views fetch its stack.</summary>
    public event Action<DebugSession, DapStop>? SessionStopped;

    /// <summary>Starts programs an adapter asks to have run in a terminal; the shell may set the terminal lane's.</summary>
    public Func<RunInTerminalArguments, Action<string, string>?, CancellationToken, Task<RunInTerminalResult>> RunInTerminal { get; set; } = DetachedProcess.RunAsync;

    /// <summary>Starts a session (a launch or an attach) and returns it once it is running or stopped. A failure leaves nothing behind.</summary>
    public async Task<DebugSession> StartAsync(DebugStartRequest request, DebugSession? parent = null, CancellationToken ct = default)
    {
        AdapterEndpoint endpoint;
        bool shared;
        lock (_gate)
        {
            // A child of the same kind uses its parent's adapter again (js-debug's workers connect to the same server).
            shared = parent is not null && parent.Type == request.Type && _owned.ContainsKey(parent);
            endpoint = shared ? _owned[parent!].Endpoint : null!;
        }

        if (!shared) endpoint = await adapters.ResolveAsync(request.Type, request.Arguments, ct).ConfigureAwait(false);
        AdapterChannel channel;
        try
        {
            channel = await endpoint.OpenAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            if (!shared) await endpoint.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var id = Interlocked.Increment(ref _nextId);
        var connection = new DapConnection(channel.Input, channel.Output);
        DebugSession? session = null;
        session = new DebugSession(id, request.Name, request.Type, connection, Breakpoints, new Lifetime(this, () => session!, channel.Owner), parent);
        lock (_gate)
        {
            _owned[session] = (endpoint, []);
            _users[endpoint] = _users.GetValueOrDefault(endpoint) + 1;
            _sessions.Add(session);
            _active = session;
        }

        Wire(session);
        SessionStarted?.Invoke(session);
        ActiveChanged?.Invoke(session);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(StartLimit);
        try
        {
            await session.StartAsync(request, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await session.StopAsync().ConfigureAwait(false);
            throw new IOException($"{request.Name} did not start within {StartLimit.TotalSeconds:0} seconds");
        }
        catch
        {
            await session.StopAsync().ConfigureAwait(false);
            throw;
        }

        return session;
    }

    private void Wire(DebugSession session)
    {
        session.StartChild = child => StartAsync(child, session);
        session.RunInTerminal = async (args, ct) =>
        {
            var result = await RunInTerminal(args, (category, text) => Output?.Invoke(session, new DapOutput(category, text)), ct).ConfigureAwait(false);
            if (result.Handle is not null)
            {
                lock (_gate)
                {
                    if (_owned.TryGetValue(session, out var owned)) owned.Programs.Add(result.Handle);
                    else result.Handle.Dispose();
                }
            }

            return result;
        };
        session.Output += o => Output?.Invoke(session, o);
        session.StateChanged += () => SessionChanged?.Invoke(session);
        session.Stopped += stop =>
        {
            bool changed;
            lock (_gate)
            {
                changed = _active != session;
                _active = session;
            }

            if (changed) ActiveChanged?.Invoke(session);
            SessionStopped?.Invoke(session, stop);
        };
        session.Ended += () => OnEnded(session);
    }

    private void OnEnded(DebugSession session)
    {
        List<IDisposable> programs;
        DebugSession? next;
        bool changed;
        lock (_gate)
        {
            _sessions.Remove(session);
            programs = _owned.TryGetValue(session, out var owned) ? owned.Programs : [];
            if (programs.Count > 0) programs = [.. programs];
            changed = _active == session;
            next = _sessions.FirstOrDefault(s => s == session.Parent) ?? _sessions.LastOrDefault();
            if (changed) _active = next;
        }

        foreach (var p in programs) p.Dispose();
        SessionEnded?.Invoke(session);
        if (changed) ActiveChanged?.Invoke(next);
    }

    /// <summary>Called when a session's adapter connection is gone: the endpoint ends with its last user.</summary>
    private async ValueTask Released(DebugSession session)
    {
        AdapterEndpoint? done = null;
        lock (_gate)
        {
            if (_owned.Remove(session, out var owned) && _users.TryGetValue(owned.Endpoint, out var count))
            {
                if (count <= 1)
                {
                    _users.Remove(owned.Endpoint);
                    done = owned.Endpoint;
                }
                else _users[owned.Endpoint] = count - 1;
            }
        }

        if (done is not null) await done.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Restarts a session: the adapter's own restart when it has one, otherwise stop and start again with the same configuration.</summary>
    public async Task<DebugSession> RestartAsync(DebugSession session, CancellationToken ct = default)
    {
        if (session.State != DebugSessionState.Ended && await session.RestartAsync(ct).ConfigureAwait(false)) return session;
        var request = session.Request ?? throw new InvalidOperationException("The session was never started");
        await session.StopAsync(ct).ConfigureAwait(false);
        return await StartAsync(request, session.Parent, ct).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        foreach (var session in Sessions.Reverse()) await session.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAllAsync().ConfigureAwait(false);

    /// <summary>What ends with a session: its channel, and the endpoint when it was the last to use it.</summary>
    private sealed class Lifetime(DebugManager manager, Func<DebugSession> session, IAsyncDisposable? channel) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            await manager.Released(session()).ConfigureAwait(false);
        }
    }
}
