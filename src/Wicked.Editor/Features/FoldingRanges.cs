using System.Text.RegularExpressions;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>
/// One foldable range in document lines (0-based). Collapsing it hides lines <see cref="StartLine"/> + 1 to
/// <see cref="EndLine"/>; the start line stays visible as the header. An indentation range ends on the last line more
/// indented than its header (so a closing brace stays visible, as in VS Code); a region range ends on its end marker.
/// </summary>
public readonly record struct FoldRange(int StartLine, int EndLine)
{
    public int HiddenCount => EndLine - StartLine;
}

/// <summary>A language's region markers (<c>#region</c> ... <c>#endregion</c> and its comment forms) as two anchored regular expressions.</summary>
public sealed class FoldingMarkers
{
    private static readonly Dictionary<string, FoldingMarkers?> Cache = [];

    private FoldingMarkers(Regex start, Regex end)
    {
        Start = start;
        End = end;
    }

    public Regex Start { get; }

    public Regex End { get; }

    /// <summary>For languages whose configuration names no markers: <c>#region</c>, <c>// region</c>, <c>// #region</c>, <c># region</c>, <c>-- region</c> and their end forms.</summary>
    public static readonly FoldingMarkers Default = Create(@"^\s*(?://|#|--|;|%|/\*|<!--)?\s*#?region\b", @"^\s*(?://|#|--|;|%|/\*|<!--)?\s*#?endregion\b")!;

    public static FoldingMarkers? Create(string start, string end)
    {
        try
        {
            const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
            return new FoldingMarkers(new Regex(start, Options, TimeSpan.FromSeconds(1)), new Regex(end, Options, TimeSpan.FromSeconds(1)));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The markers the language's TextMate configuration gives (<c>folding.markers</c>), else <see cref="Default"/> (also
    /// when there is no language). Cached per language id.
    /// </summary>
    public static FoldingMarkers For(LanguageInfo? language)
    {
        if (language is null) return Default;
        lock (Cache)
        {
            if (Cache.TryGetValue(language.Id, out var cached)) return cached ?? Default;
            FoldingMarkers? made = null;
            var configured = Languages.Registry.GetAvailableLanguages().FirstOrDefault(l => l.Id == language.Id)?.Configuration?.Folding?.Markers;
            if (configured is { Start: { Length: > 0 } s, End: { Length: > 0 } e }) made = Create(s, e);
            Cache[language.Id] = made;
            return made ?? Default;
        }
    }
}

/// <summary>
/// The foldable ranges of one document version, sorted by start line and properly nested. Computed on a worker in one
/// pass over the indentation (VS Code's indentation strategy: a line whose next non-blank line is more indented starts a
/// range that ends at the last non-blank line before the next line at or below its indentation) merged with region
/// markers. Ranges that would cross another range, or share its start line, are dropped. The view looks ranges up by
/// line with binary searches over parallel arrays.
/// </summary>
public sealed class FoldingRanges
{
    private readonly int[] _starts;
    private readonly int[] _ends;
    private readonly int[] _parents;
    private readonly int[] _depths;

    private FoldingRanges(long version, int[] starts, int[] ends, int[] parents, int[] depths)
    {
        Version = version;
        _starts = starts;
        _ends = ends;
        _parents = parents;
        _depths = depths;
    }

    public static readonly FoldingRanges Empty = new(-1, [], [], [], []);

    public long Version { get; }

    public int Count => _starts.Length;

    public int StartLine(int index) => _starts[index];

    public int EndLine(int index) => _ends[index];

    public FoldRange this[int index] => new(_starts[index], _ends[index]);

    /// <summary>The index of the enclosing range, or -1.</summary>
    public int Parent(int index) => _parents[index];

    /// <summary>Nesting depth, 0 for outermost.</summary>
    public int Depth(int index) => _depths[index];

    /// <summary>The range that starts on <paramref name="line"/>, or -1. O(log n), no allocation.</summary>
    public int IndexStartingAt(int line)
    {
        var i = Array.BinarySearch(_starts, line);
        return i >= 0 ? i : -1;
    }

    /// <summary>The first range starting at or after <paramref name="line"/> (a gutter walks forward from here), or <see cref="Count"/>.</summary>
    public int FirstStartingAtOrAfter(int line)
    {
        var i = Array.BinarySearch(_starts, line);
        return i >= 0 ? i : ~i;
    }

    /// <summary>The innermost range whose lines (header included) contain <paramref name="line"/>, or -1.</summary>
    public int InnermostContaining(int line)
    {
        var i = FirstStartingAtOrAfter(line + 1) - 1;
        while (i >= 0 && _ends[i] < line) i = _parents[i];
        return i;
    }

    public static FoldingRanges Compute(Rope text, long version, int tabSize, FoldingMarkers? markers = null)
    {
        var table = LineTable.From(text);
        tabSize = Math.Max(1, tabSize);
        var indents = Indentation.MeasureAll(table, tabSize);
        var found = new List<(int Start, int End)>();

        // Indentation ranges: a stack of open headers; a line at or below a header's indent closes it.
        var stack = new List<(int Indent, int Line)>();
        var lastNonBlank = -1;
        for (var line = 0; line < indents.Length; line++)
        {
            var indent = indents[line];
            if (indent < 0) continue;
            while (stack.Count > 0 && stack[^1].Indent >= indent)
            {
                var top = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                if (lastNonBlank > top.Line) found.Add((top.Line, lastNonBlank));
            }

            stack.Add((indent, line));
            lastNonBlank = line;
        }

        foreach (var top in stack)
        {
            if (lastNonBlank > top.Line) found.Add((top.Line, lastNonBlank));
        }

        // Region markers.
        if (markers is not null)
        {
            var open = new List<int>();
            for (var line = 0; line < table.Count; line++)
            {
                if (indents[line] < 0) continue;
                var span = table.Span(line);
                if (markers.Start.IsMatch(span))
                {
                    open.Add(line);
                }
                else if (open.Count > 0 && markers.End.IsMatch(span))
                {
                    found.Add((open[^1], line));
                    open.RemoveAt(open.Count - 1);
                }
            }
        }

        return Build(version, found);
    }

    internal static FoldingRanges Build(long version, List<(int Start, int End)> found)
    {
        found.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
        var starts = new List<int>(found.Count);
        var ends = new List<int>(found.Count);
        var parents = new List<int>(found.Count);
        var depths = new List<int>(found.Count);
        var open = new List<int>(); // indices of ranges still enclosing the current start
        foreach (var (start, end) in found)
        {
            if (end <= start) continue;
            while (open.Count > 0 && ends[open[^1]] < start) open.RemoveAt(open.Count - 1);
            if (starts.Count > 0 && starts[^1] == start) continue;
            if (open.Count > 0 && end > ends[open[^1]]) continue;
            starts.Add(start);
            ends.Add(end);
            parents.Add(open.Count > 0 ? open[^1] : -1);
            depths.Add(open.Count);
            open.Add(starts.Count - 1);
        }

        return new FoldingRanges(version, [.. starts], [.. ends], [.. parents], [.. depths]);
    }
}
