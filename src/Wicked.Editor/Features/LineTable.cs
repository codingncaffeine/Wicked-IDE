using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>
/// A worker's flat view of one rope snapshot: the whole text as one string and where each line starts. Building it is a
/// single pass (a few milliseconds for 100,000 lines), after which every feature scans lines as plain spans without
/// walking the rope per line. Worker-side only: it copies the text.
/// </summary>
internal sealed class LineTable
{
    private readonly int[] _starts;

    private LineTable(string text, int[] starts)
    {
        Text = text;
        _starts = starts;
    }

    public string Text { get; }

    public int Count => _starts.Length;

    public static LineTable From(Rope rope)
    {
        var text = rope.ToString();
        var starts = new int[rope.LineCount];
        var line = 1;
        var span = text.AsSpan();
        var at = 0;
        while (line < starts.Length)
        {
            var next = span[at..].IndexOf('\n');
            if (next < 0) break;
            at += next + 1;
            starts[line++] = at;
        }

        return new LineTable(text, starts);
    }

    public int Start(int line) => _starts[line];

    /// <summary>The end of the line's text: its line break is not included.</summary>
    public int End(int line) => line + 1 < _starts.Length ? _starts[line + 1] - 1 : Text.Length;

    public ReadOnlySpan<char> Span(int line) => Text.AsSpan(_starts[line], End(line) - _starts[line]);

    /// <summary>The line holding <paramref name="offset"/> (an offset at a line break belongs to the line it ends).</summary>
    public int LineOf(int offset)
    {
        var i = Array.BinarySearch(_starts, offset);
        return i >= 0 ? i : ~i - 1;
    }
}
