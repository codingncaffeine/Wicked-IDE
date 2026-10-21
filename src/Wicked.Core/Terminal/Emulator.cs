using System.Text;

namespace Wicked.Core.Terminal;

/// <summary>
/// A VT100/xterm-compatible terminal: bytes in, a screen of cells, a scrollback, and replies out. It is not thread-safe;
/// one thread (the session's emulator thread) owns it, and everything the rest of the app sees goes out as an immutable
/// <see cref="TerminalFrame"/> or as a result of a call made on that thread.
/// </summary>
public sealed partial class TerminalEmulator
{
    private readonly TerminalOptions _options;
    private readonly ScrollbackRing _scrollback;
    private readonly Line[][] _screens = new Line[2][];
    private readonly SavedCursor[] _saved = new SavedCursor[2];
    private readonly uint[] _defaultPalette;
    private Line[] _rows;
    private bool _alt;
    private int _cols;
    private int _rowCount;
    private long _baseLine;
    private int _viewportOffset;
    private int _epoch;
    private long _frameVersion;
    private int _bellCount;
    private string _title = "";
    private uint[]? _paletteOverrides;
    private uint _fgOverride;
    private uint _bgOverride;
    private uint _cursorOverride;

    // The cursor and the pen.
    private int _row;
    private int _col;
    private bool _wrapPending;
    private bool _originMode;
    private bool _insertMode;
    private int _top;
    private int _bottom;
    private bool[] _tabs;
    private uint _fg;
    private uint _bg;
    private CellFlags _flags;
    private ushort _link;
    private readonly Charset[] _charsets = [Charset.Ascii, Charset.Ascii, Charset.Ascii, Charset.Ascii];
    private int _glSet;
    private bool _joinNext;
    private int _lastPrinted;

    private TerminalModes _modes = new();

    public TerminalEmulator(int columns, int rows, TerminalOptions? options = null)
    {
        _options = options ?? new TerminalOptions();
        _cols = Math.Max(1, columns);
        _rowCount = Math.Max(1, rows);
        _scrollback = new ScrollbackRing(Math.Max(0, _options.Scrollback));
        _defaultPalette = Palette.Build(_options.Ansi);
        Clusters = new ClusterTable();
        Links = new LinkTable();
        _screens[0] = NewScreen();
        _screens[1] = NewScreen();
        _rows = _screens[0];
        _tabs = new bool[_cols];
        _bottom = _rowCount - 1;
        ResetTabs();
    }

    public int Columns => _cols;

    public int Rows => _rowCount;

    public ClusterTable Clusters { get; }

    public LinkTable Links { get; }

    /// <summary>What the program has asked for; replaced whenever it changes, so reading it from another thread is safe.</summary>
    public TerminalModes Modes => Volatile.Read(ref _modes);

    public string Title => _title;

    /// <summary>The working directory the shell last reported (OSC 7 or 633), or null.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>How many lines are in the scrollback.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>The absolute number of the oldest line still held; absolute numbers never change while a line lives.</summary>
    public long BaseLine => _baseLine;

    public int ViewportOffset => _viewportOffset;

    public int Epoch => _epoch;

    public int BellCount => _bellCount;

    public int CursorRow => _row;

    public int CursorColumn => _col;

    public bool CursorPendingWrap => _wrapPending;

    public event Action<ReadOnlyMemory<byte>>? Reply;

    public event Action<string>? TitleChanged;

    public event Action? Bell;

    public event Action<string>? WorkingDirectoryChanged;

    public event Action<string>? ClipboardWrite;

    public event Action<CommandRecord>? PromptStarted;

    public event Action<CommandRecord>? CommandStarted;

    public event Action<CommandRecord>? CommandFinished;

    public event Action<TerminalModes>? ModesChanged;

    /// <summary>The ConEmu progress report (OSC 9;4): a state (0 hidden, 1 normal, 2 error, 3 indeterminate, 4 warning) and a percentage.</summary>
    public event Action<int, int>? ProgressChanged;

    private enum Charset
    {
        Ascii,
        DecGraphics,
    }

