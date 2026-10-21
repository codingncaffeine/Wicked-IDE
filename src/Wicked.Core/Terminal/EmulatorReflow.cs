namespace Wicked.Core.Terminal;

// Changing the size: the main screen and its scrollback are re-wrapped to the new width (a line the terminal wrapped is
// joined and wrapped again, a line the program ended stays ended); the alternate screen is cropped or padded.
public sealed partial class TerminalEmulator
{
    /// <summary>A position that must be found again after the text has moved: the cursor, the top of the screen.</summary>
    private struct Track
    {
        public int SourceRow;
        public int SourceColumn;
        public bool Pending;
        public int Row;
        public int Column;
        public bool NewPending;
        public bool Done;
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        if (columns == _cols && rows == _rowCount) return;

        // The cursor of the main screen: the live one, or the one saved when the alternate screen was entered.
        var mainCursor = _alt ? (_saved[0].Valid ? (_saved[0].Row, _saved[0].Col, _saved[0].WrapPending) : (0, 0, false)) : (_row, _col, _wrapPending);
        var reflowed = ReflowMain(columns, rows, mainCursor);

        var altRows = new Line[rows];
        for (var r = 0; r < rows; r++)
        {
            altRows[r] = new Line(columns);
            if (r < _screens[1].Length) Array.Copy(_screens[1][r].Cells, altRows[r].Cells, Math.Min(columns, _cols));
            if (r < _screens[1].Length) altRows[r].Wrapped = false;
        }

        // A wide character cut in half by the new edge would be left alone.
        foreach (var line in altRows)
        {
            if ((line.Cells[columns - 1].Flags & CellFlags.Wide) != 0) line.Cells[columns - 1] = new Cell();
            if ((line.Cells[0].Flags & CellFlags.WideTail) != 0) line.Cells[0] = new Cell();
        }

        var oldTabs = _tabs;
        _tabs = new bool[columns];
        for (var i = 0; i < columns; i++) _tabs[i] = i < oldTabs.Length ? oldTabs[i] : i % 8 == 0;

        var oldRows = _rowCount;
        _screens[0] = reflowed.Screen;
        _screens[1] = altRows;
        _cols = columns;
        _rowCount = rows;
        _rows = _alt ? _screens[1] : _screens[0];
        _top = 0;
        _bottom = rows - 1;
        _viewportOffset = 0;
        _epoch++;

        if (_alt)
        {
            _row = Math.Clamp(_row, 0, rows - 1);
            _col = Math.Clamp(_col, 0, columns - 1);
            _wrapPending = false;
            if (_saved[0].Valid)
            {
                _saved[0].Row = reflowed.Cursor.Row;
                _saved[0].Col = reflowed.Cursor.Column;
                _saved[0].WrapPending = reflowed.Cursor.Pending;
            }
        }
        else
        {
            _row = reflowed.Cursor.Row;
            _col = reflowed.Cursor.Column;
            _wrapPending = reflowed.Cursor.Pending;
        }

        foreach (var index in new[] { 0, 1 })
        {
            if (index == 0 && _alt) continue;
            _saved[index].Row = Math.Clamp(_saved[index].Row, 0, rows - 1);
            _saved[index].Col = Math.Clamp(_saved[index].Col, 0, columns - 1);
        }

        _ = oldRows;
        if (_current is { } current)
        {
            current.PromptLine = _screens[0].FirstOrDefault(l => ReferenceEquals(l.Command, current));
            current.InputLine = null;
        }
    }

    private (Line[] Screen, (int Row, int Column, bool Pending) Cursor) ReflowMain(int newCols, int newRows, (int Row, int Col, bool Pending) cursor)
    {
        var oldCols = _cols;
        var oldRows = _rowCount;
        var screen = _screens[0];
        var scrollbackCount = _scrollback.Count;

        // Rows below the cursor that are blank are not content: they come back as blank rows afterwards.
        var lastUsed = oldRows - 1;
        while (lastUsed > cursor.Row && screen[lastUsed].Command is null && screen[lastUsed].IsBlankRow()) lastUsed--;
        var total = scrollbackCount + lastUsed + 1;
        var contentReachedBottom = lastUsed == oldRows - 1;

        Line Source(int i) => i < scrollbackCount ? _scrollback[i] : screen[i - scrollbackCount];

        var tracks = new[]
        {
            new Track { SourceRow = scrollbackCount + cursor.Row, SourceColumn = cursor.Col, Pending = cursor.Pending },
            new Track { SourceRow = scrollbackCount, SourceColumn = 0 },
        };

        var output = new List<Line>(total + total / 8 + 4);
        var cells = new List<Cell>(Math.Max(oldCols, newCols) * 2);
        var index = 0;
        while (index < total)
        {
            var first = index;
            cells.Clear();
            var last = first;
            while (true)
            {
                var row = Source(last);
                if (row.Wrapped && last + 1 < total)
                {
                    AppendRow(cells, row.Cells, oldCols, trim: false);
                    last++;
                }
                else
                {
                    AppendRow(cells, row.Cells, oldCols, trim: true);
                    break;
                }
            }

            // Positions inside this logical line, as offsets into its cells; the line is at least long enough to hold them.
            var length = cells.Count;
            for (var t = 0; t < tracks.Length; t++)
            {
                ref var track = ref tracks[t];
                if (track.Done || track.SourceRow < first || track.SourceRow > last) continue;
                var offset = (track.SourceRow - first) * oldCols + track.SourceColumn + (track.Pending ? 1 : 0);
                while (length < offset) length++;
            }

            while (cells.Count < length) cells.Add(new Cell());
            Layout(cells, first, last, oldCols, newCols, output, tracks, Source(first).Command);
            index = last + 1;
        }

        var totalRows = output.Count;
        var cursorTrack = tracks[0];
        var topTrack = tracks[1];
        var top = topTrack.Done ? topTrack.Row : 0;
        var cursorRow = cursorTrack.Done ? cursorTrack.Row : Math.Max(0, totalRows - 1);
        if (cursorRow - top >= newRows) top = cursorRow - newRows + 1;
        if (top + newRows > totalRows && contentReachedBottom) top = Math.Max(0, totalRows - newRows);
        top = Math.Clamp(top, 0, Math.Max(0, totalRows - 1));

        var history = new List<Line>(top);
        for (var i = 0; i < top; i++) history.Add(output[i].Archive());
        var newScreen = new Line[newRows];
        for (var r = 0; r < newRows; r++) newScreen[r] = top + r < totalRows ? output[top + r] : new Line(newCols);

        _baseLine += scrollbackCount;
        var dropped = _scrollback.Replace(history);
        _baseLine += dropped;

        var screenCursorRow = Math.Clamp(cursorRow - top, 0, newRows - 1);
        return (newScreen, (screenCursorRow, Math.Clamp(cursorTrack.Column, 0, newCols - 1), cursorTrack.NewPending));
    }

