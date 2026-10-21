using System.Collections.Immutable;
using System.Text;

namespace Wicked.Core.Git;

/// <summary>How a line of the working text differs from the text it is compared with.</summary>
public enum LineChangeKind
{
    /// <summary>The line is new.</summary>
    Added,

    /// <summary>The line replaces one or more others.</summary>
    Modified,

    /// <summary>Lines were removed just above this line.</summary>
    DeletedAbove,

    /// <summary>Lines were removed after the last line (there is no line below to carry the mark).</summary>
    DeletedBelow,
}

/// <summary>A gutter mark: a 0-based line of the working text and how it changed.</summary>
public readonly record struct LineChange(int Line, LineChangeKind Kind);

/// <summary>Helpers for text as lines, the way the editor and git both see it.</summary>
public static class TextLines
{
    /// <summary>Splits on '\n' (a final newline leaves a final empty line, as the editor counts lines) and drops each '\r', so CRLF and LF compare equal.</summary>
    public static string[] Split(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];
        }

        return lines;
    }
}

/// <summary>The line-change marks of the gutter, from the blocks of a diff.</summary>
public static class GutterMarks
{
    /// <summary>The marks for the working text against the text it is compared with (the index, or HEAD), from a worker.</summary>
    public static ImmutableArray<LineChange> Compute(IReadOnlyList<string> original, IReadOnlyList<string> working, TimeSpan? cap = null, CancellationToken ct = default) =>
        FromBlocks(DiffEngine.Diff(original, working, cap, ct).Blocks, working.Count);

    public static ImmutableArray<LineChange> FromBlocks(ImmutableArray<LineBlock> blocks, int workingLineCount)
    {
        var marks = ImmutableArray.CreateBuilder<LineChange>();
        foreach (var block in blocks)
        {
            if (block.IsDeletion)
            {
                if (block.ModStart >= workingLineCount) marks.Add(new LineChange(Math.Max(0, workingLineCount - 1), LineChangeKind.DeletedBelow));
                else marks.Add(new LineChange(block.ModStart, LineChangeKind.DeletedAbove));
                continue;
            }

            for (var i = 0; i < block.ModCount; i++)
            {
                marks.Add(new LineChange(block.ModStart + i, block.IsInsertion || i >= block.OrigCount ? LineChangeKind.Added : LineChangeKind.Modified));
            }
        }

        return marks.ToImmutable();
    }

    /// <summary>The hunk at a line (for the peek widget's stage, revert and next/previous): the block that covers it or marks it.</summary>
    public static LineBlock? BlockAt(ImmutableArray<LineBlock> blocks, int line)
    {
        foreach (var b in blocks)
        {
            if (line >= b.ModStart && line < Math.Max(b.ModEnd, b.ModStart + 1)) return b;
        }

        return null;
    }
}

/// <summary>A run of characters inside a line.</summary>
public readonly record struct CharRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>The characters that differ between two paired lines (0-based line numbers), as ranges on each side.</summary>
public sealed record InlineChange(int LeftLine, int RightLine, ImmutableArray<CharRange> Left, ImmutableArray<CharRange> Right);

/// <summary>What a row of the side-by-side view shows.</summary>
public enum AlignedRowKind
{
    Unchanged,

    /// <summary>Both sides have a line and they differ.</summary>
    Modified,

    /// <summary>Only the left (original) side has a line; the right shows a filler.</summary>
    Removed,

    /// <summary>Only the right (modified) side has a line; the left shows a filler.</summary>
    Added,
}

/// <summary>One row: the line number on each side (0-based) or null where that side shows a filler.</summary>
public readonly record struct AlignedRow(int? Left, int? Right, AlignedRowKind Kind);

/// <summary>
/// Two texts laid side by side: <see cref="Rows"/> pairs their lines with filler rows where one side has none, the line maps
/// go from a line of either text to its row (and back through the row), and <see cref="Inline"/> holds the word-level changes
/// inside the paired lines. Built on a worker; immutable.
/// </summary>
public sealed class SideBySideDiff
{
    private SideBySideDiff(ImmutableArray<AlignedRow> rows, ImmutableArray<int> leftRowOfLine, ImmutableArray<int> rightRowOfLine, ImmutableArray<InlineChange> inline, ImmutableArray<LineBlock> blocks, bool timedOut)
    {
        Rows = rows;
        LeftRowOfLine = leftRowOfLine;
        RightRowOfLine = rightRowOfLine;
        Inline = inline;
        Blocks = blocks;
        TimedOut = timedOut;
    }

