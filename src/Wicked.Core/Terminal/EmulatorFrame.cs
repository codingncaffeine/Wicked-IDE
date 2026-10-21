namespace Wicked.Core.Terminal;

// Reading the terminal out: frames for the view, text for the clipboard and search, commands for navigation.
public sealed partial class TerminalEmulator
{
    /// <summary>
    /// A snapshot of what is visible. Screen rows are copied (the emulator keeps changing them); scrollback rows are shared,
    /// because a line in the scrollback is never edited again.
    /// </summary>
    public TerminalFrame BuildFrame()
    {
        var history = _alt ? 0 : _scrollback.Count;
        var offset = Math.Min(_viewportOffset, history);
        var lines = new FrameRow[_rowCount];
        for (var r = 0; r < _rowCount; r++)
        {
            var index = history - offset + r;
            if (index < history)
            {
                var line = _scrollback[index];
                lines[r] = new FrameRow(line.Cells, line.Wrapped, line.Command);
            }
            else
            {
                var line = _rows[index - history];
                var cells = line.Cells;
                var n = TrimmedLength(cells);
                var copy = new Cell[n];
                Array.Copy(cells, copy, n);
                lines[r] = new FrameRow(copy, line.Wrapped, line.Command);
            }
        }

        var cursorVisible = _modes.CursorVisible && offset == 0;
        return new TerminalFrame
        {
            Columns = _cols,
            Rows = _rowCount,
            Lines = lines,
            Version = ++_frameVersion,
            TopLine = _baseLine + _scrollback.Count - offset,
            Epoch = _epoch,
            ScrollbackLines = history,
            ViewportOffset = offset,
            CursorRow = cursorVisible ? _row : -1,
            CursorColumn = _col,
            Modes = _modes,
            Clusters = Clusters,
            Links = Links,
            PaletteOverrides = _paletteOverrides,
            ForegroundOverride = _fgOverride,
            BackgroundOverride = _bgOverride,
            CursorColorOverride = _cursorOverride,
            Title = _title,
            BellCount = _bellCount,
        };
    }

    // ── Text ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The text of cells <paramref name="from"/> up to <paramref name="to"/> (exclusive): empty cells read as spaces, a wide character once.</summary>
    internal string RowText(Cell[] cells, int from, int to)
    {
        to = Math.Min(to, cells.Length);
        var text = new System.Text.StringBuilder(Math.Max(0, to - from));
        for (var i = Math.Max(0, from); i < to; i++)
        {
            var cell = cells[i];
            if ((cell.Flags & CellFlags.WideTail) != 0) continue;
            if ((cell.Flags & CellFlags.Cluster) != 0) text.Append(Clusters[cell.Rune]);
            else if (cell.Rune == 0) text.Append(' ');
            else text.Append(char.ConvertFromUtf32(cell.Rune));
        }

        return text.ToString();
    }

    /// <summary>The text a selection covers; lines the terminal wrapped are joined, lines the program ended are split.</summary>
    public string GetText(TerminalSelection selection)
    {
        var start = selection.Start;
        var end = selection.End;
        var text = new System.Text.StringBuilder();
        for (var line = start.Line; line <= end.Line; line++)
        {
            var row = LineAt(line);
            if (row is null)
            {
                if (line != end.Line) text.Append('\n');
                continue;
            }

            int a, b;
            switch (selection.Kind)
            {
                case SelectionKind.Lines:
                    a = 0;
                    b = int.MaxValue;
                    break;
                case SelectionKind.Block:
                    a = Math.Min(selection.Anchor.Column, selection.Active.Column);
                    b = Math.Max(selection.Anchor.Column, selection.Active.Column);
                    break;
                default:
                    a = line == start.Line ? start.Column : 0;
                    b = line == end.Line ? end.Column : int.MaxValue;
                    break;
            }

            var content = TrimmedLength(row.Cells);
            var stop = b == int.MaxValue ? content : Math.Min(b + 1, row.Cells.Length);
            // A selection that reaches past the end of the text takes the text only; one that ends inside it takes exactly what it covers.
            if (b == int.MaxValue || b + 1 >= content) stop = Math.Min(stop, content);
            if (stop > row.Cells.Length) stop = row.Cells.Length;
            var piece = a < stop ? RowText(row.Cells, a, stop) : "";
            // A selection that starts or ends inside a wide character takes the whole character.
            text.Append(piece);
            if (line != end.Line && (!row.Wrapped || selection.Kind == SelectionKind.Block)) text.Append('\n');
        }

        return text.ToString();
    }