    private struct SavedCursor
    {
        public int Row;
        public int Col;
        public bool WrapPending;
        public bool Origin;
        public uint Fg;
        public uint Bg;
        public CellFlags Flags;
        public ushort Link;
        public int GlSet;
        public Charset G0, G1, G2, G3;
        public bool Valid;
    }

    private Line[] NewScreen()
    {
        var rows = new Line[_rowCount];
        for (var i = 0; i < rows.Length; i++) rows[i] = new Line(_cols);
        return rows;
    }

    private void SetModes(TerminalModes modes)
    {
        if (modes == _modes) return;
        Volatile.Write(ref _modes, modes);
        ModesChanged?.Invoke(modes);
    }

    private void Respond(string text)
    {
        if (Reply is null) return;
        Reply(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>A full reset (RIS): the screen, the scrollback, the modes and the pen go back to how a new terminal starts.</summary>
    public void Reset()
    {
        _screens[0] = NewScreen();
        _screens[1] = NewScreen();
        _alt = false;
        _rows = _screens[0];
        _scrollback.Clear();
        _baseLine = 0;
        _viewportOffset = 0;
        _epoch++;
        _row = _col = 0;
        _wrapPending = false;
        _originMode = false;
        _insertMode = false;
        _top = 0;
        _bottom = _rowCount - 1;
        ResetPen();
        _charsets[0] = _charsets[1] = _charsets[2] = _charsets[3] = Charset.Ascii;
        _glSet = 0;
        _joinNext = false;
        _saved[0] = default;
        _saved[1] = default;
        ResetTabs();
        _paletteOverrides = null;
        _fgOverride = _bgOverride = _cursorOverride = 0;
        _link = 0;
        _current = null;
        SetModes(new TerminalModes());
    }

    private void ResetPen()
    {
        _fg = 0;
        _bg = 0;
        _flags = CellFlags.None;
    }

    private void ResetTabs()
    {
        for (var i = 0; i < _tabs.Length; i++) _tabs[i] = i % 8 == 0 && i > 0;
    }

    // ── Cursor and scrolling ─────────────────────────────────────────────────────────────────────

    private Cell EraseCell => new() { Bg = _bg };

    private int MinRow => _originMode ? _top : 0;

    private int MaxRow => _originMode ? _bottom : _rowCount - 1;

    private void MoveTo(int row, int col)
    {
        _row = Math.Clamp(row, MinRow, MaxRow);
        _col = Math.Clamp(col, 0, _cols - 1);
        _wrapPending = false;
    }

    /// <summary>Down one line, scrolling the region when the cursor is on its last row.</summary>
    private void Index()
    {
        if (_row == _bottom) ScrollUp(_top, _bottom, 1, toScrollback: _top == 0 && !_alt);
        else if (_row < _rowCount - 1) _row++;
        _wrapPending = false;
    }

    private void ReverseIndex()
    {
        if (_row == _top) ScrollDown(_top, _bottom, 1);
        else if (_row > 0) _row--;
        _wrapPending = false;
    }

    private void LineFeed()
    {
        Index();
        if (_modes.NewLineMode) _col = 0;
    }

    private void CarriageReturn()
    {
        _col = 0;
        _wrapPending = false;
    }

    private void Wrap()
    {
        _rows[_row].Wrapped = true;
        Index();
        _col = 0;
        _wrapPending = false;
    }

    private void ScrollUp(int top, int bottom, int count, bool toScrollback)
    {
        count = Math.Min(count, bottom - top + 1);
        if (count <= 0) return;
        var erase = EraseCell;
        for (var n = 0; n < count; n++)
        {
            var first = _rows[top];
            if (toScrollback && _scrollback.Capacity > 0)
            {
                if (_scrollback.Push(first.Archive())) _baseLine++;
                if (_viewportOffset > 0) _viewportOffset = Math.Min(_viewportOffset + 1, _scrollback.Count);
            }

            Array.Copy(_rows, top + 1, _rows, top, bottom - top);
            Clear(first, erase);
            _rows[bottom] = first;
        }
    }

    private void ScrollDown(int top, int bottom, int count)
    {
        count = Math.Min(count, bottom - top + 1);
        if (count <= 0) return;
        var erase = EraseCell;
        for (var n = 0; n < count; n++)
        {
            var last = _rows[bottom];
            Array.Copy(_rows, top, _rows, top + 1, bottom - top);
            Clear(last, erase);
            _rows[top] = last;
        }
    }

    private static void Clear(Line line, Cell erase)
    {
        if (erase.Bg == 0) Array.Clear(line.Cells);
        else Array.Fill(line.Cells, erase);
        line.Wrapped = false;
        line.Command = null;
    }

    private void Tab(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var c = _col + 1;
            while (c < _cols - 1 && !_tabs[c]) c++;
            _col = Math.Min(c, _cols - 1);
        }

        _wrapPending = false;
    }

    private void BackTab(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var c = _col - 1;
            while (c > 0 && !_tabs[c]) c--;
            _col = Math.Max(c, 0);
        }

        _wrapPending = false;
    }