    public ImmutableArray<AlignedRow> Rows { get; }

    /// <summary>For each line of the original text, the row it is on.</summary>
    public ImmutableArray<int> LeftRowOfLine { get; }

    /// <summary>For each line of the modified text, the row it is on.</summary>
    public ImmutableArray<int> RightRowOfLine { get; }

    public ImmutableArray<InlineChange> Inline { get; }

    /// <summary>The line blocks the rows came from, for "next change" and for the hunk actions.</summary>
    public ImmutableArray<LineBlock> Blocks { get; }

    public bool TimedOut { get; }

    /// <summary>Rows where a change begins, for F7 / Alt+F5 navigation.</summary>
    public IEnumerable<int> ChangeRows => Rows.Select((r, i) => (r, i)).Where(t => t.r.Kind != AlignedRowKind.Unchanged && (t.i == 0 || Rows[t.i - 1].Kind == AlignedRowKind.Unchanged)).Select(t => t.i);

    public static SideBySideDiff Build(IReadOnlyList<string> original, IReadOnlyList<string> modified, TimeSpan? cap = null, CancellationToken ct = default)
    {
        var diff = DiffEngine.Diff(original, modified, cap, ct);
        var rows = ImmutableArray.CreateBuilder<AlignedRow>();
        var inline = ImmutableArray.CreateBuilder<InlineChange>();
        var left = new int[original.Count];
        var right = new int[modified.Count];
        int li = 0, ri = 0;

        void Same(int count)
        {
            for (var n = 0; n < count; n++)
            {
                left[li] = right[ri] = rows.Count;
                rows.Add(new AlignedRow(li++, ri++, AlignedRowKind.Unchanged));
            }
        }

        foreach (var block in diff.Blocks)
        {
            Same(block.OrigStart - li);
            foreach (var (l, r) in Pair(original, block, modified))
            {
                if (l is { } lv && r is { } rv)
                {
                    left[lv] = right[rv] = rows.Count;
                    rows.Add(new AlignedRow(lv, rv, AlignedRowKind.Modified));
                    var change = InlineDiff.Between(original[lv], modified[rv], lv, rv);
                    if (change is not null) inline.Add(change);
                }
                else if (l is { } onlyLeft)
                {
                    left[onlyLeft] = rows.Count;
                    rows.Add(new AlignedRow(onlyLeft, null, AlignedRowKind.Removed));
                }
                else if (r is { } onlyRight)
                {
                    right[onlyRight] = rows.Count;
                    rows.Add(new AlignedRow(null, onlyRight, AlignedRowKind.Added));
                }
            }

            li = block.OrigEnd;
            ri = block.ModEnd;
        }

        Same(original.Count - li);
        return new SideBySideDiff(rows.ToImmutable(), [.. left], [.. right], inline.ToImmutable(), diff.Blocks, diff.TimedOut);
    }

    /// <summary>
    /// Pairs the lines of a block that look alike (so a line edited in place sits across from its old self) and lays the
    /// rest out removed-first. Pairing keeps the order of both sides.
    /// </summary>
    private static IEnumerable<(int? Left, int? Right)> Pair(IReadOnlyList<string> original, LineBlock block, IReadOnlyList<string> modified)
    {
        int n = block.OrigCount, m = block.ModCount;
        if (n == 0 || m == 0 || (long)n * m > 40_000)
        {
            // Too big to compare every pair: line up by position.
            var both = Math.Min(n, m);
            for (var k = 0; k < both; k++) yield return (block.OrigStart + k, block.ModStart + k);
            for (var k = both; k < n; k++) yield return (block.OrigStart + k, null);
            for (var k = both; k < m; k++) yield return (null, block.ModStart + k);
            yield break;
        }

        var leftGrams = Enumerable.Range(0, n).Select(i => Bigrams(original[block.OrigStart + i])).ToArray();
        var rightGrams = Enumerable.Range(0, m).Select(j => Bigrams(modified[block.ModStart + j])).ToArray();
        var score = new double[n + 1, m + 1];
        var take = new bool[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                var best = Math.Max(score[i + 1, j], score[i, j + 1]);
                var s = Similarity(leftGrams[i], rightGrams[j]);
                if (s >= 0.4 && score[i + 1, j + 1] + s > best)
                {
                    best = score[i + 1, j + 1] + s;
                    take[i, j] = true;
                }

                score[i, j] = best;
            }
        }

