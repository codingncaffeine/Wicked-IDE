using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wicked.Core.Tasks;

public enum ProblemSeverity
{
    Error,
    Warning,
    Info,
}

/// <summary>
/// One problem a matcher found in a task's output. Lines and columns are 1-based as compilers print them; a column the
/// output did not give is 1, and an end the output did not give is 0.
/// </summary>
public sealed record TaskProblem(string File, int Line, int Column, int EndLine, int EndColumn, ProblemSeverity Severity, string? Code, string Message, string Owner);

/// <summary>Where a file named in the output is looked for.</summary>
public enum FileLocationKind
{
    /// <summary>Under <see cref="ProblemMatcher.FilePrefix"/> (the workspace folder by default).</summary>
    Relative,

    Absolute,

    /// <summary>The prefix first, then as written, then the task's folder.</summary>
    AutoDetect,
}

/// <summary>
/// One line pattern of a matcher: a regular expression and the group number of each part it captures (0 for a part it
/// does not). A last pattern with <paramref name="Loop"/> keeps matching the lines that follow, reusing what the earlier
/// patterns captured (a file's name, then each of its problems).
/// </summary>
public sealed record MatcherPattern(Regex Regex, int File = 0, int Location = 0, int Line = 0, int Column = 0, int EndLine = 0, int EndColumn = 0, int Severity = 0, int Code = 0, int Message = 0, bool Loop = false);

/// <summary>The lines that open and close a watching task's compile cycles.</summary>
public sealed record BackgroundSpec(bool ActiveOnStart, Regex Begins, Regex Ends);

/// <summary>A problem matcher: how a task's output is read for problems (VS Code's own shape).</summary>
/// <param name="Name">The name tasks.json refers to it by ("$tsc"); empty for a matcher written in place.</param>
/// <param name="Owner">The tool the problems came from, shown beside them.</param>
/// <param name="DefaultSeverity">The severity of a problem whose line names none.</param>
/// <param name="FileLocation">Where the files named in the output are looked for.</param>
/// <param name="FilePrefix">The folder relative names are under; the workspace folder when null.</param>
/// <param name="Patterns">The line patterns; more than one reads a problem that spans lines.</param>
/// <param name="Background">The begin and end lines of a watching task; null for a task that ends.</param>
public sealed record ProblemMatcher(string Name, string Owner, ProblemSeverity DefaultSeverity, FileLocationKind FileLocation, string? FilePrefix, ImmutableArray<MatcherPattern> Patterns, BackgroundSpec? Background)
{
    public static Regex Rx(string pattern) => new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}

/// <summary>The matchers VS Code ships under "$" names, written from the formats their tools print.</summary>
public static class BuiltInMatchers
{
    private static readonly ImmutableDictionary<string, ProblemMatcher> ByName = Build();

    public static IEnumerable<ProblemMatcher> All => ByName.Values;

    public static ProblemMatcher? Find(string name) => ByName.GetValueOrDefault(name);

