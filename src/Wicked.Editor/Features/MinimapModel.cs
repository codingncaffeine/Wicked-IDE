using Wicked.Editor.Model;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>One drawn block of the minimap: a run of non-blank characters of one colour, in visual columns.</summary>
public readonly record struct MinimapRun(int StartColumn, int Length, int ColorId);

/// <summary>
/// What a VS Code "blocks" minimap needs from the text, per line: the indentation in columns and a run-length list of
/// non-blank character runs with their colour ids (a rectangle each, no glyphs). Columns are visual (tabs expanded) and
/// clipped at <see cref="MaxColumns"/> (VS Code's <c>minimap.maxColumn</c>, 120). Colour ids index
/// <see cref="Colors"/>, the syntax snapshot's table, so a theme change only swaps that table.
/// <para>
/// Storage is two flat arrays (a line's runs are a slice of one int array, each run packed in a single int), so 100,000
/// lines cost a few megabytes and the painter reads a line without allocating. Built on a worker; immutable and tagged
/// with a version. <see cref="Update"/> re-summarises only the edited lines; <see cref="UpdateLines"/> does the same for
/// a line range when the syntax snapshot catches up.
/// </para>
/// </summary>
public sealed class MinimapModel
{
    private const int DefaultColorId = 1;

    private readonly int[] _lineOffsets; // _lineOffsets[l]..[l+1] bounds line l's runs in _runs
    private readonly int[] _runs;        // start (8 bits) | length (8 bits) | colour id (16 bits)
    private readonly byte[] _indents;

    private MinimapModel(long version, int tabSize, int maxColumns, ColorTable? colors, int[] lineOffsets, int[] runs, byte[] indents)
    {
        Version = version;
        TabSize = tabSize;
        MaxColumns = maxColumns;
        Colors = colors;
        _lineOffsets = lineOffsets;
        _runs = runs;
        _indents = indents;
    }

    public long Version { get; }

    public int TabSize { get; }

    public int MaxColumns { get; }

    /// <summary>The colour table the run colour ids refer to; null when built without highlighting (every run is the default colour).</summary>
    public ColorTable? Colors { get; }

    public int LineCount => _indents.Length;

    /// <summary>The indentation of a line in visual columns (clipped to <see cref="MaxColumns"/>; 0 for a blank line).</summary>
    public int Indent(int line) => _indents[line];

    public int RunCount(int line) => _lineOffsets[line + 1] - _lineOffsets[line];

    public MinimapRun Run(int line, int index)
    {
        var packed = _runs[_lineOffsets[line] + index];
        return new MinimapRun(packed & 0xFF, (packed >> 8) & 0xFF, (packed >>> 16) & 0xFFFF);
    }

    /// <summary>The ARGB colour of a run's colour id.</summary>
    public uint ColorOf(int colorId) => Colors?[colorId] ?? 0xFFD4D4D4;

    // ── Building (worker) ────────────────────────────────────────────────────────────────────────

    public static MinimapModel Build(Rope text, long version, SyntaxSnapshot? syntax, int tabSize = 4, int maxColumns = 120)
    {
        tabSize = Math.Max(1, tabSize);
        maxColumns = Math.Clamp(maxColumns, 1, 255);
        var table = LineTable.From(text);
        var offsets = new int[table.Count + 1];
        var indents = new byte[table.Count];
        var runs = new List<int>(table.Count * 4);
        for (var line = 0; line < table.Count; line++)
        {
            offsets[line] = runs.Count;
            indents[line] = Summarise(table.Span(line), syntax?[line], tabSize, maxColumns, runs);
        }

        offsets[table.Count] = runs.Count;
        return new MinimapModel(version, tabSize, maxColumns, syntax?.Colors, offsets, [.. runs], indents);
    }

    /// <summary>The model after an edit: lines the change touched are summarised again; the rest are carried over.</summary>
    public MinimapModel Update(DocumentChange change, SyntaxSnapshot? syntax)
    {
        if (change.Changes.IsDefaultOrEmpty) return With(change.Version, syntax, this);
        var firstOld = change.Before.LineOf(change.Changes[0].Offset);
        var lastOld = change.Before.LineOf(change.Changes[^1].End);
        var lastNew = lastOld + (change.After.LineCount - change.Before.LineCount);
        return Splice(change.After, change.Version, syntax, firstOld, lastOld, lastNew);
    }

