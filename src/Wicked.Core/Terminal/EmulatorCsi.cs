namespace Wicked.Core.Terminal;

// What the escape sequences do.
public sealed partial class TerminalEmulator
{
    /// <summary>Parameter <paramref name="i"/>, or <paramref name="fallback"/> when it was left out.</summary>
    private int P(int i, int fallback) => i < _paramCount && _params[i] >= 0 ? _params[i] : fallback;

    /// <summary>Like <see cref="P"/> for a count: 0 means 1 too.</summary>
    private int Count(int i) => Math.Max(1, P(i, 1));

    private void EscDispatch(char final, char intermediate)
    {
        if (intermediate != '\0')
        {
            switch (intermediate)
            {
                case '(':
                case ')':
                case '*':
                case '+':
                    var set = intermediate == '(' ? 0 : intermediate == ')' ? 1 : intermediate == '*' ? 2 : 3;
                    _charsets[set] = final == '0' ? Charset.DecGraphics : Charset.Ascii;
                    break;
                case '#':
                    if (final == '8')
                    {
                        // DECALN: the alignment pattern.
                        var cell = new Cell { Rune = 'E' };
                        foreach (var line in _rows) Array.Fill(line.Cells, cell);
                        MoveTo(0, 0);
                    }

                    break;
            }

            return;
        }

        switch (final)
        {
            case '7': SaveCursor(); break;
            case '8': RestoreCursor(); break;
            case 'D': Index(); break;
            case 'E':
                Index();
                _col = 0;
                break;
            case 'M': ReverseIndex(); break;
            case 'H':
                if (_col < _tabs.Length) _tabs[_col] = true;
                break;
            case 'c': Reset(); break;
            case '=': SetModes(_modes with { ApplicationKeypad = true }); break;
            case '>': SetModes(_modes with { ApplicationKeypad = false }); break;
            case 'n': _glSet = 2; break;
            case 'o': _glSet = 3; break;
        }
    }

    private void CsiDispatch(char final)
    {
        if (_csiPrefix == '?')
        {
            if (_intermediate == '$' && final == 'p') ReportMode(P(0, 0), dec: true);
            else if (final is 'h' or 'l') SetPrivateModes(final == 'h');
            else if (final == 'n') DeviceStatus(dec: true);
            else if (final == 'J') EraseInDisplay(P(0, 0));
            else if (final == 'K') EraseInLine(P(0, 0));
            return;
        }

        if (_csiPrefix == '>')
        {
            if (final == 'c') Respond("\u001b[>0;115;0c");
            else if (final == 'q') Respond($"\u001bP>|{_options.Name}\u001b\\");
            return;
        }

        if (_csiPrefix != '\0') return;

        if (_intermediate != '\0')
        {
            switch (_intermediate, final)
            {
                case (' ', 'q'): SetCursorStyle(P(0, 0)); break;
                case ('!', 'p'): SoftReset(); break;
                case ('$', 'p'): ReportMode(P(0, 0), dec: false); break;
            }

            return;
        }

        switch (final)
        {
            case '@': InsertCells(Count(0)); break;
            case 'A': MoveTo(Math.Max(_row >= _top ? _top : 0, _row - Count(0)), _col); break;
            case 'B':
            case 'e':
                MoveTo(Math.Min(_row <= _bottom ? _bottom : _rowCount - 1, _row + Count(0)), _col);
                break;
            case 'C':
            case 'a':
                MoveTo(_row, _col + Count(0));
                break;
            case 'D': MoveTo(_row, _col - Count(0)); break;
            case 'E': MoveTo(Math.Min(_row <= _bottom ? _bottom : _rowCount - 1, _row + Count(0)), 0); break;
            case 'F': MoveTo(Math.Max(_row >= _top ? _top : 0, _row - Count(0)), 0); break;
            case 'G':
            case '`':
                MoveTo(_row, Count(0) - 1);
                break;
            case 'H':
            case 'f':
                MoveTo((_originMode ? _top : 0) + Count(0) - 1, Count(1) - 1);
                break;
            case 'I': Tab(Count(0)); break;
            case 'J': EraseInDisplay(P(0, 0)); break;
            case 'K': EraseInLine(P(0, 0)); break;
            case 'L': InsertLines(Count(0)); break;
            case 'M': DeleteLines(Count(0)); break;
            case 'P': DeleteCells(Count(0)); break;
            case 'S': ScrollUp(_top, _bottom, Count(0), toScrollback: _top == 0 && !_alt); break;
            case 'T': ScrollDown(_top, _bottom, Count(0)); break;
            case 'X': EraseCells(_row, _col, _col + Count(0)); break;
            case 'Z': BackTab(Count(0)); break;
            case 'b': RepeatLast(Count(0)); break;
            case 'c':
                if (P(0, 0) == 0) Respond("\u001b[?62;22c");
                break;
            case 'd': MoveTo((_originMode ? _top : 0) + Count(0) - 1, _col); break;
            case 'g':
                if (P(0, 0) == 0) { if (_col < _tabs.Length) _tabs[_col] = false; }
                else if (P(0, 0) == 3) Array.Clear(_tabs);
                break;
            case 'h': SetAnsiModes(true); break;
            case 'l': SetAnsiModes(false); break;
            case 'm': Sgr(); break;
            case 'n': DeviceStatus(dec: false); break;
            case 'r': SetScrollRegion(); break;
            case 's': SaveCursor(); break;
            case 'u': RestoreCursor(); break;
            case 't': WindowOps(); break;
        }
    }

