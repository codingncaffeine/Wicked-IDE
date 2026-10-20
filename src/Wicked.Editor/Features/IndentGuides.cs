using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>One vertical indent guide: at the left edge of indentation <paramref name="Level"/> (1 is the first, at column 0).</summary>
public readonly record struct IndentGuide(int Line, int Level, bool IsActive);

/// <summary>The guide level the caret's block highlights, and the lines that block spans. <see cref="Level"/> 0 means none.</summary>
public readonly record struct ActiveGuide(int Level, int FirstLine, int LastLine)
{
    public static readonly ActiveGuide None = new(0, 0, -1);
}

/// <summary>
/// The indent guides of one document version. A line indented by <c>n</c> columns carries <c>ceil(n / tabSize)</c>
/// guides, at columns 0, tabSize, 2*tabSize ... each strictly inside the indentation. A blank line takes the smaller of
/// the guide counts of the nearest non-blank lines above and below, so a guide runs unbroken through blank lines in the
/// middle of a block and stops at the end of one. Built on a worker; the lookups the view calls per frame read an int
/// array and allocate nothing. Indentation is measured with tabs expanded to tab stops; <c>insertSpaces</c> does not
/// change the measure (a file indented with tabs still has one guide per tab).
/// </summary>
public sealed class IndentGuides
{
    private readonly int[] _levels;

    private IndentGuides(long version, int tabSize, int[] levels)
    {
        Version = version;
        TabSize = tabSize;
        _levels = levels;
    }

    public long Version { get; }

    public int TabSize { get; }

    public int LineCount => _levels.Length;

    public static IndentGuides Compute(Rope text, long version, int tabSize, bool insertSpaces = true)
    {
        _ = insertSpaces;
        tabSize = Math.Max(1, tabSize);
        var indents = Indentation.MeasureAll(LineTable.From(text), tabSize);
        return FromIndents(version, tabSize, indents);
    }

    internal static IndentGuides FromIndents(long version, int tabSize, int[] indents)
    {
        var levels = new int[indents.Length];
        for (var i = 0; i < indents.Length; i++) levels[i] = indents[i] < 0 ? -1 : (indents[i] + tabSize - 1) / tabSize;

        // Blank lines: the smaller of the guide counts above and below, found by two sweeps.
        var below = new int[levels.Length];
        var next = 0;
        for (var i = levels.Length - 1; i >= 0; i--)
        {
            if (levels[i] >= 0) next = levels[i];
            below[i] = next;
        }

        var prev = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            if (levels[i] >= 0) prev = levels[i];
            else levels[i] = Math.Min(prev, below[i]);
        }

        return new IndentGuides(version, tabSize, levels);
    }

    /// <summary>How many guides <paramref name="line"/> carries (its blank-line rule applied).</summary>
    public int GuideCount(int line) => (uint)line < (uint)_levels.Length ? _levels[line] : 0;

    /// <summary>
    /// The block the caret on <paramref name="caretLine"/> highlights. On a header line (the next line is indented
    /// deeper) it is the block that follows; otherwise the block that contains the line, at the line's own depth.
    /// A scan of the lines around the caret over an int array: microseconds even for a very large block.
    /// </summary>
    public ActiveGuide ActiveFor(int caretLine)
    {
        if ((uint)caretLine >= (uint)_levels.Length) return ActiveGuide.None;
        var level = _levels[caretLine];
        if (caretLine + 1 < _levels.Length && _levels[caretLine + 1] > level) level++;
        if (level == 0) return ActiveGuide.None;

        // The block is the run of lines carrying that guide; a header line just above it is not part of it.
        var first = caretLine;
        while (first > 0 && _levels[first - 1] >= level) first--;
        var last = caretLine;
        while (last + 1 < _levels.Length && _levels[last + 1] >= level) last++;
        if (_levels[caretLine] < level) first = caretLine + 1;
        return new ActiveGuide(level, first, last);
    }

    /// <summary>Appends the guides of lines <paramref name="firstLine"/>..<paramref name="lastLine"/> (inclusive) to <paramref name="into"/>.</summary>
    public void Collect(int firstLine, int lastLine, ActiveGuide active, List<IndentGuide> into)
    {
        firstLine = Math.Max(0, firstLine);
        lastLine = Math.Min(_levels.Length - 1, lastLine);
        for (var line = firstLine; line <= lastLine; line++)
        {
            var count = _levels[line];
            for (var level = 1; level <= count; level++)
            {
                var isActive = level == active.Level && line >= active.FirstLine && line <= active.LastLine;
                into.Add(new IndentGuide(line, level, isActive));
            }
        }
    }

    /// <summary>The guides of a visible range for a caret on <paramref name="caretLine"/>: one array per call.</summary>
    public IndentGuide[] GuidesFor(int firstLine, int lastLine, int caretLine)
    {
        var list = new List<IndentGuide>();
        Collect(firstLine, lastLine, ActiveFor(caretLine), list);
        return [.. list];
    }
}