    private void SaveCursor()
    {
        _saved[_alt ? 1 : 0] = new SavedCursor
        {
            Row = _row, Col = _col, WrapPending = _wrapPending, Origin = _originMode, Fg = _fg, Bg = _bg, Flags = _flags, Link = _link,
            GlSet = _glSet, G0 = _charsets[0], G1 = _charsets[1], G2 = _charsets[2], G3 = _charsets[3], Valid = true,
        };
    }

    private void RestoreCursor()
    {
        var s = _saved[_alt ? 1 : 0];
        if (!s.Valid)
        {
            MoveTo(0, 0);
            ResetPen();
            _originMode = false;
            return;
        }

        _originMode = s.Origin;
        _row = Math.Clamp(s.Row, 0, _rowCount - 1);
        _col = Math.Clamp(s.Col, 0, _cols - 1);
        _wrapPending = s.WrapPending;
        _fg = s.Fg;
        _bg = s.Bg;
        _flags = s.Flags;
        _link = s.Link;
        _glSet = s.GlSet;
        _charsets[0] = s.G0;
        _charsets[1] = s.G1;
        _charsets[2] = s.G2;
        _charsets[3] = s.G3;
    }

    private void EnterAlternate(bool saveCursor, bool clear)
    {
        if (_alt) return;
        if (saveCursor) SaveCursor();
        _alt = true;
        _rows = _screens[1];
        if (clear)
        {
            foreach (var line in _rows) Clear(line, new Cell());
        }

        _viewportOffset = 0;
        SetModes(_modes with { AlternateScreen = true });
    }

    private void LeaveAlternate(bool restoreCursor)
    {
        if (!_alt) return;
        _alt = false;
        _rows = _screens[0];
        if (restoreCursor) RestoreCursor();
        SetModes(_modes with { AlternateScreen = false });
    }

    // ── Printing ─────────────────────────────────────────────────────────────────────────────────

    private static readonly int[] DecGraphics =
    [
        0x00A0, 0x25C6, 0x2592, 0x2409, 0x240C, 0x240D, 0x240A, 0x00B0, 0x00B1, 0x2424, 0x240B, 0x2518, 0x2510, 0x250C, 0x2514,
        0x253C, 0x23BA, 0x23BB, 0x2500, 0x23BC, 0x23BD, 0x251C, 0x2524, 0x2534, 0x252C, 0x2502, 0x2264, 0x2265, 0x03C0, 0x2260,
        0x00A3, 0x00B7,
    ];

    private bool PlainAscii => _charsets[_glSet] == Charset.Ascii && !_insertMode && !_joinNext;

    /// <summary>A run of printable ASCII: the common case, kept free of per-character decisions.</summary>
    private void PrintAscii(ReadOnlySpan<byte> text)
    {
        if (!PlainAscii)
        {
            foreach (var b in text) PutRune(b);
            return;
        }

        var cells = _rows[_row].Cells;
        foreach (var b in text)
        {
            if (_wrapPending)
            {
                if (_modes.AutoWrap)
                {
                    Wrap();
                    cells = _rows[_row].Cells;
                }
            }

            ref var cell = ref cells[_col];
            if ((cell.Flags & (CellFlags.Wide | CellFlags.WideTail)) != 0) BreakWide(cells, _col);
            cell.Rune = b;
            cell.Fg = _fg;
            cell.Bg = _bg;
            cell.Flags = _flags;
            cell.Link = _link;
            if (_col == _cols - 1) _wrapPending = true;
            else
            {
                _col++;
                _wrapPending = false;
            }
        }

        _lastPrinted = text[^1];
    }