    private void SetScrollRegion()
    {
        var top = Count(0) - 1;
        var bottom = P(1, _rowCount) - 1;
        if (bottom <= top || top >= _rowCount) return;
        _top = top;
        _bottom = Math.Min(bottom, _rowCount - 1);
        MoveTo(_originMode ? _top : 0, 0);
    }

    private void SoftReset()
    {
        _insertMode = false;
        _originMode = false;
        _top = 0;
        _bottom = _rowCount - 1;
        ResetPen();
        _charsets[0] = _charsets[1] = _charsets[2] = _charsets[3] = Charset.Ascii;
        _glSet = 0;
        _wrapPending = false;
        _saved[0] = default;
        _saved[1] = default;
        SetModes(_modes with { ApplicationCursor = false, ApplicationKeypad = false, AutoWrap = true, CursorVisible = true, NewLineMode = false });
    }

    private void SetCursorStyle(int style)
    {
        switch (style)
        {
            case 0: SetModes(_modes with { CursorShape = null, CursorBlink = null }); break;
            case 1: SetModes(_modes with { CursorShape = CursorShape.Block, CursorBlink = true }); break;
            case 2: SetModes(_modes with { CursorShape = CursorShape.Block, CursorBlink = false }); break;
            case 3: SetModes(_modes with { CursorShape = CursorShape.Underline, CursorBlink = true }); break;
            case 4: SetModes(_modes with { CursorShape = CursorShape.Underline, CursorBlink = false }); break;
            case 5: SetModes(_modes with { CursorShape = CursorShape.Bar, CursorBlink = true }); break;
            case 6: SetModes(_modes with { CursorShape = CursorShape.Bar, CursorBlink = false }); break;
        }
    }

    private void SetAnsiModes(bool on)
    {
        for (var i = 0; i < _paramCount; i++)
        {
            switch (_params[i])
            {
                case 4: _insertMode = on; break;
                case 20: SetModes(_modes with { NewLineMode = on }); break;
            }
        }
    }

