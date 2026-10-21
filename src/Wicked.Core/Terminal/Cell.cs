namespace Wicked.Core.Terminal;

/// <summary>How a cell is drawn. Wide marks the first of the two cells a double-width character covers, WideTail the second.</summary>
[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1 << 0,
    Dim = 1 << 1,
    Italic = 1 << 2,
    Underline = 1 << 3,
    Blink = 1 << 4,
    Inverse = 1 << 5,
    Invisible = 1 << 6,
    Strike = 1 << 7,
    DoubleUnderline = 1 << 8,
    CurlyUnderline = 1 << 9,
    DottedUnderline = 1 << 10,
    DashedUnderline = 1 << 11,
    Wide = 1 << 12,
    WideTail = 1 << 13,

    /// <summary>The cell's text is a cluster (a base character with combining marks), whose index is in <see cref="Cell.Rune"/>.</summary>
    Cluster = 1 << 14,

    AnyUnderline = Underline | DoubleUnderline | CurlyUnderline | DottedUnderline | DashedUnderline,

    /// <summary>The flags a pen carries from cell to cell; the layout flags are the cell's own.</summary>
    Pen = Bold | Dim | Italic | Underline | Blink | Inverse | Invisible | Strike | DoubleUnderline | CurlyUnderline | DottedUnderline | DashedUnderline,
}

/// <summary>
/// A colour a cell asks for: the terminal's default, one of the 256 palette entries, or a 24-bit value. Packed into a
/// <see cref="uint"/> so a cell stays 16 bytes.
/// </summary>
public static class CellColor
{
    public const uint Default = 0;
    private const uint PaletteTag = 1u << 24;
    private const uint RgbTag = 2u << 24;

    public static uint Palette(int index) => PaletteTag | (uint)(index & 0xFF);

    public static uint Rgb(byte r, byte g, byte b) => RgbTag | ((uint)r << 16) | ((uint)g << 8) | b;

    public static bool IsDefault(uint color) => color == Default;

    public static bool IsPalette(uint color) => (color >> 24) == 1;

    public static bool IsRgb(uint color) => (color >> 24) == 2;

    public static int PaletteIndex(uint color) => (int)(color & 0xFF);

    /// <summary>The 0xAARRGGBB of an RGB colour.</summary>
    public static uint ToArgb(uint color) => 0xFF000000 | (color & 0xFFFFFF);
}

/// <summary>One character position: 16 bytes, so a screenful and ten thousand lines of scrollback stay small.</summary>
public struct Cell
{
    /// <summary>The Unicode code point, 0 for an empty cell; with <see cref="CellFlags.Cluster"/> an index into the cluster table.</summary>
    public int Rune;

    public uint Fg;

    public uint Bg;

    public CellFlags Flags;

    /// <summary>The hyperlink (OSC 8) the cell belongs to, 0 for none.</summary>
    public ushort Link;

    public readonly bool IsBlank => Rune == 0 && Fg == 0 && Bg == 0 && (Flags & (CellFlags.Pen | CellFlags.Wide | CellFlags.WideTail | CellFlags.Cluster)) == 0 && Link == 0;

}

/// <summary>Strings of cells that are more than one code point (a letter and its accents, an emoji sequence), kept apart so a cell stays small. Append-only; read by any thread.</summary>
public sealed class ClusterTable
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private string[] _items = new string[64];
    private int _count;

    public const int Limit = 1 << 20;

    public string this[int i] => i >= 0 && i < Volatile.Read(ref _count) ? Volatile.Read(ref _items)[i] : "";

    /// <summary>The index of <paramref name="text"/>, adding it; -1 when the table is full.</summary>
    public int Add(string text)
    {
        if (_index.TryGetValue(text, out var found)) return found;
        if (_count >= Limit) return -1;
        var items = _items;
        if (_count == items.Length)
        {
            items = new string[_count * 2];
            Array.Copy(_items, items, _count);
        }

        items[_count] = text;
        Volatile.Write(ref _items, items);
        _index[text] = _count;
        Volatile.Write(ref _count, _count + 1);
        return _count - 1;
    }
}

/// <summary>Hyperlink targets (OSC 8) by the id a cell carries. Append-only; read by any thread.</summary>
public sealed class LinkTable
{
    private readonly Dictionary<string, ushort> _index = new(StringComparer.Ordinal);
    private string[] _items = new string[16];
    private int _count = 1;

    public string? this[int id] => id > 0 && id < Volatile.Read(ref _count) ? Volatile.Read(ref _items)[id] : null;

    /// <summary>The id for <paramref name="url"/> (with the sender's grouping id, so two links to one address stay two), or 0 when the table is full.</summary>
    public ushort Add(string url, string groupId)
    {
        var key = groupId.Length == 0 ? url : groupId + "\u0001" + url;
        if (_index.TryGetValue(key, out var found)) return found;
        if (_count >= ushort.MaxValue) return 0;
        var items = _items;
        if (_count == items.Length)
        {
            items = new string[_count * 2];
            Array.Copy(_items, items, _count);
        }

        items[_count] = url;
        Volatile.Write(ref _items, items);
        var id = (ushort)_count;
        _index[key] = id;
        Volatile.Write(ref _count, _count + 1);
        return id;
    }
}

/// <summary>A command the shell reported through its integration (OSC 133 / 633): where it began, what it was, how it ended.</summary>
public sealed class CommandRecord
{
    /// <summary>1 for the first command of the session, counting up.</summary>
    public long Sequence { get; init; }

    public string? CommandLine { get; internal set; }

    /// <summary>The working directory when the prompt was drawn, when the shell said.</summary>
    public string? Directory { get; internal set; }

    /// <summary>Null until the command finished, and after it when the shell did not say.</summary>
    public int? ExitCode { get; internal set; }


    public bool OutputStarted { get; internal set; }

    public bool Finished { get; internal set; }

    public bool Running => OutputStarted && !Finished;

    /// <summary>The line the command's output starts on (set when it began running).</summary>
    internal Line? OutputLine { get; set; }

    internal Line? PromptLine { get; set; }

    internal Line? InputLine { get; set; }

    internal int InputColumn { get; set; }
}

/// <summary>One row of the terminal. A screen row holds a cell for every column; a scrollback row only as many as it needs.</summary>
public sealed class Line
{
    public Line(int columns) => Cells = new Cell[columns];

    public Line(Cell[] cells, bool wrapped)
    {
        Cells = cells;
        Wrapped = wrapped;
    }

    public Cell[] Cells { get; internal set; }

    /// <summary>The text carries on into the next row (it was wrapped by the terminal, not ended by the program).</summary>
    public bool Wrapped { get; internal set; }

    /// <summary>The shell's command that starts on this row, if any.</summary>
    public CommandRecord? Command { get; internal set; }


    /// <summary>A copy for the scrollback: trailing blank cells dropped.</summary>
    internal Line Archive()
    {
        var cells = Cells;
        var n = cells.Length;
        while (n > 0 && cells[n - 1].IsBlank) n--;
        var copy = new Cell[n];
        Array.Copy(cells, copy, n);
        return new Line(copy, Wrapped) { Command = Command };
    }

    internal bool IsBlankRow()
    {
        foreach (var c in Cells)
        {
            if (!c.IsBlank) return false;
        }

        return true;
    }
}
