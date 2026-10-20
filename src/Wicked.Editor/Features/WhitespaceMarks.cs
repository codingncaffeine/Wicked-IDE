using Wicked.Editor.Model;

namespace Wicked.Editor.Features;

/// <summary>VS Code's <c>editor.renderWhitespace</c> values.</summary>
public enum WhitespaceMode
{
    /// <summary>Nothing is marked.</summary>
    None,

    /// <summary>Every space and tab except a single space between two words.</summary>
    Boundary,

    /// <summary>Whitespace inside the selections.</summary>
    Selection,

    /// <summary>Whitespace at the end of a line.</summary>
    Trailing,

    /// <summary>Every space and tab.</summary>
    All,
}

/// <summary>One whitespace character to mark: its index in the line, and whether it is a tab (an arrow) rather than a space (a dot).</summary>
public readonly record struct WhitespaceMark(int Column, bool IsTab);

/// <summary>
/// Which spaces and tabs of a line to draw a marker on, for each <see cref="WhitespaceMode"/>. A per-line function of
/// the line's text (and, for the selection mode, the selected column ranges within that line), cheap enough to call for
/// each visible line every frame: it appends to a list the caller reuses and allocates nothing itself. The painter turns
/// a column into an x position from the shaped line.
/// </summary>
public static class WhitespaceMarks
{
    /// <summary>
    /// Appends the marks of <paramref name="line"/> to <paramref name="into"/> and returns how many were added.
    /// <paramref name="selected"/> is only read in <see cref="WhitespaceMode.Selection"/>: sorted, non-overlapping
    /// [start, end) character ranges of this line that are selected (see <see cref="SelectedRanges"/>).
    /// </summary>
    public static int Collect(ReadOnlySpan<char> line, WhitespaceMode mode, ReadOnlySpan<(int Start, int End)> selected, List<WhitespaceMark> into)
    {
        if (mode == WhitespaceMode.None) return 0;
        var before = into.Count;
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] is not (' ' or '\t'))
            {
                i++;
                continue;
            }

            // A run of whitespace [i, end).
            var end = i;
            while (end < line.Length && line[end] is ' ' or '\t') end++;
            var atStart = i == 0;
            var atEnd = end == line.Length;

            switch (mode)
            {
                case WhitespaceMode.All:
                    AddRun(line, i, end, into);
                    break;
                case WhitespaceMode.Trailing:
                    if (atEnd) AddRun(line, i, end, into);
                    break;
                case WhitespaceMode.Boundary:
                    // A lone space with a word on each side is the one thing not marked.
                    if (end - i == 1 && line[i] == ' ' && !atStart && !atEnd) break;
                    AddRun(line, i, end, into);
                    break;
                case WhitespaceMode.Selection:
                    foreach (var (s, e) in selected)
                    {
                        if (e <= i) continue;
                        if (s >= end) break;
                        AddRun(line, Math.Max(i, s), Math.Min(end, e), into);
                    }

                    break;
            }

            i = end;
        }

        return into.Count - before;
    }

    private static void AddRun(ReadOnlySpan<char> line, int from, int to, List<WhitespaceMark> into)
    {
        for (var c = from; c < to; c++) into.Add(new WhitespaceMark(c, line[c] == '\t'));
    }

    /// <summary>
    /// The selected character ranges that fall on a line, for <see cref="WhitespaceMode.Selection"/>. Appends to
    /// <paramref name="into"/> (cleared first); <paramref name="selections"/> may be in any order.
    /// </summary>
    public static void SelectedRanges(IReadOnlyList<Selection> selections, int lineStartOffset, int lineLength, List<(int Start, int End)> into)
    {
        into.Clear();
        var lineEnd = lineStartOffset + lineLength;
        foreach (var s in selections)
        {
            if (s.IsEmpty || s.End <= lineStartOffset || s.Start >= lineEnd) continue;
            into.Add((Math.Max(s.Start, lineStartOffset) - lineStartOffset, Math.Min(s.End, lineEnd) - lineStartOffset));
        }

        into.Sort((a, b) => a.Start.CompareTo(b.Start));
    }
}