    /// <summary>Whatever half of a double-width character <paramref name="col"/> belongs to is blanked, so no half is left alone.</summary>
    private void BreakWide(Cell[] cells, int col)
    {
        if ((cells[col].Flags & CellFlags.Wide) != 0 && col + 1 < cells.Length && (cells[col + 1].Flags & CellFlags.WideTail) != 0) cells[col + 1] = EraseCell;
        else if ((cells[col].Flags & CellFlags.WideTail) != 0 && col > 0 && (cells[col - 1].Flags & CellFlags.Wide) != 0) cells[col - 1] = EraseCell;
    }

    private void PutRune(int rune)
    {
        if (_charsets[_glSet] == Charset.DecGraphics && rune is >= 0x5F and <= 0x7E) rune = DecGraphics[rune - 0x5F];
        var width = CharWidth.Of(rune);

        if (_joinNext)
        {
            _joinNext = false;
            AppendToPrevious(rune);
            return;
        }

        if (width == 0 || CharWidth.IsEmojiModifier(rune))
        {
            // A zero-width joiner makes the next character part of this cell too.
            if (rune == 0x200D) _joinNext = true;
            AppendToPrevious(rune);
            return;
        }

        if (CharWidth.IsRegionalIndicator(rune) && PreviousIsLoneRegionalIndicator())
        {
            AppendToPrevious(rune);
            return;
        }

        if (_wrapPending)
        {
            if (_modes.AutoWrap) Wrap();
        }

        var cells = _rows[_row].Cells;
        if (width == 2)
        {
            if (_cols < 2) return;
            if (_col == _cols - 1)
            {
                // No room for both halves: the last cell stays empty and the character goes to the next row.
                if (!_modes.AutoWrap) return;
                BreakWide(cells, _col);
                cells[_col] = EraseCell;
                Wrap();
                cells = _rows[_row].Cells;
            }

            if (_insertMode) InsertCells(2);
            BreakWide(cells, _col);
            BreakWide(cells, _col + 1);
            cells[_col] = new Cell { Rune = rune, Fg = _fg, Bg = _bg, Flags = (_flags & CellFlags.Pen) | CellFlags.Wide, Link = _link };
            cells[_col + 1] = new Cell { Fg = _fg, Bg = _bg, Flags = (_flags & CellFlags.Pen) | CellFlags.WideTail, Link = _link };
            if (_col + 2 >= _cols)
            {
                _col = _cols - 1;
                _wrapPending = true;
            }
            else _col += 2;
        }
        else
        {
            if (_insertMode) InsertCells(1);
            BreakWide(cells, _col);
            cells[_col] = new Cell { Rune = rune, Fg = _fg, Bg = _bg, Flags = _flags & CellFlags.Pen, Link = _link };
            if (_col == _cols - 1) _wrapPending = true;
            else _col++;
        }

        _lastPrinted = rune;
    }

    private int PreviousCellColumn()
    {
        var pos = _wrapPending ? _col : _col - 1;
        if (pos < 0) return -1;
        if ((_rows[_row].Cells[pos].Flags & CellFlags.WideTail) != 0 && pos > 0) pos--;
        return pos;
    }

    private bool PreviousIsLoneRegionalIndicator()
    {
        var pos = PreviousCellColumn();
        if (pos < 0) return false;
        var cell = _rows[_row].Cells[pos];
        return (cell.Flags & CellFlags.Cluster) == 0 && CharWidth.IsRegionalIndicator(cell.Rune);
    }

    /// <summary>A mark, joiner, modifier or selector: drawn in the cell before it.</summary>
    private void AppendToPrevious(int rune)
    {
        var pos = PreviousCellColumn();
        if (pos < 0) return;
        var cells = _rows[_row].Cells;
        ref var cell = ref cells[pos];
        var basis = (cell.Flags & CellFlags.Cluster) != 0 ? Clusters[cell.Rune] : cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune);
        if (basis.Length > 24) return;
        var single = (cell.Flags & CellFlags.Cluster) == 0 ? cell.Rune : 0;

