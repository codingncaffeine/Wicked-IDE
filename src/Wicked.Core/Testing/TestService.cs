using System.Collections.Immutable;
using Wicked.Core.Tasks;

namespace Wicked.Core.Testing;

/// <summary>A node of the tree as a reader sees it: copied under the service's lock, so it can be shown on another thread.</summary>
public sealed record TestRow(string Id, string Name, string Detail, TestKind Kind, int Depth, bool HasChildren, TestState State, TimeSpan? Duration, string? Message, string? Stack, string? Output, string? File, int Line, int Tests, int Failed, int Passed);

/// <summary>
/// The test explorer's model: the projects every adapter finds, their tests, and the results of this session. Discovery
/// and runs happen on the thread pool, one run at a time; <see cref="Changed"/> is raised on the thread that changed the
/// tree, and readers take a copy with <see cref="Flatten"/>.
/// </summary>
public sealed class TestService : IDisposable
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _runs = new(1, 1);
    private readonly ITaskOutput? _output;
    private readonly ToolEnv _tools;
    private readonly string _workFolder;
    private List<TestNode> _roots = [];
    private Dictionary<string, TestNode> _index = new(StringComparer.Ordinal);
    private CancellationTokenSource? _current;
    private int _busy;

    public TestService(ITaskOutput? output = null, IReadOnlyList<ITestAdapter>? adapters = null, ToolEnv? tools = null, string? workFolder = null)
    {
        _output = output;
        Adapters = adapters ?? [new DotnetTestAdapter(), new CargoTestAdapter(), new GoTestAdapter(), new PytestAdapter(), new JsTestAdapter()];
        _tools = tools ?? new ToolEnv();
        _workFolder = workFolder ?? Path.Combine(Path.GetTempPath(), "wicked-tests");
    }

    public IReadOnlyList<ITestAdapter> Adapters { get; }

    public string? WorkspaceFolder { get; set; }

    /// <summary>The tree or a result changed.</summary>
    public event Action? Changed;

    /// <summary>Discovery or a run started (true) or ended (false).</summary>
    public event Action<bool>? BusyChanged;

    public bool IsBusy => Volatile.Read(ref _busy) > 0;

    public bool HasProjects
    {
        get
        {
            lock (_gate) return _roots.Count > 0;
        }
    }

    private TestWorkspace Workspace()
    {
        Directory.CreateDirectory(_workFolder);
        return new TestWorkspace(WorkspaceFolder ?? "", _tools, _workFolder);
    }

    private void Busy(bool on)
    {
        var now = on ? Interlocked.Increment(ref _busy) : Interlocked.Decrement(ref _busy);
        if (on && now == 1 || !on && now == 0) BusyChanged?.Invoke(on);
    }

    // ── Discovery ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Looks for tests with every adapter; the results of the last discovery are kept for nodes that are still there.</summary>
    public Task DiscoverAsync(CancellationToken ct = default) => Task.Run(() => DiscoverCoreAsync(ct), ct);

    private async Task DiscoverCoreAsync(CancellationToken ct)
    {
        if (WorkspaceFolder is not { Length: > 0 }) return;
        Busy(true);
        try
        {
            var ws = Workspace();
            var found = await Task.WhenAll(Adapters.Select(async a =>
            {
                try
                {
                    return await a.DiscoverAsync(ws, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Log.Warn($"Test discovery with {a.Name} failed: {ex.Message}");
                    return [];
                }
            })).ConfigureAwait(false);
            var roots = found.SelectMany(x => x).OrderBy(r => r.ProjectPath, StringComparer.Ordinal).ToList();
            lock (_gate)
            {
                var old = Index(_roots);
                foreach (var node in roots.SelectMany(Walk))
                {
                    SortGroups(node);
                    if (old.TryGetValue(node.Id, out var before) && node.Kind == TestKind.Test)
                    {
                        node.State = before.State;
                        node.Duration = before.Duration;
                        node.Message = before.Message;
                        node.Stack = before.Stack;
                        node.Output = before.Output;
                    }
                }

                _roots = roots;
                _index = Index(roots);
            }

            Changed?.Invoke();
        }
        finally
        {
            Busy(false);
        }
    }

    private static void SortGroups(TestNode node)
    {
        if (node.Kind != TestKind.Test) node.SortChildren();
    }

    private static IEnumerable<TestNode> Walk(TestNode node)
    {
        yield return node;
        foreach (var child in node.Children.ToList())
        {
            foreach (var n in Walk(child)) yield return n;
        }
    }

    private static Dictionary<string, TestNode> Index(IEnumerable<TestNode> roots)
    {
        var map = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        foreach (var n in roots.SelectMany(Walk)) map[n.Id] = n;
        return map;
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The tree as rows, depth first; a group in <paramref name="collapsed"/> shows no children.</summary>
    public ImmutableArray<TestRow> Flatten(ISet<string>? collapsed = null, string? filter = null)
    {
        lock (_gate)
        {
            var rows = ImmutableArray.CreateBuilder<TestRow>();
            foreach (var root in _roots) Add(root, 0);
            return rows.ToImmutable();

            bool Matches(TestNode n) => string.IsNullOrWhiteSpace(filter) || n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || n.Detail.Contains(filter, StringComparison.OrdinalIgnoreCase) || n.Children.Any(Matches);

            void Add(TestNode node, int depth)
            {
                if (!Matches(node)) return;
                var leaves = node.Leaves.ToList();
                rows.Add(new TestRow(node.Id, node.Name, node.Detail, node.Kind, depth, node.Children.Count > 0, node.Rollup(), node.Duration ?? SumDuration(leaves, node), node.Message, node.Stack, node.Output, node.File, node.Line,
                    leaves.Count, leaves.Count(l => l.State == TestState.Failed), leaves.Count(l => l.State == TestState.Passed)));
                if (collapsed is not null && collapsed.Contains(node.Id)) return;
                foreach (var child in node.Children) Add(child, depth + 1);
            }
        }
    }

    private static TimeSpan? SumDuration(List<TestNode> leaves, TestNode node)
    {
        if (node.Kind == TestKind.Test) return null;
        var times = leaves.Where(l => l.Duration is not null).Select(l => l.Duration!.Value).ToList();
        return times.Count == 0 ? null : times.Aggregate(TimeSpan.Zero, (a, b) => a + b);
    }

    public (int Total, int Passed, int Failed, int Skipped) Counts()
    {
        lock (_gate)
        {
            var leaves = _roots.SelectMany(r => r.Leaves).Where(l => l.Kind == TestKind.Test).ToList();
            return (leaves.Count, leaves.Count(l => l.State == TestState.Passed), leaves.Count(l => l.State == TestState.Failed), leaves.Count(l => l.State == TestState.Skipped));
        }
    }

    /// <summary>A copy of a node's details (for the details pane), or null when it is gone.</summary>
    public TestRow? Row(string id) => Flatten().FirstOrDefault(r => r.Id == id);

    public ITestAdapter? AdapterFor(string id)
    {
        lock (_gate) return _index.GetValueOrDefault(id)?.Root.Adapter;
    }

    /// <summary>Where a test's code is: the framework's own answer, or a search of the project's sources.</summary>
    public async Task<(string File, int Line)?> LocateAsync(string id, CancellationToken ct = default)
    {
        TestNode? node;
        lock (_gate) node = _index.GetValueOrDefault(id);
        if (node is null) return null;
        if (node.File is { Length: > 0 } f && node.Line > 0) return (f, node.Line);
        var adapter = node.Root.Adapter;
        if (adapter is null) return null;
        var found = await adapter.LocateAsync(node, ct).ConfigureAwait(false);
        if (found is { } place)
        {
            lock (_gate)
            {
                node.File = place.File;
                node.Line = place.Line;
            }
        }

        return found;
    }

    // ── Running ──────────────────────────────────────────────────────────────────────────────────

    public Task RunAllAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        List<string> ids;
        lock (_gate) ids = [.. _roots.Select(r => r.Id)];
        return RunCoreAsync(ids, ct);
    }, ct);

    public Task RunFailedAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        List<string> ids;
        lock (_gate) ids = [.. _roots.SelectMany(r => r.Leaves).Where(l => l.State == TestState.Failed && l.Kind == TestKind.Test).Select(l => l.Id)];
        return RunCoreAsync(ids, ct);
    }, ct);

    /// <summary>Stops the run in progress.</summary>
    public void Cancel()
    {
        lock (_gate) _current?.Cancel();
    }

    /// <summary>Runs the tests under the nodes given (a project, a group, a test), one project at a time.</summary>
    public Task RunAsync(IEnumerable<string> ids, CancellationToken ct = default)
    {
        var picked = ids.ToList();
        return Task.Run(() => RunCoreAsync(picked, ct), ct);
    }

    private async Task RunCoreAsync(List<string> ids, CancellationToken ct)
    {
        await _runs.WaitAsync(ct).ConfigureAwait(false);
        Busy(true);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var requests = new List<TestRunRequest>();
        lock (_gate)
        {
            _current = cts;
            var picked = ids.Select(i => _index.GetValueOrDefault(i)).OfType<TestNode>().Distinct().ToList();
            // A node below another picked node is already covered.
            var pickedIds = picked.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
            picked = [.. picked.Where(n => !HasPickedAncestor(n, pickedIds))];
            foreach (var byProject in picked.GroupBy(n => n.Root))
            {
                var project = byProject.Key;
                var leaves = byProject.SelectMany(n => n.Leaves).Where(l => l.Kind == TestKind.Test).DistinctBy(l => l.Id).ToList();
                var selected = leaves.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
                var all = byProject.Any(n => n == project) || project.Leaves.All(l => selected.Contains(l.Id));
                if (leaves.Count == 0 && !all) continue;
                foreach (var leaf in all ? project.Leaves : leaves)
                {
                    leaf.State = TestState.Queued;
                    leaf.Message = leaf.Stack = leaf.Output = null;
                    leaf.Duration = null;
                }

                requests.Add(new TestRunRequest(project, all ? [] : [.. byProject], all));
            }
        }

        Changed?.Invoke();
        var sink = _output?.Open("Test Results", new TaskPresentation(RevealMode.Silent, Clear: false));
        var failedTotal = 0;
        try
        {
            foreach (var request in requests)
            {
                if (cts.IsCancellationRequested) break;
                var runSink = new RunSink(this, sink);
                lock (_gate)
                {
                    foreach (var leaf in request.All ? request.Project.Leaves : request.Tests.SelectMany(t => t.Leaves)) leaf.State = TestState.Running;
                }

                Changed?.Invoke();
                sink?.Write($"> Running {request.Project.Name} ({request.Project.Adapter?.Name}) <");
                try
                {
                    await request.Project.Adapter!.RunAsync(Workspace(), request, runSink, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    // Stopped by the user: what the run never reported on is marked below.
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Xml.XmlException or System.Text.Json.JsonException)
                {
                    sink?.Write($"The test run could not be read: {ex.Message}");
                    lock (_gate)
                    {
                        foreach (var leaf in request.Project.Leaves.Where(l => l.State == TestState.Running)) leaf.State = TestState.Failed;
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        // What the run never reported on was not run (filtered out, or the run was stopped).
                        foreach (var leaf in (request.All ? request.Project.Leaves : request.Tests.SelectMany(t => t.Leaves)).Where(l => l.State is TestState.Running or TestState.Queued)) leaf.State = TestState.Unknown;
                    }
                }

                Changed?.Invoke();
            }

            var counts = Counts();
            failedTotal = counts.Failed;
        }
        finally
        {
            lock (_gate) _current = null;
            sink?.Write("");
            sink?.Write(cts.IsCancellationRequested ? "> Test run stopped <" : failedTotal > 0 ? $"> {failedTotal} test(s) failed <" : "> Test run finished <");
            sink?.Close(new TaskOutcome(failedTotal > 0 ? 1 : 0, cts.IsCancellationRequested, null, failedTotal, 0));
            Busy(false);
            _runs.Release();
            Changed?.Invoke();
        }
    }

    private static bool HasPickedAncestor(TestNode node, HashSet<string> picked)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
        {
            if (picked.Contains(p.Id)) return true;
        }

        return false;
    }

    private sealed class RunSink(TestService owner, ITaskSink? output) : ITestRunSink
    {
        private DateTime _lastChange = DateTime.MinValue;

        public void Output(string line) => output?.Write(line);

        public void Result(TestResult r)
        {
            lock (owner._gate)
            {
                if (!owner._index.TryGetValue(r.Id, out var node))
                {
                    if (r.ParentId is null || !owner._index.TryGetValue(r.ParentId, out var parent)) return;
                    node = new TestNode(r.Id, r.Name ?? r.Id, TestKind.Test) { Key = r.Name ?? r.Id };
                    parent.Add(node);
                    owner._index[r.Id] = node;
                }

                node.State = r.State;
                node.Duration = r.Duration;
                node.Message = r.Message;
                node.Stack = r.Stack;
                node.Output = r.Output;
            }

            // A long run reports in a stream; the window is told at most every 100 ms, and at the end of the run.
            var now = DateTime.UtcNow;
            if (now - _lastChange < TimeSpan.FromMilliseconds(100)) return;
            _lastChange = now;
            owner.Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        Cancel();
    }
}