    private static void AppendRow(List<Cell> cells, Cell[] row, int width, bool trim)
    {
        var n = trim ? TrimmedLength(row) : Math.Min(row.Length, width);
        for (var i = 0; i < n; i++) cells.Add(row[i]);
        if (!trim)
        {
            for (var i = n; i < width; i++) cells.Add(new Cell());
        }
    }

    private static int TrimmedLength(Cell[] row)
    {
        var n = row.Length;
        while (n > 0 && row[n - 1].IsBlank) n--;
        return n;
    }

    /// <summary>Lays one logical line out in rows of <paramref name="newCols"/> cells and resolves the tracked positions that lie in it.</summary>
    private static void Layout(List<Cell> cells, int firstSourceRow, int lastSourceRow, int oldCols, int newCols, List<Line> output, Track[] tracks, CommandRecord? command)
    {
        var length = cells.Count;
        var placedRow = new int[length + 1];
        var placedCol = new int[length + 1];
        var rowIndex = output.Count;
        var current = new Line(newCols) { Command = command };
        var x = 0;
        var pos = 0;
        while (pos < length)
        {
            var cell = cells[pos];
            if ((cell.Flags & CellFlags.WideTail) != 0)
            {
                placedRow[pos] = rowIndex;
                placedCol[pos] = Math.Max(0, x - 1);
                pos++;
                continue;
            }

            var wide = (cell.Flags & CellFlags.Wide) != 0 && pos + 1 < length && (cells[pos + 1].Flags & CellFlags.WideTail) != 0;
            var width = wide ? 2 : 1;
            if (!wide && (cell.Flags & CellFlags.Wide) != 0) cell.Flags &= ~CellFlags.Wide;
            if (x + width > newCols)
            {
                if (width > newCols)
                {
                    pos += 2;
                    continue;
                }

                current.Wrapped = true;
                output.Add(current);
                rowIndex++;
                current = new Line(newCols);
                x = 0;
            }

            placedRow[pos] = rowIndex;
            placedCol[pos] = x;
            current.Cells[x] = cell;
            if (wide)
            {
                current.Cells[x + 1] = cells[pos + 1];
                placedRow[pos + 1] = rowIndex;
                placedCol[pos + 1] = x + 1;
            }

            x += width;
            pos += wide ? 2 : 1;
        }

        placedRow[length] = rowIndex;
        placedCol[length] = x;

        for (var t = 0; t < tracks.Length; t++)
        {
            ref var track = ref tracks[t];
            if (track.Done || track.SourceRow < firstSourceRow || track.SourceRow > lastSourceRow) continue;
            var offset = (track.SourceRow - firstSourceRow) * oldCols + track.SourceColumn;
            track.Done = true;
            if (track.Pending)
            {
                // The cursor was waiting to wrap after the cell before this offset: it stays right after that cell.
                var cellBefore = Math.Min(offset, length - 1);
                var row = placedRow[cellBefore];
                var col = placedCol[cellBefore] + ((cells[cellBefore].Flags & CellFlags.Wide) != 0 ? 2 : 1);
                if (col >= newCols)
                {
                    track.Row = row;
                    track.Column = newCols - 1;
                    track.NewPending = true;
                }
                else
                {
                    track.Row = row;
                    track.Column = col;
                }

                continue;
            }

            var at = Math.Min(offset, length);
            if (at >= length && x >= newCols)
            {
                track.Row = rowIndex;
                track.Column = newCols - 1;
                track.NewPending = true;
            }
            else
            {
                track.Row = placedRow[at];
                track.Column = placedCol[at];
            }
        }

        output.Add(current);
    }
}
