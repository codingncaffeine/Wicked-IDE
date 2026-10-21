namespace Wicked.Core.Debug;

/// <summary>What a source breakpoint does when it is hit.</summary>
public enum BreakpointKind
{
    /// <summary>Stops.</summary>
    Plain,

    /// <summary>Stops only when a condition holds or a hit count is reached.</summary>
    Conditional,

    /// <summary>Writes a message and carries on.</summary>
    Logpoint,
}

/// <summary>
/// A breakpoint on a line of a file (lines count from 1). A logpoint's message may hold <c>{expressions}</c>; a hit
/// condition is the adapter's own syntax (<c>&gt; 5</c>, <c>== 3</c>, <c>% 2</c>, or a plain number).
/// </summary>
public sealed record SourceBreakpoint(int Id, string Path, int Line, string? Condition = null, string? HitCondition = null, string? LogMessage = null, bool Enabled = true)
{
    public BreakpointKind Kind => LogMessage is { Length: > 0 } ? BreakpointKind.Logpoint
        : Condition is { Length: > 0 } || HitCondition is { Length: > 0 } ? BreakpointKind.Conditional : BreakpointKind.Plain;

    /// <summary>The condition, hit count or message in one short phrase for a list ("x > 3", "hit count 5", "log: i = {i}").</summary>
    public string Summary => Kind switch
    {
        BreakpointKind.Logpoint => "Log: " + LogMessage,
        BreakpointKind.Conditional => string.Join(", ", new[] { Condition, HitCondition is { Length: > 0 } h ? "hit count " + h : null }.Where(s => !string.IsNullOrEmpty(s))),
        _ => "",
    };
}

/// <summary>A breakpoint on a function by name, for adapters that support them.</summary>
public sealed record FunctionBreakpoint(int Id, string Name, string? Condition = null, string? HitCondition = null, bool Enabled = true);

/// <summary>An exception filter the adapter offers ("All Exceptions", "Uncaught Exceptions") and whether it is on.</summary>
public sealed record ExceptionFilterSetting(string Filter, string Label, bool Enabled, string? Description = null, bool SupportsCondition = false, string? Condition = null);

/// <summary>What changed in the store, for the session (which files to send again) and the views.</summary>
public sealed record BreakpointsChanged(IReadOnlySet<string> Files, bool Functions, bool Exceptions);

/// <summary>What the debugger remembers about a workspace between runs.</summary>
public sealed record DebugWorkspaceState
{
    public List<SourceBreakpoint> Breakpoints { get; init; } = [];

    public List<FunctionBreakpoint> FunctionBreakpoints { get; init; } = [];

    public List<ExceptionFilterSetting> ExceptionFilters { get; init; } = [];

    public List<string> Watches { get; init; } = [];

    /// <summary>The configuration last chosen in the picker.</summary>
    public string? Configuration { get; init; }
}

/// <summary>
/// The workspace's breakpoints, kept apart from any session so they exist before one starts and outlive it. Every session
/// reads the store to send what it needs and listens to <see cref="Changed"/> to keep its adapter current; the editor keeps
/// the lines of open files up to date as the text is edited. Thread-safe; events are raised outside the lock.
/// </summary>
public sealed class BreakpointStore
{
    private readonly object _gate = new();
    private List<SourceBreakpoint> _source = [];
    private List<FunctionBreakpoint> _functions = [];
    private List<ExceptionFilterSetting> _filters = [];
    private int _nextId = 1;

    public event Action<BreakpointsChanged>? Changed;

    /// <summary>Paths compare as written but with the separator and dot segments normalised.</summary>
    public static string Normalize(string path) => System.IO.Path.GetFullPath(path);

    public IReadOnlyList<SourceBreakpoint> Source
    {
        get
        {
            lock (_gate) return [.. _source];
        }
    }

    public IReadOnlyList<FunctionBreakpoint> Functions
    {
        get
        {
            lock (_gate) return [.. _functions];
        }
    }

    public IReadOnlyList<ExceptionFilterSetting> Filters
    {
        get
        {
            lock (_gate) return [.. _filters];
        }
    }

    public IReadOnlyList<SourceBreakpoint> ForFile(string path)
    {
        var full = Normalize(path);
        lock (_gate) return [.. _source.Where(b => b.Path == full).OrderBy(b => b.Line)];
    }