    private static ImmutableDictionary<string, ProblemMatcher> Build()
    {
        static MatcherPattern Tsc() => new(ProblemMatcher.Rx(@"^([^\s].*)[\(:](\d+)[,:](\d+)(?:\):\s+|\s+-\s+)(error|warning|info)\s+(TS\d+)\s*:\s*(.*)$"), File: 1, Line: 2, Column: 3, Severity: 4, Code: 5, Message: 6);
        var all = new List<ProblemMatcher>
        {
            new("$tsc", "typescript", ProblemSeverity.Error, FileLocationKind.Relative, null, [Tsc()], null),
            new("$tsc-watch", "typescript", ProblemSeverity.Error, FileLocationKind.Relative, null, [Tsc()],
                new BackgroundSpec(true,
                    ProblemMatcher.Rx(@"^\s*(?:message TS6032:|\[?\d{1,2}:\d{1,2}:\d{1,2}(?: AM| PM)?\]? -) File change detected\. Starting incremental compilation\.\.\."),
                    ProblemMatcher.Rx(@"^\s*(?:message TS6042:|\[?\d{1,2}:\d{1,2}:\d{1,2}(?: AM| PM)?\]? -) (?:Compilation complete\.|Found \d+ errors?\.) Watching for file changes\."))),
            new("$gcc", "gcc", ProblemSeverity.Error, FileLocationKind.Relative, null,
                [new(ProblemMatcher.Rx(@"^(.+?):(\d+):(\d+):\s+(warning|error|fatal error):\s+(.*)$"), File: 1, Line: 2, Column: 3, Severity: 4, Message: 5)], null),
            new("$msCompile", "msCompile", ProblemSeverity.Error, FileLocationKind.Absolute, null,
                [new(ProblemMatcher.Rx(@"^\s*(?:\s*\d+>)?(\S.*)\((\d+|\d+,\d+|\d+,\d+,\d+,\d+)\)\s*:\s+(error|warning|info)\s+(\w{1,2}\d+)\s*:\s*(.*?)(?:\s+\[[^\]]+\.(?:csproj|vbproj|fsproj|sln|slnx|proj)\])?$"), File: 1, Location: 2, Severity: 3, Code: 4, Message: 5)], null),
            new("$eslint-compact", "eslint-compact", ProblemSeverity.Error, FileLocationKind.Absolute, null,
                [new(ProblemMatcher.Rx(@"^(.+):\sline\s(\d+),\scol\s(\d+),\s(Error|Warning|Info)\s-\s(.+)\s\((.+)\)$"), File: 1, Line: 2, Column: 3, Severity: 4, Message: 5, Code: 6)], null),
            new("$eslint-stylish", "eslint-stylish", ProblemSeverity.Error, FileLocationKind.Absolute, null,
            [
                new(ProblemMatcher.Rx(@"^([^\s].*)$"), File: 1),
                new(ProblemMatcher.Rx(@"^\s+(\d+):(\d+)\s+(error|warning|info)\s+(.*?)(?:\s{2,}(\S+))?$"), Line: 1, Column: 2, Severity: 3, Message: 4, Code: 5, Loop: true),
            ], null),
            new("$go", "go", ProblemSeverity.Error, FileLocationKind.Relative, null,
                [new(ProblemMatcher.Rx(@"^\s*(?:vet:\s+)?(\S.*?\.go):(\d+):(?:(\d+):)?\s+(?:vet:.*?:\s+)?(.*)$"), File: 1, Line: 2, Column: 3, Message: 4)], null),
            new("$rustc", "rustc", ProblemSeverity.Error, FileLocationKind.Relative, null,
            [
                new(ProblemMatcher.Rx(@"^(warning|warn|error)(?:\[(.*?)\])?: (.*)$"), Severity: 1, Code: 2, Message: 3),
                new(ProblemMatcher.Rx(@"^\s*-->\s+(.*?):(\d+):(\d+)\s*$"), File: 1, Line: 2, Column: 3),
            ], null),
            new("$jshint", "jshint", ProblemSeverity.Warning, FileLocationKind.Absolute, null,
                [new(ProblemMatcher.Rx(@"^(.+):\sline\s(\d+),\scol\s(\d+),\s(.+?)(?:\s+\((\w)(\d+)\))?$"), File: 1, Line: 2, Column: 3, Message: 4, Severity: 5, Code: 6)], null),
        };
        all.Add(all.First(m => m.Name == "$rustc") with { Name = "$rustc-watch" });
        return all.ToImmutableDictionary(m => m.Name, StringComparer.Ordinal);
    }
}

/// <summary>What one line of output did to a task's problems.</summary>
public readonly record struct MatcherEvent(TaskProblem? Problem, bool Began, bool Ended);