        var joined = Clusters.Add(basis + char.ConvertFromUtf32(rune));
        if (joined < 0) return;
        cell.Rune = joined;
        cell.Flags |= CellFlags.Cluster;

        // A text-style symbol followed by the emoji selector is drawn two cells wide.
        if (rune == 0xFE0F && single != 0 && (cell.Flags & CellFlags.Wide) == 0 && !_wrapPending && pos == _col - 1 && _col < _cols && CharWidth.WidensWithVariationSelector(single))
        {
            BreakWide(cells, _col);
            cell.Flags |= CellFlags.Wide;
            cells[_col] = new Cell { Fg = cell.Fg, Bg = cell.Bg, Flags = (cell.Flags & CellFlags.Pen) | CellFlags.WideTail, Link = cell.Link };
            if (_col + 1 >= _cols) _wrapPending = true;
            else _col++;
        }
    }

    // ── Editing ──────────────────────────────────────────────────────────────────────────────────

    private void InsertCells(int count)
    {
        var cells = _rows[_row].Cells;
        count = Math.Min(count, _cols - _col);
        if (count <= 0) return;
        BreakWide(cells, _col);
        Array.Copy(cells, _col, cells, _col + count, _cols - _col - count);
        for (var i = 0; i < count; i++) cells[_col + i] = EraseCell;
        if ((cells[_cols - 1].Flags & CellFlags.Wide) != 0) cells[_cols - 1] = EraseCell;
    }

    private void DeleteCells(int count)
    {
        var cells = _rows[_row].Cells;
        count = Math.Min(count, _cols - _col);
        if (count <= 0) return;
        BreakWide(cells, _col);
        if (_col + count < _cols) BreakWide(cells, _col + count);
        Array.Copy(cells, _col + count, cells, _col, _cols - _col - count);
        for (var i = _cols - count; i < _cols; i++) cells[i] = EraseCell;
    }

    private void EraseCells(int row, int from, int to)
    {
        var cells = _rows[row].Cells;
        from = Math.Max(0, from);
        to = Math.Min(_cols, to);
        if (from >= to) return;
        BreakWide(cells, from);
        BreakWide(cells, to - 1);
        var erase = EraseCell;
        for (var i = from; i < to; i++) cells[i] = erase;
        if (to == _cols) _rows[row].Wrapped = false;
    }

    private void InsertLines(int count)
    {
        if (_row < _top || _row > _bottom) return;
        ScrollDown(_row, _bottom, count);
        _col = 0;
        _wrapPending = false;
    }

    private void DeleteLines(int count)
    {
        if (_row < _top || _row > _bottom) return;
        ScrollUp(_row, _bottom, count, toScrollback: false);
        _col = 0;
        _wrapPending = false;
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: EraseCells(_row, _col, _cols); break;
            case 1: EraseCells(_row, 0, _col + 1); break;
            case 2:
                EraseCells(_row, 0, _cols);
                break;
        }

        _wrapPending = false;
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                EraseCells(_row, _col, _cols);
                for (var r = _row + 1; r < _rowCount; r++) Clear(_rows[r], EraseCell);
                break;
            case 1:
                for (var r = 0; r < _row; r++) Clear(_rows[r], EraseCell);
                EraseCells(_row, 0, _col + 1);
                break;
            case 2:
                for (var r = 0; r < _rowCount; r++) Clear(_rows[r], EraseCell);
                break;
            case 3:
                ClearScrollback();
                break;
        }

        _wrapPending = false;
    }

    /// <summary>Drops the scrollback (ED 3, and the Clear command): what is on the screen stays.</summary>
    public void ClearScrollback()
    {
        _baseLine += _scrollback.Count;
        _scrollback.Clear();
        _viewportOffset = 0;
        _epoch++;
    }

    private void RepeatLast(int count)
    {
        if (_lastPrinted == 0) return;
        count = Math.Min(count, 65536);
        for (var i = 0; i < count; i++) PutRune(_lastPrinted);
    }

    // ── The viewport ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Scrolls the view <paramref name="lines"/> up (positive) or down; clamped to the scrollback.</summary>
    public void ScrollViewport(int lines) => _viewportOffset = Math.Clamp(_viewportOffset + lines, 0, _alt ? 0 : _scrollback.Count);

    public void ScrollViewportToBottom() => _viewportOffset = 0;

    public void ScrollViewportToTop() => _viewportOffset = _alt ? 0 : _scrollback.Count;

    /// <summary>Scrolls so that absolute line <paramref name="line"/> is the first row shown (or as near as the scrollback allows).</summary>
    public void ScrollViewportToLine(long line)
    {
        var index = line - _baseLine;
        _viewportOffset = Math.Clamp((int)Math.Clamp(_scrollback.Count - index, 0, int.MaxValue), 0, _alt ? 0 : _scrollback.Count);
    }

    // ── Reading lines by absolute number ─────────────────────────────────────────────────────────

    /// <summary>The line with this absolute number, or null when it has left the scrollback or does not exist yet.</summary>
    internal Line? LineAt(long line)
    {
        var index = line - _baseLine;
        if (index < 0) return null;
        if (index < _scrollback.Count) return _scrollback[(int)index];
        var row = index - _scrollback.Count;
        return row < _rowCount ? _screens[0][row] : null;
    }

    /// <summary>The absolute number of the main screen's first row.</summary>
    internal long ScreenTopLine => _baseLine + _scrollback.Count;

    /// <summary>A ring of finished lines, oldest first, that drops the oldest when full.</summary>
    internal sealed class ScrollbackRing
    {
        private Line[] _items;
        private int _head;

        public ScrollbackRing(int capacity)
        {
            Capacity = capacity;
            _items = new Line[Math.Min(capacity, 256)];
        }

        public int Capacity { get; private set; }

        public int Count { get; private set; }

        public Line this[int i] => _items[(_head + i) % _items.Length];

        /// <summary>Adds a line; returns true when the oldest one was dropped to make room.</summary>
        public bool Push(Line line)
        {
            if (Count == Capacity)
            {
                _items[_head] = line;
                _head = (_head + 1) % _items.Length;
                return true;
            }

            if (Count == _items.Length)
            {
                var grown = new Line[Math.Min(Capacity, _items.Length * 2)];
                for (var i = 0; i < Count; i++) grown[i] = this[i];
                _items = grown;
                _head = 0;
            }

            _items[(_head + Count) % _items.Length] = line;
            Count++;
            return false;
        }

        public void Clear()
        {
            Array.Clear(_items);
            _head = 0;
            Count = 0;
        }

        /// <summary>Replaces the contents (after a reflow), keeping the newest lines that fit.</summary>
        public int Replace(List<Line> lines)
        {
            var skip = Math.Max(0, lines.Count - Capacity);
            var keep = lines.Count - skip;
            _items = new Line[Math.Max(1, Math.Min(Capacity, Math.Max(keep, 256)))];
            for (var i = 0; i < keep; i++) _items[i] = lines[skip + i];
            _head = 0;
            Count = keep;
            return skip;
        }
    }
}

