using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Editor.View;

/// <summary>
/// The rows a document takes on screen when lines wrap, for the UI thread: each line's wrap and row count, and where
/// each line's first row is, folded lines taking none. A whole document is wrapped on a worker (<see cref="WrapMap"/>)
/// and taken over with <see cref="From"/>; after that an edit patches it in place — the touched lines wrapped again, the
/// arrays spliced, the row starts re-summed from the first line that moved — so typing never waits for a worker and
/// never allocates once the arrays have room (VS Code keeps its wrapped lines the same way).
/// </summary>
public sealed class WrapLayout
{
    private int[] _counts;
    private LineWrap?[] _wraps;
    private bool[] _fromShape; // the wrap used the shaped line's widths, not the grid's
    private int[] _rowStart = [];
    private int _lineCount;
    private int _dirtyFrom;
    private FoldState? _foldsFor;

    private WrapLayout(WrapOptions options, long version, int lineCount)
    {
        Options = options;
        Version = version;
        _lineCount = lineCount;
        _counts = new int[lineCount + 16];
        _wraps = new LineWrap?[lineCount + 16];
        _fromShape = new bool[lineCount + 16];
    }

    /// <summary>What the lines were wrapped for (the width, the indent, the tab size).</summary>
    public WrapOptions Options { get; }

    /// <summary>The document version the wraps follow.</summary>
    public long Version { get; private set; }

    public int LineCount => _lineCount;

    /// <summary>Takes over a worker's wrap of a whole document.</summary>
    public static WrapLayout From(WrapMap map)
    {
        var layout = new WrapLayout(map.Options, map.Version, map.LineCount);
        for (var i = 0; i < map.LineCount; i++)
        {
            layout._counts[i] = map.VisualLinesOf(i);
            layout._wraps[i] = map.WrapOf(i);
        }

        return layout;
    }

    /// <summary>
    /// Wraps one line on the grid (or along <paramref name="caretX"/>, a shaped line's caret positions), skipping the
    /// work for a line too short to reach the width whatever its characters are.
    /// </summary>
    public static LineWrap? WrapLine(string text, WrapOptions options, float[]? caretX = null)
    {
        if (caretX is null && text.Length * options.CharWidth * Math.Max(1, options.TabSize) <= options.Width) return null;
        return WrapMap.Wrap(text, options, caretX);
    }

    /// <summary>Follows an edit: the lines it touched are wrapped again, the rest move with their lines.</summary>
    public void Apply(DocumentChange change)
    {
        Version = change.Version;
        if (change.Changes.IsDefaultOrEmpty) return;
        var before = change.Before;
        var after = change.After;
        var firstOld = before.LineOf(change.Changes[0].Offset);
        var lastOld = before.LineOf(change.Changes[^1].End);
        var delta = after.LineCount - before.LineCount;
        var lastNew = lastOld + delta;
        var count = after.LineCount;
        if (count > _counts.Length)
        {
            var capacity = Math.Max(count + 16, _counts.Length * 3 / 2);
            Array.Resize(ref _counts, capacity);
            Array.Resize(ref _wraps, capacity);
            Array.Resize(ref _fromShape, capacity);
        }

        var tail = _lineCount - (lastOld + 1);
        if (delta != 0 && tail > 0)
        {
            Array.Copy(_counts, lastOld + 1, _counts, lastNew + 1, tail);
            Array.Copy(_wraps, lastOld + 1, _wraps, lastNew + 1, tail);
            Array.Copy(_fromShape, lastOld + 1, _fromShape, lastNew + 1, tail);
        }

        if (delta < 0)
        {
            // The slots past the new end keep nothing alive.
            Array.Clear(_wraps, count, _lineCount - count);
        }

        _lineCount = count;
        for (var line = firstOld; line <= lastNew; line++)
        {
            var wrap = WrapLine(after.Line(line), Options);
            _wraps[line] = wrap;
            _counts[line] = wrap is null ? 1 : wrap.Breaks.Length + 1;
            _fromShape[line] = false;
        }

        _dirtyFrom = Math.Min(_dirtyFrom, firstOld);
    }