/// <summary>
/// Reads a task's output line by line through its problem matchers. Multi-line patterns keep what earlier lines captured;
/// a watching task's begin and end lines are told apart from problems; the same problem printed twice (a build that
/// repeats its errors in a summary) is kept once. One collector per run, fed from one thread.
/// </summary>
public sealed class ProblemCollector
{
    private readonly List<MatcherState> _states;
    private readonly string _workspace;
    private readonly string _cwd;
    private readonly List<TaskProblem> _problems = [];
    private readonly HashSet<(string, int, int, string?, string)> _seen = [];
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);

    public ProblemCollector(IEnumerable<ProblemMatcher> matchers, string workspaceFolder, string cwd)
    {
        _states = [.. matchers.Select(m => new MatcherState(m))];
        _workspace = workspaceFolder;
        _cwd = cwd;
    }

    /// <summary>Whether any matcher has begin and end lines (the task is a watcher).</summary>
    public bool HasBackground => _states.Any(s => s.Matcher.Background is not null);

    /// <summary>Whether a matcher starts the task in its active state.</summary>
    public bool ActiveOnStart => _states.Any(s => s.Matcher.Background is { ActiveOnStart: true });

    public IReadOnlyList<TaskProblem> Problems => _problems;

    public void Clear()
    {
        _problems.Clear();
        _seen.Clear();
        foreach (var s in _states) s.Reset();
    }

    /// <summary>Reads a line (colour codes already removed); a problem found, or a watching cycle beginning or ending.</summary>
    public MatcherEvent Feed(string line)
    {
        TaskProblem? found = null;
        var began = false;
        var ended = false;
        foreach (var state in _states)
        {
            var bg = state.Matcher.Background;
            if (bg is not null)
            {
                if (IsMatch(bg.Begins, line))
                {
                    began = true;
                    state.Reset();
                    continue;
                }

                if (IsMatch(bg.Ends, line))
                {
                    ended = true;
                    state.Reset();
                    continue;
                }
            }
        }

        if (began)
        {
            _problems.Clear();
            _seen.Clear();
        }

        if (!began && !ended)
        {
            foreach (var state in _states)
            {
                if (state.Feed(line, this) is { } problem && found is null) found = problem;
            }
        }

        return new MatcherEvent(found, began, ended);
    }

    private static bool IsMatch(Regex rx, string line)
    {
        try
        {
            return rx.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private TaskProblem? Add(TaskProblem problem)
    {
        if (!_seen.Add((problem.File, problem.Line, problem.Column, problem.Code, problem.Message))) return null;
        _problems.Add(problem);
        return problem;
    }

    private string Resolve(ProblemMatcher matcher, string file)
    {
        var key = matcher.FileLocation + "|" + file;
        if (_resolved.TryGetValue(key, out var known)) return known;
        var path = file.Trim();
        string result;
        try
        {
            if (Path.IsPathRooted(path)) result = path;
            else
            {
                // A relative name is looked for under the prefix first; a build run in a subfolder printed it
                // relative to that folder instead, so the task's own folder is the second place.
                var viaPrefix = Path.GetFullPath(Path.Combine(matcher.FilePrefix ?? _workspace, path));
                var viaCwd = Path.GetFullPath(Path.Combine(_cwd, path));
                result = matcher.FileLocation == FileLocationKind.Absolute || File.Exists(viaPrefix) || !File.Exists(viaCwd) ? viaPrefix : viaCwd;
            }
        }
        catch (ArgumentException)
        {
            result = path;
        }

        _resolved[key] = result;
        return result;
    }

    private sealed class MatcherState(ProblemMatcher matcher)
    {
        private int _next;
        private readonly Captured _data = new();

        public ProblemMatcher Matcher { get; } = matcher;

        public void Reset()
        {
            _next = 0;
            _data.Clear();
        }

        public TaskProblem? Feed(string line, ProblemCollector owner)
        {
            var patterns = Matcher.Patterns;
            if (patterns.IsDefaultOrEmpty) return null;
            if (_next > 0)
            {
                var m = TryMatch(patterns[_next], line);
                if (m is not null)
                {
                    _data.Take(patterns[_next], m);
                    return Advance(patterns, owner);
                }

                // A line that does not continue the pattern starts over: the same line may begin a new match.
                Reset();
            }

            var first = TryMatch(patterns[0], line);
            if (first is null) return null;
            _data.Take(patterns[0], first);
            return Advance(patterns, owner);
        }

        private TaskProblem? Advance(ImmutableArray<MatcherPattern> patterns, ProblemCollector owner)
        {
            var matched = _next;
            if (matched < patterns.Length - 1)
            {
                _next = matched + 1;
                return null;
            }

            var problem = _data.ToProblem(Matcher, owner);
            if (patterns[matched].Loop)
            {
                _data.ClearLoopPart(patterns);
                _next = matched;
            }
            else
            {
                Reset();
            }

            return problem is null ? null : owner.Add(problem);
        }

        private static Match? TryMatch(MatcherPattern pattern, string line)
        {
            try
            {
                var m = pattern.Regex.Match(line);
                return m.Success ? m : null;
            }
            catch (RegexMatchTimeoutException)
            {
                return null;
            }
        }
    }

    /// <summary>The parts captured so far across the lines of one multi-line match.</summary>
    private sealed class Captured
    {
        private string? _file;
        private int _line;
        private int _column;
        private int _endLine;
        private int _endColumn;
        private string? _severity;
        private string? _code;
        private string? _message;

        public void Clear()
        {
            _file = null;
            ClearFields();
        }

        /// <summary>After a looping pattern yields a problem, what the loop line captured goes; the file stays.</summary>
        public void ClearLoopPart(ImmutableArray<MatcherPattern> patterns)
        {
            // Fields captured by earlier patterns (not the looping one) are kept for the next repetition.
            var last = patterns[^1];
            if (last.File != 0) _file = null;
            if (last.Line != 0 || last.Location != 0) _line = 0;
            if (last.Column != 0 || last.Location != 0) _column = 0;
            if (last.EndLine != 0 || last.Location != 0) _endLine = 0;
            if (last.EndColumn != 0 || last.Location != 0) _endColumn = 0;
            if (last.Severity != 0) _severity = null;
            if (last.Code != 0) _code = null;
            if (last.Message != 0) _message = null;
        }

        private void ClearFields()
        {
            _line = _column = _endLine = _endColumn = 0;
            _severity = _code = _message = null;
        }

        public void Take(MatcherPattern p, Match m)
        {
            string? Group(int n) => n > 0 && n < m.Groups.Count && m.Groups[n].Success ? m.Groups[n].Value : null;
            if (Group(p.File) is { } file) _file = file;
            if (Group(p.Location) is { } location) TakeLocation(location);
            if (Number(Group(p.Line)) is { } line) _line = line;
            if (Number(Group(p.Column)) is { } column) _column = column;
            if (Number(Group(p.EndLine)) is { } endLine) _endLine = endLine;
            if (Number(Group(p.EndColumn)) is { } endColumn) _endColumn = endColumn;
            if (Group(p.Severity) is { } severity) _severity = severity;
            if (Group(p.Code) is { } code) _code = code;
            if (Group(p.Message) is { } message) _message = message;
        }

        private void TakeLocation(string location)
        {
            var parts = location.Split([',', '.', ':', '-'], StringSplitOptions.RemoveEmptyEntries).Select(Number).ToArray();
            if (parts.Length == 0 || parts[0] is null) return;
            _line = parts[0]!.Value;
            if (parts.Length == 2) _column = parts[1] ?? 0;
            else if (parts.Length == 3)
            {
                _column = parts[1] ?? 0;
                _endColumn = parts[2] ?? 0;
            }
            else if (parts.Length >= 4)
            {
                _column = parts[1] ?? 0;
                _endLine = parts[2] ?? 0;
                _endColumn = parts[3] ?? 0;
            }
        }

        public TaskProblem? ToProblem(ProblemMatcher matcher, ProblemCollector owner)
        {
            if (_file is null || _line <= 0 || _message is null) return null;
            var severity = _severity is null ? matcher.DefaultSeverity : SeverityOf(_severity, matcher.DefaultSeverity);
            return new TaskProblem(owner.Resolve(matcher, _file), _line, Math.Max(1, _column), _endLine, _endColumn, severity, _code, _message.Trim(), matcher.Owner);
        }

        private static int? Number(string? text) =>
            int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

        private static ProblemSeverity SeverityOf(string text, ProblemSeverity fallback)
        {
            var s = text.Trim().ToLowerInvariant();
            if (s.StartsWith("err", StringComparison.Ordinal) || s.StartsWith("fatal", StringComparison.Ordinal) || s == "e") return ProblemSeverity.Error;
            if (s.StartsWith("warn", StringComparison.Ordinal) || s == "w") return ProblemSeverity.Warning;
            if (s.StartsWith("info", StringComparison.Ordinal) || s.StartsWith("note", StringComparison.Ordinal) || s.StartsWith("hint", StringComparison.Ordinal) || s == "i") return ProblemSeverity.Info;
            return fallback;
        }
    }
}