    private void SetPrivateModes(bool on)
    {
        for (var i = 0; i < _paramCount; i++)
        {
            switch (_params[i])
            {
                case 1: SetModes(_modes with { ApplicationCursor = on }); break;
                case 5: SetModes(_modes with { ReverseVideo = on }); break;
                case 6:
                    _originMode = on;
                    MoveTo(on ? _top : 0, 0);
                    break;
                case 7: SetModes(_modes with { AutoWrap = on }); break;
                case 9: SetModes(_modes with { Mouse = on ? MouseTracking.Buttons : MouseTracking.None }); break;
                case 12: SetModes(_modes with { CursorBlink = on }); break;
                case 25: SetModes(_modes with { CursorVisible = on }); break;
                case 47:
                case 1047:
                    if (on) EnterAlternate(saveCursor: false, clear: _params[i] == 1047);
                    else
                    {
                        if (_params[i] == 1047 && _alt) foreach (var line in _rows) Clear(line, new Cell());
                        LeaveAlternate(restoreCursor: false);
                    }

                    break;
                case 66: SetModes(_modes with { ApplicationKeypad = on }); break;
                case 1000: SetModes(_modes with { Mouse = on ? MouseTracking.Buttons : MouseTracking.None }); break;
                case 1002: SetModes(_modes with { Mouse = on ? MouseTracking.Drag : MouseTracking.None }); break;
                case 1003: SetModes(_modes with { Mouse = on ? MouseTracking.Any : MouseTracking.None }); break;
                case 1004: SetModes(_modes with { FocusReporting = on }); break;
                case 1005: SetModes(_modes with { MouseEncoding = on ? MouseEncoding.Utf8 : MouseEncoding.X10 }); break;
                case 1006: SetModes(_modes with { MouseEncoding = on ? MouseEncoding.Sgr : MouseEncoding.X10 }); break;
                case 1007: SetModes(_modes with { AlternateScroll = on }); break;
                case 1015: SetModes(_modes with { MouseEncoding = on ? MouseEncoding.Urxvt : MouseEncoding.X10 }); break;
                case 1048:
                    if (on) SaveCursor();
                    else RestoreCursor();
                    break;
                case 1049:
                    if (on) EnterAlternate(saveCursor: true, clear: true);
                    else LeaveAlternate(restoreCursor: true);
                    break;
                case 2004: SetModes(_modes with { BracketedPaste = on }); break;
                case 2026: SetModes(_modes with { SynchronizedOutput = on }); break;
            }
        }
    }

    /// <summary>DECRQM: whether a mode is set (1), reset (2) or not known (0).</summary>
    private void ReportMode(int mode, bool dec)
    {
        int state;
        if (dec)
        {
            var m = _modes;
            bool? on = mode switch
            {
                1 => m.ApplicationCursor,
                5 => m.ReverseVideo,
                6 => _originMode,
                7 => m.AutoWrap,
                12 => m.CursorBlink ?? false,
                25 => m.CursorVisible,
                47 or 1047 or 1049 => m.AlternateScreen,
                66 => m.ApplicationKeypad,
                1000 => m.Mouse == MouseTracking.Buttons,
                1002 => m.Mouse == MouseTracking.Drag,
                1003 => m.Mouse == MouseTracking.Any,
                1004 => m.FocusReporting,
                1006 => m.MouseEncoding == MouseEncoding.Sgr,
                1007 => m.AlternateScroll,
                2004 => m.BracketedPaste,
                2026 => m.SynchronizedOutput,
                _ => null,
            };
            state = on is null ? 0 : on.Value ? 1 : 2;
            Respond($"\u001b[?{mode};{state}$y");
        }
        else
        {
            state = mode switch { 4 => _insertMode ? 1 : 2, 20 => _modes.NewLineMode ? 1 : 2, _ => 0 };
            Respond($"\u001b[{mode};{state}$y");
        }
    }

    private void DeviceStatus(bool dec)
    {
        switch (P(0, 0))
        {
            case 5: Respond("\u001b[0n"); break;
            case 6:
                var row = _row - (_originMode ? _top : 0) + 1;
                Respond(dec ? $"\u001b[?{row};{_col + 1}R" : $"\u001b[{row};{_col + 1}R");
                break;
        }
    }

    private void WindowOps()
    {
        switch (P(0, 0))
        {
            case 18:
            case 19:
                Respond($"\u001b[{(P(0, 0) == 18 ? 8 : 9)};{_rowCount};{_cols}t");
                break;
        }
    }

    // ── SGR ──────────────────────────────────────────────────────────────────────────────────────