        int a = 0, b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && take[a, b])
            {
                yield return (block.OrigStart + a, block.ModStart + b);
                a++;
                b++;
            }
            else if (b == m || (a < n && Math.Abs(score[a + 1, b] - score[a, b]) < 1e-9))
            {
                yield return (block.OrigStart + a, null);
                a++;
            }
            else
            {
                yield return (null, block.ModStart + b);
                b++;
            }
        }
    }

    private static Dictionary<int, int> Bigrams(string line)
    {
        var d = new Dictionary<int, int>();
        var t = line.Trim();
        if (t.Length == 1) d[t[0]] = 1;
        for (var i = 0; i + 1 < t.Length; i++)
        {
            var key = (t[i] << 16) | t[i + 1];
            d[key] = d.GetValueOrDefault(key) + 1;
        }

        return d;
    }

    private static double Similarity(Dictionary<int, int> x, Dictionary<int, int> y)
    {
        if (x.Count == 0 && y.Count == 0) return 1;
        if (x.Count == 0 || y.Count == 0) return 0;
        var common = 0;
        foreach (var (k, v) in x)
        {
            if (y.TryGetValue(k, out var w)) common += Math.Min(v, w);
        }

        return 2.0 * common / (x.Values.Sum() + y.Values.Sum());
    }
}

/// <summary>Word-level changes inside a pair of lines.</summary>
public static class InlineDiff
{
    /// <summary>The changed ranges of two lines, or null when they are the same. Pieces are words, runs of spaces and single symbols, so a rename shows as the word and not as scattered letters.</summary>
    public static InlineChange? Between(string left, string right, int leftLine, int rightLine)
    {
        if (left == right) return null;
        var a = Tokens(left);
        var b = Tokens(right);
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int[] Number(List<(int Start, int Length)> tokens, string text)
        {
            var result = new int[tokens.Count];
            for (var i = 0; i < result.Length; i++)
            {
                var piece = text.Substring(tokens[i].Start, tokens[i].Length);
                if (!ids.TryGetValue(piece, out var id)) ids[piece] = id = ids.Count;
                result[i] = id;
            }

            return result;
        }

        var diff = DiffEngine.Diff(Number(a, left), Number(b, right), TimeSpan.FromMilliseconds(50));
        var lr = ImmutableArray.CreateBuilder<CharRange>();
        var rr = ImmutableArray.CreateBuilder<CharRange>();
        foreach (var block in diff.Blocks)
        {
            Add(lr, a, block.OrigStart, block.OrigCount);
            Add(rr, b, block.ModStart, block.ModCount);
        }

        return new InlineChange(leftLine, rightLine, Merge(lr, left), Merge(rr, right));
    }

    private static void Add(ImmutableArray<CharRange>.Builder into, List<(int Start, int Length)> tokens, int first, int count)
    {
        if (count == 0) return;
        var start = tokens[first].Start;
        var end = tokens[first + count - 1].Start + tokens[first + count - 1].Length;
        into.Add(new CharRange(start, end - start));
    }

    /// <summary>Joins ranges separated by nothing but spaces: one highlighted phrase reads better than three words with gaps.</summary>
    private static ImmutableArray<CharRange> Merge(ImmutableArray<CharRange>.Builder ranges, string text)
    {
        var merged = ImmutableArray.CreateBuilder<CharRange>();
        foreach (var r in ranges)
        {
            if (merged.Count > 0 && merged[^1].End <= r.Start && text.AsSpan(merged[^1].End, r.Start - merged[^1].End).Trim().IsEmpty)
            {
                merged[^1] = new CharRange(merged[^1].Start, r.End - merged[^1].Start);
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged.ToImmutable();
    }

    private static List<(int Start, int Length)> Tokens(string text)
    {
        var tokens = new List<(int, int)>();
        var i = 0;
        while (i < text.Length)
        {
            var start = i;
            if (char.IsLetterOrDigit(text[i]) || text[i] == '_')
            {
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
            }
            else if (char.IsWhiteSpace(text[i]))
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            }
            else
            {
                i++;
            }

            tokens.Add((start, i - start));
        }

        return tokens;
    }

    /// <summary>A line's text with its changed ranges marked, for tests and the unified view: "foo [bar] baz".</summary>
    public static string Mark(string text, ImmutableArray<CharRange> ranges)
    {
        var b = new StringBuilder();
        var at = 0;
        foreach (var r in ranges)
        {
            b.Append(text, at, r.Start - at).Append('[').Append(text, r.Start, r.Length).Append(']');
            at = r.End;
        }

        return b.Append(text, at, text.Length - at).ToString();
    }
}
