using System.Text;
using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

/// <summary>A terminal emulator with a readable screen, for tests: feed it text and escape sequences, then read rows, cells, replies and events.</summary>
internal sealed class VtScreen
{
    public VtScreen(int columns = 20, int rows = 6, TerminalOptions? options = null)
    {
        Emulator = new TerminalEmulator(columns, rows, options);
        Emulator.Reply += bytes => Replies.Append(Encoding.UTF8.GetString(bytes.Span));
        Emulator.TitleChanged += title => Titles.Add(title);
        Emulator.WorkingDirectoryChanged += dir => Directories.Add(dir);
        Emulator.ClipboardWrite += text => Clipboard.Add(text);
        Emulator.Bell += () => Bells++;
    }

    public TerminalEmulator Emulator { get; }

    public StringBuilder Replies { get; } = new();

    public List<string> Titles { get; } = [];

    public List<string> Directories { get; } = [];

    public List<string> Clipboard { get; } = [];

    public int Bells { get; private set; }

    public VtScreen Feed(string text)
    {
        Emulator.Write(Encoding.UTF8.GetBytes(text));
        return this;
    }

    public VtScreen Feed(params byte[] bytes)
    {
        Emulator.Write(bytes);
        return this;
    }

    public TerminalFrame Frame => Emulator.BuildFrame();

    /// <summary>The visible row as text, trailing blanks dropped.</summary>
    public string Row(int row)
    {
        var frame = Frame;
        return Emulator.RowText(frame.Lines[row].Cells, 0, frame.Columns).TrimEnd();
    }

    /// <summary>All visible rows, trailing blank rows dropped.</summary>
    public string[] Rows()
    {
        var frame = Frame;
        var rows = Enumerable.Range(0, frame.Rows).Select(Row).ToList();
        while (rows.Count > 0 && rows[^1].Length == 0) rows.RemoveAt(rows.Count - 1);
        return rows.ToArray();
    }

    public string Screen => string.Join('\n', Rows());

    public Cell Cell(int row, int column)
    {
        var cells = Frame.Lines[row].Cells;
        return column < cells.Length ? cells[column] : default;
    }

    public (int Row, int Column) Cursor => (Emulator.CursorRow, Emulator.CursorColumn);

    /// <summary>The text of the scrollback, oldest first.</summary>
    public string[] Scrollback()
    {
        var snapshot = Emulator.SnapshotForSearch();
        var lines = new List<string>();
        var count = Emulator.ScrollbackCount;
        for (long i = 0; i < count; i++) lines.Add(Emulator.RowText(Emulator.LineAt(Emulator.BaseLine + i)!.Cells, 0, int.MaxValue).TrimEnd());
        _ = snapshot;
        return lines.ToArray();
    }
}
