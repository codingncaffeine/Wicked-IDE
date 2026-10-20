using System.Collections.Immutable;
using Wicked.Editor.Model;

namespace Wicked.Editor.Features;

/// <summary>
/// Which ranges are collapsed, and the mapping between document lines and visual lines that follows from it. Immutable:
/// every change returns a new state, cheap enough for the UI thread (it works on the collapsed ranges only, not on all
/// the document's ranges). <see cref="VisualLine"/> and <see cref="DocumentLine"/> are binary searches over prefix sums
/// of the hidden lines, O(log k) for k collapsed ranges, with no allocation.
/// <para>
/// Collapsed ranges are kept as plain line pairs so they survive edits: <see cref="Map"/> moves them through a
/// <see cref="DocumentChange"/> (the header and the last hidden line are followed as text positions; a range whose
/// header is deleted, or that shrinks to nothing, is dropped), and <see cref="Rebind"/> reconciles them with a fresh
/// <see cref="FoldingRanges"/> once the worker has recomputed it.
/// </para>
/// </summary>
public sealed class FoldState
{
    // Merged top-level hidden intervals, sorted: lines [_hiddenFirst[i], _hiddenLast[i]] are hidden.
    private readonly int[] _hiddenFirst;
    private readonly int[] _hiddenLast;
    // Hidden lines before interval i, and the header's visual line (hiddenFirst - 1 - hiddenBefore).
    private readonly int[] _hiddenBefore;
    private readonly int[] _headerVisual;

    private FoldState(int lineCount, ImmutableArray<FoldRange> collapsed)
    {
        LineCount = lineCount;
        Collapsed = collapsed;
        var first = new List<int>();
        var last = new List<int>();
        foreach (var range in collapsed)
        {
            var from = range.StartLine + 1;
            var to = Math.Min(range.EndLine, lineCount - 1);
            if (to < from) continue;
            if (last.Count > 0 && from <= last[^1])
            {
                // Nested or overlapping: only the outer one shows.
                if (to > last[^1]) last[^1] = to;
                continue;
            }

            first.Add(from);
            last.Add(to);
        }

        _hiddenFirst = [.. first];
        _hiddenLast = [.. last];
        _hiddenBefore = new int[first.Count];
        _headerVisual = new int[first.Count];
        var hidden = 0;
        for (var i = 0; i < first.Count; i++)
        {
            _hiddenBefore[i] = hidden;
            _headerVisual[i] = first[i] - 1 - hidden;
            hidden += last[i] - first[i] + 1;
        }

        HiddenLines = hidden;
    }

    public static FoldState None(int lineCount) => new(lineCount, []);

    /// <summary>The document's line count this state describes.</summary>
    public int LineCount { get; }

    /// <summary>The collapsed ranges, sorted by start line (nested ones included).</summary>
    public ImmutableArray<FoldRange> Collapsed { get; }

    public int HiddenLines { get; }

    public int VisualLineCount => LineCount - HiddenLines;

    public bool HasCollapsed => Collapsed.Length > 0;

    // ── Mapping ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The visual line of a document line; a hidden line maps to the header of the range hiding it.</summary>
    public int VisualLine(int documentLine)
    {
        var i = LastIntervalAtOrBefore(documentLine);
        if (i < 0) return documentLine;
        if (documentLine <= _hiddenLast[i]) return _headerVisual[i];
        return documentLine - _hiddenBefore[i] - (_hiddenLast[i] - _hiddenFirst[i] + 1);
    }