    /// <summary>
    /// The model with lines <paramref name="first"/>..<paramref name="last"/> summarised again from the same text, for
    /// when new highlighting arrives for them. <paramref name="text"/> must have the line count this model has.
    /// </summary>
    public MinimapModel UpdateLines(Rope text, long version, SyntaxSnapshot? syntax, int first, int last)
    {
        if (text.LineCount != LineCount) return Build(text, version, syntax, TabSize, MaxColumns);
        first = Math.Clamp(first, 0, LineCount - 1);
        last = Math.Clamp(last, first, LineCount - 1);
        return Splice(text, version, syntax, first, last, last);
    }

    private static MinimapModel With(long version, SyntaxSnapshot? syntax, MinimapModel m) =>
        new(version, m.TabSize, m.MaxColumns, syntax?.Colors ?? m.Colors, m._lineOffsets, m._runs, m._indents);

    private MinimapModel Splice(Rope after, long version, SyntaxSnapshot? syntax, int firstOld, int lastOld, int lastNew)
    {
        var newCount = after.LineCount;
        var middle = new List<int>();
        var middleOffsets = new int[lastNew - firstOld + 1];
        var middleIndents = new byte[lastNew - firstOld + 1];
        for (var line = firstOld; line <= lastNew; line++)
        {
            middleOffsets[line - firstOld] = middle.Count;
            var start = after.LineStart(line);
            var span = after.Substring(start, after.LineLength(line));
            middleIndents[line - firstOld] = Summarise(span, syntax?[line], TabSize, MaxColumns, middle);
        }

        var headRuns = _lineOffsets[firstOld];
        var tailRunsStart = _lineOffsets[lastOld + 1];
        var tailRuns = _runs.Length - tailRunsStart;
        var runs = new int[headRuns + middle.Count + tailRuns];
        Array.Copy(_runs, 0, runs, 0, headRuns);
        middle.CopyTo(runs, headRuns);
        Array.Copy(_runs, tailRunsStart, runs, headRuns + middle.Count, tailRuns);

        var offsets = new int[newCount + 1];
        Array.Copy(_lineOffsets, 0, offsets, 0, firstOld);
        for (var i = 0; i < middleOffsets.Length; i++) offsets[firstOld + i] = headRuns + middleOffsets[i];
        var shift = headRuns + middle.Count - tailRunsStart;
        for (var line = lastNew + 1; line <= newCount; line++) offsets[line] = _lineOffsets[line - (lastNew - lastOld)] + shift;

        var indents = new byte[newCount];
        Array.Copy(_indents, 0, indents, 0, firstOld);
        Array.Copy(middleIndents, 0, indents, firstOld, middleIndents.Length);
        Array.Copy(_indents, lastOld + 1, indents, lastNew + 1, _indents.Length - lastOld - 1);
        return new MinimapModel(version, TabSize, MaxColumns, syntax?.Colors ?? Colors, offsets, runs, indents);
    }

    /// <summary>Appends the packed runs of one line to <paramref name="into"/> and returns its indentation.</summary>
    private static byte Summarise(ReadOnlySpan<char> line, LineTokens? tokens, int tabSize, int maxColumns, List<int> into)
    {
        var column = 0;
        var indent = -1;
        var ti = 0;
        var runStart = -1;
        var runColor = 0;
        for (var i = 0; i < line.Length && column < maxColumns; i++)
        {
            var c = line[i];
            if (c is ' ' or '\t' or '\r')
            {
                if (runStart >= 0)
                {
                    into.Add(Pack(runStart, column - runStart, runColor));
                    runStart = -1;
                }

                column += c == '\t' ? tabSize - column % tabSize : c == ' ' ? 1 : 0;
                continue;
            }

            if (indent < 0) indent = column;
            if (char.IsLowSurrogate(c)) continue;

            var color = DefaultColorId;
            if (tokens is not null && tokens.Count > 0)
            {
                while (ti + 1 < tokens.Count && tokens.Start(ti + 1) <= i) ti++;
                color = tokens.Foreground(ti);
            }

            if (runStart >= 0 && color != runColor)
            {
                into.Add(Pack(runStart, column - runStart, runColor));
                runStart = -1;
            }

            if (runStart < 0)
            {
                runStart = column;
                runColor = color;
            }

            column++;
        }

        if (runStart >= 0) into.Add(Pack(runStart, Math.Min(column, maxColumns) - runStart, runColor));
        return (byte)Math.Min(indent < 0 ? 0 : indent, maxColumns);
    }

    private static int Pack(int start, int length, int color) => start | (Math.Min(length, 255) << 8) | (Math.Min(color, 0xFFFF) << 16);
}