/// <summary>The 256-colour palette programs address by number.</summary>
public static class Palette
{
    /// <summary>The standard xterm colours (0xAARRGGBB); the first 16 replaced by <paramref name="ansi"/> when given.</summary>
    public static uint[] Build(uint[]? ansi)
    {
        var p = new uint[256];
        uint[] basic =
        [
            0xFF000000, 0xFFCD3131, 0xFF0DBC79, 0xFFE5E510, 0xFF2472C8, 0xFFBC3FBC, 0xFF11A8CD, 0xFFE5E5E5,
            0xFF666666, 0xFFF14C4C, 0xFF23D18B, 0xFFF5F543, 0xFF3B8EEA, 0xFFD670D6, 0xFF29B8DB, 0xFFFFFFFF,
        ];
        for (var i = 0; i < 16; i++) p[i] = ansi is { Length: 16 } ? ansi[i] : basic[i];
        int[] levels = [0, 95, 135, 175, 215, 255];
        for (var i = 0; i < 216; i++) p[16 + i] = 0xFF000000 | (uint)(levels[i / 36] << 16) | (uint)(levels[i / 6 % 6] << 8) | (uint)levels[i % 6];
        for (var i = 0; i < 24; i++)
        {
            var v = (uint)(8 + i * 10);
            p[232 + i] = 0xFF000000 | (v << 16) | (v << 8) | v;
        }

        return p;
    }
}