    public SourceBreakpoint? Find(int id)
    {
        lock (_gate) return _source.FirstOrDefault(b => b.Id == id);
    }

    public SourceBreakpoint? At(string path, int line)
    {
        var full = Normalize(path);
        lock (_gate) return _source.FirstOrDefault(b => b.Path == full && b.Line == line);
    }

    /// <summary>Adds a breakpoint, or returns the one already on that line.</summary>
    public SourceBreakpoint Add(string path, int line, string? condition = null, string? hitCondition = null, string? logMessage = null)
    {
        var full = Normalize(path);
        SourceBreakpoint result;
        lock (_gate)
        {
            if (_source.FirstOrDefault(b => b.Path == full && b.Line == line) is { } existing) return existing;
            result = new SourceBreakpoint(_nextId++, full, line, Blank(condition), Blank(hitCondition), Blank(logMessage));
            _source.Add(result);
        }

        Raise(full);
        return result;
    }

    /// <summary>The gutter click: removes the breakpoint on the line, or adds one. Returns the new one, or null when it was removed.</summary>
    public SourceBreakpoint? Toggle(string path, int line)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            if (_source.FirstOrDefault(b => b.Path == full && b.Line == line) is { } existing)
            {
                _source.Remove(existing);
                Raise(full);
                return null;
            }
        }

        return Add(full, line);
    }

    public void Update(int id, Func<SourceBreakpoint, SourceBreakpoint> change)
    {
        string path;
        lock (_gate)
        {
            var index = _source.FindIndex(b => b.Id == id);
            if (index < 0) return;
            var old = _source[index];
            var updated = change(old) with { Id = id };
            updated = updated with { Condition = Blank(updated.Condition), HitCondition = Blank(updated.HitCondition), LogMessage = Blank(updated.LogMessage) };
            if (updated == old) return;
            _source[index] = updated;
            path = updated.Path;
        }

        Raise(path);
    }

    public void Remove(int id)
    {
        string path;
        lock (_gate)
        {
            var found = _source.FirstOrDefault(b => b.Id == id);
            if (found is null) return;
            _source.Remove(found);
            path = found.Path;
        }

        Raise(path);
    }

    public void RemoveAll()
    {
        HashSet<string> files;
        bool functions;
        lock (_gate)
        {
            files = [.. _source.Select(b => b.Path)];
            functions = _functions.Count > 0;
            _source.Clear();
            _functions.Clear();
        }

        if (files.Count > 0 || functions) Changed?.Invoke(new BreakpointsChanged(files, functions, false));
    }

    public void SetAllEnabled(bool enabled)
    {
        HashSet<string> files;
        bool functions;
        lock (_gate)
        {
            files = [.. _source.Where(b => b.Enabled != enabled).Select(b => b.Path)];
            functions = _functions.Any(f => f.Enabled != enabled);
            _source = [.. _source.Select(b => b with { Enabled = enabled })];
            _functions = [.. _functions.Select(f => f with { Enabled = enabled })];
        }

        if (files.Count > 0 || functions) Changed?.Invoke(new BreakpointsChanged(files, functions, false));
    }

    /// <summary>
    /// The lines of a file's breakpoints after its text was edited: each id's new line (an id missing from <paramref name="lines"/>
    /// is dropped, as when its line was deleted). Two breakpoints landing on one line keep the first.
    /// </summary>
    public void Relocate(string path, IReadOnlyDictionary<int, int> lines)
    {
        var full = Normalize(path);
        lock (_gate)
        {
            var next = new List<SourceBreakpoint>();
            var taken = new HashSet<int>();
            var changed = false;
            foreach (var b in _source)
            {
                if (b.Path != full)
                {
                    next.Add(b);
                    continue;
                }

                if (!lines.TryGetValue(b.Id, out var line) || !taken.Add(line))
                {
                    changed = true;
                    continue;
                }

                if (line != b.Line) changed = true;
                next.Add(b with { Line = line });
            }

            if (!changed) return;
            _source = next;
        }

        Raise(full);
    }

    // ── Function breakpoints ─────────────────────────────────────────────────────────────────────

    public FunctionBreakpoint AddFunction(string name, string? condition = null, string? hitCondition = null)
    {
        FunctionBreakpoint result;
        lock (_gate)
        {
            if (_functions.FirstOrDefault(f => f.Name == name) is { } existing) return existing;
            result = new FunctionBreakpoint(_nextId++, name.Trim(), Blank(condition), Blank(hitCondition));
            _functions.Add(result);
        }

        Changed?.Invoke(new BreakpointsChanged(new HashSet<string>(), true, false));
        return result;
    }

    public void UpdateFunction(int id, Func<FunctionBreakpoint, FunctionBreakpoint> change)
    {
        lock (_gate)
        {
            var index = _functions.FindIndex(f => f.Id == id);
            if (index < 0) return;
            var updated = change(_functions[index]) with { Id = id };
            if (updated == _functions[index]) return;
            _functions[index] = updated with { Condition = Blank(updated.Condition), HitCondition = Blank(updated.HitCondition) };
        }

        Changed?.Invoke(new BreakpointsChanged(new HashSet<string>(), true, false));
    }

    public void RemoveFunction(int id)
    {
        lock (_gate)
        {
            if (_functions.RemoveAll(f => f.Id == id) == 0) return;
        }

        Changed?.Invoke(new BreakpointsChanged(new HashSet<string>(), true, false));
    }

    // ── Exception filters ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The filters an adapter offers. A filter already known keeps the user's choice (and condition); a new one starts as the adapter's
    /// default. Filters the adapter does not offer are kept in the list, so another adapter's choices survive a run with this one.
    /// </summary>
    public void OfferFilters(IEnumerable<ExceptionBreakpointsFilter> offered)
    {
        lock (_gate)
        {
            var merged = new List<ExceptionFilterSetting>();
            foreach (var f in offered)
            {
                var known = _filters.FirstOrDefault(k => k.Filter == f.Filter);
                merged.Add(new ExceptionFilterSetting(f.Filter, f.Label, known?.Enabled ?? f.Default, f.Description, f.SupportsCondition, known?.Condition));
            }

            merged.AddRange(_filters.Where(k => merged.All(m => m.Filter != k.Filter)));
            if (merged.SequenceEqual(_filters)) return;
            _filters = merged;
        }

        Changed?.Invoke(new BreakpointsChanged(new HashSet<string>(), false, true));
    }

    public void SetFilter(string filter, bool enabled, string? condition = null)
    {
        lock (_gate)
        {
            var index = _filters.FindIndex(f => f.Filter == filter);
            if (index < 0) return;
            var updated = _filters[index] with { Enabled = enabled, Condition = Blank(condition) ?? _filters[index].Condition };
            if (updated == _filters[index]) return;
            _filters[index] = updated;
        }

        Changed?.Invoke(new BreakpointsChanged(new HashSet<string>(), false, true));
    }

    public void SetFilterCondition(string filter, string? condition)
    {
        lock (_gate)
        {
            var index = _filters.FindIndex(f => f.Filter == filter);
            if (index < 0) return;
            _filters[index] = _filters[index] with { Condition = Blank(condition) };
        }

        Changed?.Invoke(new BreakpointsChanged(new HashSet<string>(), false, true));
    }

    // ── Remembering ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The breakpoints as the session state keeps them.</summary>
    public DebugWorkspaceState Snapshot(IEnumerable<string>? watches = null, string? configuration = null)
    {
        lock (_gate)
        {
            return new DebugWorkspaceState
            {
                Breakpoints = [.. _source],
                FunctionBreakpoints = [.. _functions],
                ExceptionFilters = [.. _filters],
                Watches = [.. watches ?? []],
                Configuration = configuration,
            };
        }
    }

    /// <summary>Replaces everything with what was remembered.</summary>
    public void Restore(DebugWorkspaceState state)
    {
        HashSet<string> files;
        lock (_gate)
        {
            files = [.. _source.Select(b => b.Path), .. state.Breakpoints.Select(b => b.Path)];
            _source = [.. state.Breakpoints.Select(b => b with { Path = Normalize(b.Path) })];
            _functions = [.. state.FunctionBreakpoints];
            _filters = [.. state.ExceptionFilters];
            _nextId = Math.Max(1, _source.Select(b => b.Id).Concat(_functions.Select(f => f.Id)).DefaultIfEmpty(0).Max() + 1);
        }

        Changed?.Invoke(new BreakpointsChanged(files, true, true));
    }

    private void Raise(string path) => Changed?.Invoke(new BreakpointsChanged(new HashSet<string> { path }, false, false));

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