    /// <summary>The document line shown on a visual line.</summary>
    public int DocumentLine(int visualLine)
    {
        // The intervals whose header is on an earlier visual line each push the line down by their size.
        var lo = 0;
        var hi = _headerVisual.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_headerVisual[mid] < visualLine) lo = mid + 1;
            else hi = mid;
        }

        if (lo == 0) return visualLine;
        var j = lo - 1;
        return visualLine + _hiddenBefore[j] + (_hiddenLast[j] - _hiddenFirst[j] + 1);
    }

    public bool IsHidden(int documentLine)
    {
        var i = LastIntervalAtOrBefore(documentLine);
        return i >= 0 && documentLine <= _hiddenLast[i];
    }

    /// <summary>The collapsed range whose header is <paramref name="documentLine"/>, if any (for the folded-text marker).</summary>
    public bool IsCollapsedHeader(int documentLine, out FoldRange range)
    {
        foreach (var r in Collapsed)
        {
            if (r.StartLine == documentLine)
            {
                range = r;
                return true;
            }

            if (r.StartLine > documentLine) break;
        }

        range = default;
        return false;
    }

    private int LastIntervalAtOrBefore(int line)
    {
        var lo = 0;
        var hi = _hiddenFirst.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_hiddenFirst[mid] <= line) lo = mid + 1;
            else hi = mid;
        }

        return lo - 1;
    }

    // ── Collapsing ───────────────────────────────────────────────────────────────────────────────

    public FoldState Collapse(FoldRange range)
    {
        if (range.EndLine <= range.StartLine || Collapsed.Contains(range)) return this;
        var list = Collapsed.Where(r => r.StartLine != range.StartLine).Append(range);
        return new FoldState(LineCount, Sorted(list));
    }

    /// <summary>Expands whatever is collapsed with its header on <paramref name="headerLine"/>.</summary>
    public FoldState Expand(int headerLine) => Without(r => r.StartLine == headerLine);

    /// <summary>Expands every collapsed range that hides <paramref name="documentLine"/> (to reveal a search hit or the caret).</summary>
    public FoldState Reveal(int documentLine) => Without(r => documentLine > r.StartLine && documentLine <= r.EndLine);

    public FoldState Toggle(FoldingRanges ranges, int headerLine)
    {
        if (Collapsed.Any(r => r.StartLine == headerLine)) return Expand(headerLine);
        var index = ranges.IndexStartingAt(headerLine);
        return index < 0 ? this : Collapse(ranges[index]);
    }

    /// <summary>"Fold" at a caret: collapses the innermost not-yet-collapsed range around the line, as VS Code's Fold command does.</summary>
    public FoldState FoldAt(FoldingRanges ranges, int line)
    {
        var i = ranges.InnermostContaining(line);
        while (i >= 0 && Collapsed.Contains(ranges[i])) i = ranges.Parent(i);
        return i < 0 ? this : Collapse(ranges[i]);
    }

    /// <summary>"Unfold" at a caret: expands the collapsed range that hides the line, or whose header it is.</summary>
    public FoldState UnfoldAt(int line) => Without(r => line >= r.StartLine && line <= r.EndLine);

    public FoldState CollapseAll(FoldingRanges ranges) => new(LineCount, Sorted(Enumerable.Range(0, ranges.Count).Select(i => ranges[i])));

    /// <summary>Collapses the ranges at nesting depth <paramref name="level"/> (1 is the outermost) and keeps nothing else.</summary>
    public FoldState CollapseLevel(FoldingRanges ranges, int level) =>
        new(LineCount, Sorted(Enumerable.Range(0, ranges.Count).Where(i => ranges.Depth(i) == level - 1).Select(i => ranges[i])));

    public FoldState ExpandAll() => new(LineCount, []);

    private FoldState Without(Func<FoldRange, bool> remove)
    {
        var kept = Collapsed.Where(r => !remove(r)).ToImmutableArray();
        return kept.Length == Collapsed.Length ? this : new FoldState(LineCount, kept);
    }

    private static ImmutableArray<FoldRange> Sorted(IEnumerable<FoldRange> ranges) =>
        [.. ranges.OrderBy(r => r.StartLine).ThenByDescending(r => r.EndLine)];

    // ── Keeping up with edits ────────────────────────────────────────────────────────────────────

    /// <summary>The state after <paramref name="change"/>: collapsed ranges follow the text; the line count is the new text's.</summary>
    public FoldState Map(DocumentChange change)
    {
        var after = change.After;
        if (Collapsed.IsEmpty) return new FoldState(after.LineCount, []);
        var mapped = new List<FoldRange>(Collapsed.Length);
        foreach (var range in Collapsed)
        {
            if (range.StartLine >= change.Before.LineCount || range.EndLine >= change.Before.LineCount) continue;
            var startOffset = change.Before.LineStart(range.StartLine);
            var endOffset = change.Before.LineStart(range.EndLine) + change.Before.LineLength(range.EndLine);

            // A header whose line start is swallowed by a deletion is gone.
            if (change.Changes.Any(c => c.RemovedLength > 0 && startOffset > c.Offset && startOffset < c.End)) continue;

            var newStart = after.LineOf(ChangeSet.Map(startOffset, change.Changes, stickAfter: true));
            var newEnd = after.LineOf(ChangeSet.Map(endOffset, change.Changes, stickAfter: false));
            if (newEnd > newStart) mapped.Add(new FoldRange(newStart, newEnd));
        }

        return new FoldState(after.LineCount, Sorted(mapped));
    }

    /// <summary>
    /// Reconciles with a fresh set of ranges: a collapsed range stays when a range now starts on its header line (taking
    /// that range's end, which may have moved with the edit); the others are dropped.
    /// </summary>
    public FoldState Rebind(FoldingRanges ranges)
    {
        var kept = new List<FoldRange>(Collapsed.Length);
        foreach (var range in Collapsed)
        {
            var index = ranges.IndexStartingAt(range.StartLine);
            if (index >= 0) kept.Add(ranges[index]);
        }

        return new FoldState(LineCount, Sorted(kept));
    }
}