    /// <summary>Whether a line's wrap already follows its shaped widths.</summary>
    public bool FollowsShape(int line) => _fromShape[line];

    /// <summary>
    /// Wraps a line again along its shaped widths (wide characters, ligatures and fallback fonts are not on the grid).
    /// True when its row count changed.
    /// </summary>
    public bool Reshape(int line, string text, float[] caretX)
    {
        var wrap = WrapMap.Wrap(text, Options, caretX);
        var rows = wrap is null ? 1 : wrap.Breaks.Length + 1;
        _wraps[line] = wrap;
        _fromShape[line] = true;
        if (rows == _counts[line]) return false;
        _counts[line] = rows;
        _dirtyFrom = Math.Min(_dirtyFrom, line);
        return true;
    }

    /// <summary>Brings the row starts up to date for the folds now in force. Cheap when nothing moved.</summary>
    public void Sync(FoldState? folds)
    {
        if (folds is not null && folds.LineCount != _lineCount) folds = null;
        if (!ReferenceEquals(folds, _foldsFor))
        {
            _foldsFor = folds;
            _dirtyFrom = 0;
        }

        if (_rowStart.Length < _lineCount + 1)
        {
            _rowStart = new int[Math.Max(_lineCount + 1, _counts.Length + 1)];
            _dirtyFrom = 0;
        }

        if (_dirtyFrom > _lineCount) return;

        // Re-sum from the first line that moved, hidden lines taking no rows.
        var from = Math.Min(_dirtyFrom, _lineCount);
        var span = 0;
        var spans = folds?.HiddenSpanCount ?? 0;
        while (span < spans && folds!.HiddenSpan(span).Last < from) span++;
        var row = _rowStart[from];
        if (from == 0) row = 0;
        for (var line = from; line < _lineCount; line++)
        {
            _rowStart[line] = row;
            while (span < spans && folds!.HiddenSpan(span).Last < line) span++;
            var hidden = span < spans && line >= folds!.HiddenSpan(span).First;
            if (!hidden) row += _counts[line];
        }

        _rowStart[_lineCount] = row;
        _dirtyFrom = int.MaxValue;
    }

    /// <summary>All the rows the visible lines take.</summary>
    public int RowCount => _rowStart[_lineCount];

    /// <summary>A line's first row.</summary>
    public int FirstRow(int line) => _rowStart[Math.Clamp(line, 0, _lineCount)];

    /// <summary>How many rows a line takes when shown.</summary>
    public int RowsOf(int line) => _counts[line];

    /// <summary>The line shown on a row, and which of its rows it is. O(log n).</summary>
    public (int Line, int Sub) LineAtRow(int row)
    {
        row = Math.Clamp(row, 0, Math.Max(0, RowCount - 1));
        // The last line whose first row is at or before the row: hidden lines share the next shown line's start, so
        // the search lands past them.
        int lo = 0, hi = _lineCount - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_rowStart[mid] <= row) lo = mid;
            else hi = mid - 1;
        }

        return (lo, Math.Min(row - _rowStart[lo], _counts[lo] - 1));
    }

    /// <summary>Which of a line's rows holds a column (a column at a break starts the next row).</summary>
    public int SubOf(int line, int column)
    {
        if (_wraps[line] is not { } wrap) return 0;
        var i = Array.BinarySearch(wrap.Breaks, column);
        return i >= 0 ? i + 1 : ~i;
    }

    /// <summary>The columns a row of a line covers: [start, end).</summary>
    public (int Start, int End) Segment(int line, int sub, int lineLength)
    {
        if (_wraps[line] is not { } wrap) return (0, lineLength);
        var start = sub == 0 ? 0 : wrap.Breaks[sub - 1];
        var end = sub < wrap.Breaks.Length ? wrap.Breaks[sub] : lineLength;
        return (start, end);
    }

    /// <summary>How far a row is shifted right (continuations line up under the line's indentation).</summary>
    public float IndentOf(int line, int sub) => sub == 0 || _wraps[line] is not { } wrap ? 0 : wrap.ContinuationOffset;
}