    private static bool IsWordCharacter(string text) => text.Length > 0 && !" \t()[]{}'\",`|<>;─‘’“”".Contains(text[0], StringComparison.Ordinal);

    /// <summary>The columns of the word at <paramref name="column"/> of the line (inclusive), or null for a gap between words.</summary>
    public (int Start, int End)? WordAt(long line, int column)
    {
        var row = LineAt(line);
        if (row is null) return null;
        var cells = row.Cells;
        if (column >= cells.Length || column < 0) return null;
        if ((cells[column].Flags & CellFlags.WideTail) != 0 && column > 0) column--;
        string TextOfCell(int c) => (cells[c].Flags & CellFlags.Cluster) != 0 ? Clusters[cells[c].Rune] : cells[c].Rune == 0 ? " " : char.ConvertFromUtf32(cells[c].Rune);
        if (!IsWordCharacter(TextOfCell(column))) return null;
        var s = column;
        while (s > 0)
        {
            var p = s - 1;
            if ((cells[p].Flags & CellFlags.WideTail) != 0 && p > 0) p--;
            if (!IsWordCharacter(TextOfCell(p))) break;
            s = p;
        }

        var e = column;
        while (e + 1 < cells.Length)
        {
            var nx = e + 1;
            if (!IsWordCharacter(TextOfCell(nx)) && (cells[nx].Flags & CellFlags.WideTail) == 0) break;
            e = nx;
        }

        return (s, e);
    }

    /// <summary>The absolute lines a logical line covers around <paramref name="line"/>: back to where it began, on to where the program ended it.</summary>
    public (long First, long Last) LogicalLineAt(long line)
    {
        var first = line;
        while (first - 1 >= _baseLine && LineAt(first - 1) is { Wrapped: true }) first--;
        var last = line;
        while (LineAt(last) is { Wrapped: true } && LineAt(last + 1) is not null) last++;
        return (first, last);
    }

    /// <summary>The lines in the buffer and the number of the first (absolute numbers run from <see cref="BaseLine"/> up through the scrollback and the main screen).</summary>
    public long LastLine => _baseLine + _scrollback.Count + _rowCount - 1;

    // ── The shell's commands ─────────────────────────────────────────────────────────────────────

    /// <summary>Every command the shell marked that is still in the buffer, oldest first, with the line its prompt is on.</summary>
    public List<TerminalCommand> Commands()
    {
        var list = new List<TerminalCommand>();
        for (var i = 0; i < _scrollback.Count; i++)
        {
            if (_scrollback[i].Command is { } c) list.Add(new TerminalCommand(_baseLine + i, c));
        }

        var screen = _screens[0];
        for (var r = 0; r < screen.Length; r++)
        {
            if (screen[r].Command is { } c) list.Add(new TerminalCommand(_baseLine + _scrollback.Count + r, c));
        }

        return list;
    }

    // ── Search ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a search reads: the scrollback's lines (shared, they never change) and a copy of the main screen. Made on the
    /// emulator's thread; the search itself can then run anywhere.
    /// </summary>
    public SearchSnapshot SnapshotForSearch()
    {
        var lines = new List<Line>(_scrollback.Count + _rowCount);
        for (var i = 0; i < _scrollback.Count; i++) lines.Add(_scrollback[i]);
        foreach (var row in _screens[0])
        {
            var n = TrimmedLength(row.Cells);
            var copy = new Cell[n];
            Array.Copy(row.Cells, copy, n);
            lines.Add(new Line(copy, row.Wrapped));
        }

        return new SearchSnapshot(lines.ToArray(), _baseLine, _cols, Clusters, _epoch);
    }
}
