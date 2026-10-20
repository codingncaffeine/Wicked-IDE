using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>How wrapped continuation lines are indented (VS Code's <c>editor.wrappingIndent</c>).</summary>
public enum WrapIndent
{
    /// <summary>Continuations start at the left edge.</summary>
    None,

    /// <summary>Continuations line up with the first non-blank character of the line (VS Code's default).</summary>
    Same,

    /// <summary>Like <see cref="Same"/>, plus one indentation unit (VS Code's "indent").</summary>
    Indent,

    /// <summary>Like <see cref="Same"/>, plus two indentation units (VS Code's "deepIndent").</summary>
    DeepIndent,
}

/// <summary>
/// What a wrap is computed for. Distances are in one unit of the caller's choosing: pixels when the widths come from
/// shaped lines, columns for "wrap at column N" (set <see cref="CharWidth"/> to 1 and <see cref="Width"/> to N). With no
/// shaped line available a line falls back to monospace: <see cref="CharWidth"/> per character, tabs to the next tab stop.
/// </summary>
public sealed record WrapOptions
{
    /// <summary>The distance at which a visual line must break.</summary>
    public required float Width { get; init; }

    /// <summary>The advance of one character in the monospace fallback.</summary>
    public float CharWidth { get; init; } = 1;

    public int TabSize { get; init; } = 4;

    public WrapIndent Indent { get; init; } = WrapIndent.Same;

    /// <summary>VS Code's <c>wordWrapBreakAfterCharacters</c>: a line may break after any of these.</summary>
    public string BreakAfter { get; init; } = " \t})]?|/&.,;";

    /// <summary>VS Code's <c>wordWrapBreakBeforeCharacters</c>: a line may break before any of these.</summary>
    public string BreakBefore { get; init; } = "([{";

    /// <summary>Wrap at column <paramref name="columns"/> (monospace, unit = one column).</summary>
    public static WrapOptions AtColumn(int columns, int tabSize = 4, WrapIndent indent = WrapIndent.Same) =>
        new() { Width = columns, CharWidth = 1, TabSize = tabSize, Indent = indent };
}

/// <summary>One document line's wrap: the columns where continuation lines start, and how far they are indented.</summary>
public sealed class LineWrap(int[] breaks, float continuationOffset)
{
    /// <summary>Ascending columns (character indexes in the line) where visual lines 1, 2, ... begin.</summary>
    public int[] Breaks { get; } = breaks;

    /// <summary>The distance continuation lines are shifted right by.</summary>
    public float ContinuationOffset { get; } = continuationOffset;
}

/// <summary>
/// The word-wrap layout of one document version: how many visual lines each document line occupies and where it breaks.
/// A worker computes it from the text (and shaped-line caret positions when the layout thread has them); the UI thread
/// then maps document line to first visual line, visual line to document line, and column to visual sub-line in
/// O(log n) with no allocation, through a Fenwick (prefix-sum) tree over the per-line visual counts. Most lines are one
/// visual line and cost nothing but a count of 1.
/// <para>
/// An edit is applied with <see cref="Update"/>: the touched lines are re-wrapped and the counts re-summed. The re-sum is
/// O(n) over a plain int array (about a tenth of a millisecond per 100,000 lines), so it runs on the worker with the
/// re-wrap rather than on the UI thread. Breaking follows VS Code's simple strategy: the last allowed break position
/// that fits, else a hard break at the width; never inside a surrogate pair.
/// </para>
/// </summary>
public sealed class WrapMap
{
    private readonly int[] _counts;
    private readonly int[] _tree; // Fenwick, 1-based over _counts
    private readonly LineWrap?[] _wraps;
    private readonly int _top;    // highest power of two <= line count

    private WrapMap(long version, WrapOptions options, int[] counts, LineWrap?[] wraps)
    {
        Version = version;
        Options = options;
        _counts = counts;
        _wraps = wraps;
        _tree = new int[counts.Length + 1];
        for (var i = 1; i <= counts.Length; i++)
        {
            _tree[i] += counts[i - 1];
            var parent = i + (i & -i);
            if (parent <= counts.Length) _tree[parent] += _tree[i];
        }

        _top = counts.Length == 0 ? 0 : 1 << (31 - int.LeadingZeroCount(counts.Length));
        VisualLineCount = Prefix(counts.Length);
    }

    public long Version { get; }

    public WrapOptions Options { get; }

    public int LineCount => _counts.Length;

    public int VisualLineCount { get; }

    // ── Lookups for the UI thread ────────────────────────────────────────────────────────────────

    /// <summary>How many visual lines the document line occupies.</summary>
    public int VisualLinesOf(int line) => _counts[line];

    /// <summary>The first visual line of the document line. O(log n).</summary>
    public int FirstVisualLine(int line) => Prefix(line);

    /// <summary>The document line shown on a visual line, and which of its sub-lines it is. O(log n).</summary>
    public (int Line, int SubLine) DocumentLine(int visualLine)
    {
        visualLine = Math.Clamp(visualLine, 0, Math.Max(0, VisualLineCount - 1));
        var position = 0;
        var remaining = visualLine;
        for (var step = _top; step > 0; step >>= 1)
        {
            var next = position + step;
            if (next <= _counts.Length && _tree[next] <= remaining)
            {
                position = next;
                remaining -= _tree[next];
            }
        }

        return position >= _counts.Length ? (_counts.Length - 1, _counts[^1] - 1) : (position, remaining);
    }

    /// <summary>The break columns of a line, or null for a line that does not wrap.</summary>
    public LineWrap? WrapOf(int line) => _wraps[line];

    /// <summary>The columns <paramref name="subLine"/> of <paramref name="line"/> covers: [start, end), the end being the line's length for the last.</summary>
    public (int Start, int End) Segment(int line, int subLine, int lineLength)
    {
        var wrap = _wraps[line];
        if (wrap is null) return (0, lineLength);
        var start = subLine == 0 ? 0 : wrap.Breaks[subLine - 1];
        var end = subLine < wrap.Breaks.Length ? wrap.Breaks[subLine] : lineLength;
        return (start, end);
    }

    /// <summary>The distance a sub-line is shifted right by (0 for the first).</summary>
    public float IndentOf(int line, int subLine) => subLine == 0 || _wraps[line] is not { } w ? 0 : w.ContinuationOffset;

    /// <summary>The sub-line holding a column (a caret at a break column sits at the start of the next sub-line).</summary>
    public int SubLineOf(int line, int column)
    {
        var wrap = _wraps[line];
        if (wrap is null) return 0;
        var i = Array.BinarySearch(wrap.Breaks, column);
        return i >= 0 ? i + 1 : ~i;
    }

    /// <summary>The visual line of a document position.</summary>
    public int VisualLineOf(int line, int column) => FirstVisualLine(line) + SubLineOf(line, column);

    private int Prefix(int count)
    {
        var sum = 0;
        for (var i = count; i > 0; i -= i & -i) sum += _tree[i];
        return sum;
    }

    // ── Building (worker) ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wraps a whole document. <paramref name="caretX"/> gives a line's shaped caret positions (length + 1 values, the
    /// x of each character boundary) or null when that line is not shaped yet, in which case the monospace fallback
    /// is used for it.
    /// </summary>
    public static WrapMap Compute(Rope text, long version, WrapOptions options, Func<int, float[]?>? caretX = null)
    {
        var table = LineTable.From(text);
        var counts = new int[table.Count];
        var wraps = new LineWrap?[table.Count];
        for (var i = 0; i < counts.Length; i++) Fill(table, i, options, caretX, counts, wraps);
        return new WrapMap(version, options, counts, wraps);
    }

    /// <summary>
    /// The map after <paramref name="change"/>: the lines the change touched are wrapped again (with <paramref name="caretX"/>
    /// indexing the NEW text's lines), the rest keep their wraps and move with their lines.
    /// </summary>
    public WrapMap Update(DocumentChange change, Func<int, float[]?>? caretX = null)
    {
        var before = change.Before;
        var after = change.After;
        if (change.Changes.IsDefaultOrEmpty) return new WrapMap(change.Version, Options, _counts, _wraps);

        var firstOld = before.LineOf(change.Changes[0].Offset);
        var lastOld = before.LineOf(change.Changes[^1].End);
        var firstNew = firstOld;
        var lastNew = lastOld + (after.LineCount - before.LineCount);
        // Several changes can shift lines between them: the touched span covers all of them.
        var counts = new int[after.LineCount];
        var wraps = new LineWrap?[after.LineCount];
        Array.Copy(_counts, 0, counts, 0, firstOld);
        Array.Copy(_wraps, 0, wraps, 0, firstOld);
        var tailOld = lastOld + 1;
        var tailNew = lastNew + 1;
        Array.Copy(_counts, tailOld, counts, tailNew, _counts.Length - tailOld);
        Array.Copy(_wraps, tailOld, wraps, tailNew, _wraps.Length - tailOld);

        for (var line = firstNew; line <= lastNew; line++)
        {
            var start = after.LineStart(line);
            var length = after.LineLength(line);
            var span = after.Substring(start, length);
            FillOne(span, line, Options, caretX, counts, wraps);
        }

        return new WrapMap(change.Version, Options, counts, wraps);
    }

    private static void Fill(LineTable table, int line, WrapOptions options, Func<int, float[]?>? caretX, int[] counts, LineWrap?[] wraps)
    {
        var span = table.Span(line);
        // Cheap exit for the common case: even at the widest possible advance the line fits.
        var widest = options.CharWidth * Math.Max(1, options.TabSize);
        if (caretX is null && span.Length * widest <= options.Width)
        {
            counts[line] = 1;
            return;
        }

        FillOne(span.ToString(), line, options, caretX, counts, wraps);
    }

    private static void FillOne(string text, int line, WrapOptions options, Func<int, float[]?>? caretX, int[] counts, LineWrap?[] wraps)
    {
        var wrap = Wrap(text, options, caretX?.Invoke(line));
        wraps[line] = wrap;
        counts[line] = wrap is null ? 1 : wrap.Breaks.Length + 1;
    }

    /// <summary>The wrap of one line of text, or null when it fits. <paramref name="x"/> is the shaped caret positions, if any.</summary>
    public static LineWrap? Wrap(string text, WrapOptions options, float[]? x = null)
    {
        var length = text.Length;
        if (length == 0) return null;
        if (x is not null && x.Length != length + 1) x = null;

        // Boundary positions: x[i] is the distance at which character i starts.
        var pos = x ?? Monospace(text, options);
        if (pos[length] <= options.Width) return null;

        // Where continuations start: under the line's own indentation (plus one unit), unless that would leave no room.
        var offset = 0f;
        if (options.Indent != WrapIndent.None)
        {
            var indentChars = 0;
            while (indentChars < length && text[indentChars] is ' ' or '\t') indentChars++;
            offset = pos[indentChars] - pos[0];
            if (options.Indent == WrapIndent.Indent) offset += options.CharWidth * options.TabSize;
            if (options.Indent == WrapIndent.DeepIndent) offset += 2 * options.CharWidth * options.TabSize;
            if (offset > options.Width / 2) offset = 0; // VS Code gives up on the indent when it takes over half the width
        }

        var breaks = new List<int>();
        var start = 0;
        var limitNeeded = options.Width;
        while (true)
        {
            // Room on this visual line: continuation lines start `offset` in.
            var origin = pos[start] - (start == 0 ? 0 : offset);
            if (pos[length] - origin <= limitNeeded) break;

            // The last character that still fits ends at `fit`; the line must break at or before `fit`.
            var fit = start;
            while (fit < length && pos[fit + 1] - origin <= limitNeeded) fit++;
            if (fit >= length) break;
            var at = BreakAt(text, start, fit, options);
            if (at <= start) at = Math.Max(fit, start + 1);
            // Never between the halves of a surrogate pair.
            if (at < length && at > 0 && char.IsLowSurrogate(text[at]) && char.IsHighSurrogate(text[at - 1])) at = at - 1 > start ? at - 1 : at + 1;
            breaks.Add(at);
            start = at;
        }

        return breaks.Count == 0 ? null : new LineWrap([.. breaks], offset);
    }

    /// <summary>The best column in (start, fit] to start the next visual line at: after an allowed character, else the hard break at fit.</summary>
    private static int BreakAt(string text, int start, int fit, WrapOptions options)
    {
        // A break at column c puts text[c] on the next line. Allowed when text[c-1] is a break-after char (a run of
        // spaces stays together, so the break comes after it) or text[c] is a break-before char.
        for (var c = Math.Min(fit, text.Length - 1); c > start; c--)
        {
            var prev = text[c - 1];
            var here = text[c];
            if (options.BreakAfter.Contains(prev) && !(options.BreakAfter.Contains(here) && here is ' ' or '\t')) return c;
            if (options.BreakBefore.Contains(here) && prev is not (' ' or '\t')) return c;
        }

        return fit;
    }

    private static float[] Monospace(string text, WrapOptions options)
    {
        var pos = new float[text.Length + 1];
        var column = 0;
        var x = 0f;
        for (var i = 0; i < text.Length; i++)
        {
            pos[i] = x;
            var c = text[i];
            if (c == '\t')
            {
                var advance = options.TabSize - column % options.TabSize;
                column += advance;
                x += advance * options.CharWidth;
            }
            else if (!char.IsLowSurrogate(c))
            {
                column++;
                x += options.CharWidth;
            }
        }

        pos[text.Length] = x;
        return pos;
    }
}