    private void Sgr()
    {
        if (_paramCount == 0)
        {
            ResetPen();
            return;
        }

        for (var i = 0; i < _paramCount; i++)
        {
            var p = _params[i] < 0 ? 0 : _params[i];
            switch (p)
            {
                case 0: ResetPen(); break;
                case 1: _flags |= CellFlags.Bold; break;
                case 2: _flags |= CellFlags.Dim; break;
                case 3: _flags |= CellFlags.Italic; break;
                case 4:
                    var style = i + 1 < _paramCount && _paramIsSub[i + 1] ? _params[++i] : 1;
                    _flags &= ~CellFlags.AnyUnderline;
                    _flags |= style switch
                    {
                        0 => CellFlags.None,
                        2 => CellFlags.DoubleUnderline,
                        3 => CellFlags.CurlyUnderline,
                        4 => CellFlags.DottedUnderline,
                        5 => CellFlags.DashedUnderline,
                        _ => CellFlags.Underline,
                    };
                    break;
                case 5:
                case 6:
                    _flags |= CellFlags.Blink;
                    break;
                case 7: _flags |= CellFlags.Inverse; break;
                case 8: _flags |= CellFlags.Invisible; break;
                case 9: _flags |= CellFlags.Strike; break;
                case 21: _flags = (_flags & ~CellFlags.AnyUnderline) | CellFlags.DoubleUnderline; break;
                case 22: _flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: _flags &= ~CellFlags.Italic; break;
                case 24: _flags &= ~CellFlags.AnyUnderline; break;
                case 25: _flags &= ~CellFlags.Blink; break;
                case 27: _flags &= ~CellFlags.Inverse; break;
                case 28: _flags &= ~CellFlags.Invisible; break;
                case 29: _flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37: _fg = CellColor.Palette(p - 30); break;
                case 38: ExtendedColor(ref i, ref _fg); break;
                case 39: _fg = 0; break;
                case >= 40 and <= 47: _bg = CellColor.Palette(p - 40); break;
                case 48: ExtendedColor(ref i, ref _bg); break;
                case 49: _bg = 0; break;
                case 58: UnderlineColor(ref i); break;
                case >= 90 and <= 97: _fg = CellColor.Palette(p - 90 + 8); break;
                case >= 100 and <= 107: _bg = CellColor.Palette(p - 100 + 8); break;
            }
        }
    }

    /// <summary>38 / 48 in either form: <c>38;5;n</c>, <c>38;2;r;g;b</c>, or with colons <c>38:5:n</c>, <c>38:2::r:g:b</c>.</summary>
    private void ExtendedColor(ref int i, ref uint target)
    {
        if (i + 1 >= _paramCount) return;
        if (_paramIsSub[i + 1])
        {
            // The colon form: everything joined to the 38 by colons belongs to it.
            var kind = i + 1;
            var end = kind;
            while (end + 1 < _paramCount && _paramIsSub[end + 1]) end++;
            if (_params[kind] == 5 && end >= kind + 1) target = CellColor.Palette(Math.Clamp(_params[end], 0, 255));
            else if (_params[kind] == 2 && end - kind >= 3) target = CellColor.Rgb(Channel(end - 2), Channel(end - 1), Channel(end));
            i = end;
            return;
        }

        var type = _params[i + 1];
        if (type == 5)
        {
            if (i + 2 < _paramCount) target = CellColor.Palette(Math.Clamp(_params[i + 2], 0, 255));
            i = Math.Min(i + 2, _paramCount);
        }
        else if (type == 2)
        {
            if (i + 4 < _paramCount) target = CellColor.Rgb(Channel(i + 2), Channel(i + 3), Channel(i + 4));
            i = Math.Min(i + 4, _paramCount);
        }
        else i++;
    }

    private byte Channel(int index) => (byte)Math.Clamp(_params[index], 0, 255);

    private void UnderlineColor(ref int i)
    {
        // Read so the parameters after it are not mistaken for something else; the colour itself is not kept.
        if (i + 1 >= _paramCount) return;
        if (_paramIsSub[i + 1])
        {
            while (i + 1 < _paramCount && _paramIsSub[i + 1]) i++;
            return;
        }

        i = Math.Min(i + (_params[i + 1] == 5 ? 2 : _params[i + 1] == 2 ? 4 : 1), _paramCount);
    }
}
