namespace Wicked.Editor.Features;

/// <summary>Measuring how far a line is indented, shared by the indent guides and the folding ranges.</summary>
internal static class Indentation
{
    /// <summary>
    /// The indentation of a line in visual columns (a tab advances to the next multiple of <paramref name="tabSize"/>),
    /// or -1 when the line is empty or only whitespace.
    /// </summary>
    public static int Measure(ReadOnlySpan<char> line, int tabSize)
    {
        var column = 0;
        for (var i = 0; i < line.Length; i++)
        {
            switch (line[i])
            {
                case ' ':
                    column++;
                    break;
                case '\t':
                    column += tabSize - column % tabSize;
                    break;
                case '\r':
                case '\f':
                case '\v':
                    break;
                default:
                    return column;
            }
        }

        return -1;
    }

    /// <summary>The indentation of every line, -1 for blank ones.</summary>
    public static int[] MeasureAll(LineTable table, int tabSize)
    {
        var result = new int[table.Count];
        for (var i = 0; i < result.Length; i++) result[i] = Measure(table.Span(i), tabSize);
        return result;
    }
}
