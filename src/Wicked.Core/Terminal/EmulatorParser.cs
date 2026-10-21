using System.Text;

namespace Wicked.Core.Terminal;

// The input side: Paul Williams' DEC-compatible state machine, with UTF-8 decoding in the ground state.
public sealed partial class TerminalEmulator
{
    private const int MaxParams = 32;
    private const int MaxStringBytes = 1 << 20;

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        CsiEntry,
        CsiParam,
        CsiIntermediate,
        CsiIgnore,
        DcsEntry,
        DcsParam,
        DcsIntermediate,
        DcsPassthrough,
        DcsIgnore,
        OscString,
        SosPmApc,
    }

    private State _state = State.Ground;
    private readonly int[] _params = new int[MaxParams];
    private readonly bool[] _paramIsSub = new bool[MaxParams];
    private int _paramCount;
    private bool _paramStarted;
    private int _paramValue;
    private char _csiPrefix;
    private char _intermediate;
    private char _intermediate2;
    private byte[] _string = new byte[256];
    private int _stringLength;
    private bool _stringEscape;
    private int _utf8Rune;
    private int _utf8Need;
    private int _utf8Min;

    /// <summary>Feeds the program's output to the terminal.</summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        var i = 0;
        var n = data.Length;
        while (i < n)
        {
            if (_state == State.Ground && _utf8Need == 0)
            {
                var start = i;
                while (i < n && data[i] >= 0x20 && data[i] < 0x7F) i++;
                if (i > start)
                {
                    PrintAscii(data.Slice(start, i - start));
                    continue;
                }
            }

            Step(data[i++]);
        }
    }

    private void Step(byte b)
    {
        // These work in every state, and a sequence they interrupt is abandoned.
        switch (b)
        {
            case 0x18:
            case 0x1A:
                _state = State.Ground;
                _utf8Need = 0;
                return;
            case 0x1B:
                if (_state is State.OscString or State.DcsPassthrough or State.SosPmApc or State.DcsIgnore)
                {
                    // ESC inside a string: the terminator ESC \ follows; anything else ends the string and starts a new sequence.
                    _stringEscape = true;
                    _state = State.Escape;
                    return;
                }

                _utf8Need = 0;
                _state = State.Escape;
                ClearSequence();
                return;
        }

        switch (_state)
        {
            case State.Ground: Ground(b); break;
            case State.Escape: Escape(b); break;
            case State.EscapeIntermediate: EscapeIntermediate(b); break;
            case State.CsiEntry:
            case State.CsiParam:
            case State.CsiIntermediate:
            case State.CsiIgnore:
                Csi(b);
                break;
            case State.DcsEntry:
            case State.DcsParam:
            case State.DcsIntermediate:
                Dcs(b);
                break;
            case State.DcsPassthrough:
            case State.DcsIgnore:
            case State.OscString:
            case State.SosPmApc:
                StringByte(b);
                break;
        }
    }

    private void ClearSequence()
    {
        _paramCount = 0;
        _paramStarted = false;
        _paramValue = 0;
        _csiPrefix = '\0';
        _intermediate = '\0';
        _intermediate2 = '\0';
        Array.Clear(_paramIsSub);
    }

    private void Ground(byte b)
    {
        if (b >= 0x80)
        {
            Utf8(b);
            return;
        }

        if (_utf8Need > 0)
        {
            // A sequence cut short by a control or an ASCII character.
            _utf8Need = 0;
            PutRune(0xFFFD);
        }

        if (b < 0x20) Control(b);
        else if (b < 0x7F) PutRune(b);
    }

    private void Utf8(byte b)
    {
        if (_utf8Need > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8Rune = (_utf8Rune << 6) | (b & 0x3F);
                if (--_utf8Need == 0)
                {
                    var r = _utf8Rune;
                    // Overlong forms, surrogates and values past U+10FFFF are errors.
                    if (r < _utf8Min || r > 0x10FFFF || r is >= 0xD800 and <= 0xDFFF) r = 0xFFFD;
                    PutRune(r);
                }

                return;
            }

            _utf8Need = 0;
            PutRune(0xFFFD);
        }

        if (b >= 0xC2 && b <= 0xDF)
        {
            _utf8Rune = b & 0x1F;
            _utf8Need = 1;
            _utf8Min = 0x80;
        }
        else if (b >= 0xE0 && b <= 0xEF)
        {
            _utf8Rune = b & 0x0F;
            _utf8Need = 2;
            _utf8Min = 0x800;
        }
        else if (b >= 0xF0 && b <= 0xF4)
        {
            _utf8Rune = b & 0x07;
            _utf8Need = 3;
            _utf8Min = 0x10000;
        }
        else PutRune(0xFFFD);
    }

    private void Control(byte b)
    {
        switch (b)
        {
            case 0x07: _bellCount++; Bell?.Invoke(); break;
            case 0x08:
                if (_col > 0) _col--;
                _wrapPending = false;
                break;
            case 0x09: Tab(1); break;
            case 0x0A:
            case 0x0B:
            case 0x0C:
                LineFeed();
                break;
            case 0x0D: CarriageReturn(); break;
            case 0x0E: _glSet = 1; break;
            case 0x0F: _glSet = 0; break;
        }
    }

    private void Escape(byte b)
    {
        if (_stringEscape)
        {
            _stringEscape = false;
            // ESC \ ends a string; the string state it interrupted is dispatched by the terminator.
            if (b == (byte)'\\')
            {
                FinishString();
                _state = State.Ground;
                return;
            }

            // Anything else after ESC inside a string: the string is cut and this starts a fresh escape.
            _stringLength = 0;
            ClearSequence();
        }

        if (b < 0x20)
        {
            Control(b);
            return;
        }

        switch ((char)b)
        {
            case '[':
                _state = State.CsiEntry;
                ClearSequence();
                return;
            case ']':
                _state = State.OscString;
                _stringKind = StringKind.Osc;
                _stringLength = 0;
                _stringOverflow = false;
                return;
            case 'P':
                _state = State.DcsEntry;
                ClearSequence();
                _stringLength = 0;
                return;
            case 'X':
            case '^':
            case '_':
                _state = State.SosPmApc;
                _stringKind = StringKind.None;
                _stringLength = 0;
                return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediate = (char)b;
            _state = State.EscapeIntermediate;
            return;
        }

        EscDispatch((char)b, '\0');
        _state = State.Ground;
    }

    private void EscapeIntermediate(byte b)
    {
        if (b < 0x20)
        {
            Control(b);
            return;
        }

        if (b <= 0x2F)
        {
            _intermediate2 = (char)b;
            return;
        }

        EscDispatch((char)b, _intermediate);
        _state = State.Ground;
    }

    private void Csi(byte b)
    {
        if (b < 0x20)
        {
            Control(b);
            return;
        }

        if (b == 0x7F) return;

        if (_state == State.CsiIgnore)
        {
            if (b is >= 0x40 and <= 0x7E) _state = State.Ground;
            return;
        }

        if (b is >= 0x30 and <= 0x3F)
        {
            if (_state == State.CsiIntermediate)
            {
                _state = State.CsiIgnore;
                return;
            }

            if (b <= 0x39)
            {
                _paramStarted = true;
                _paramValue = Math.Min(_paramValue * 10 + (b - '0'), 1_000_000);
                _state = State.CsiParam;
            }
            else if (b is (byte)';' or (byte)':')
            {
                PushParam();
                _state = State.CsiParam;
                if (_paramCount >= MaxParams) _state = State.CsiIgnore;
                else _paramIsSub[_paramCount] = b == (byte)':' && _paramCount > 0;
            }
            else
            {
                // <, =, > and ? only start a sequence's parameters.
                if (_state == State.CsiEntry) _csiPrefix = (char)b;
                else
                {
                    _state = State.CsiIgnore;
                    return;
                }

                _state = State.CsiParam;
            }

            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            if (_intermediate == '\0') _intermediate = (char)b;
            else _intermediate2 = (char)b;
            _state = State.CsiIntermediate;
            return;
        }

        // A final byte.
        if (_paramStarted || _paramCount > 0) PushParam();
        CsiDispatch((char)b);
        _state = State.Ground;
    }

    /// <summary>Closes the parameter being read (an omitted one counts as -1, "use the default").</summary>
    private void PushParam()
    {
        if (_paramCount >= MaxParams) return;
        _params[_paramCount++] = _paramStarted ? _paramValue : -1;
        _paramStarted = false;
        _paramValue = 0;
    }

    private void Dcs(byte b)
    {
        if (b < 0x20) return;
        if (b is >= 0x30 and <= 0x3F)
        {
            if (b <= 0x39)
            {
                _paramStarted = true;
                _paramValue = Math.Min(_paramValue * 10 + (b - '0'), 1_000_000);
            }
            else if (b == (byte)';') PushParam();
            else if (_state == State.DcsEntry) _csiPrefix = (char)b;
            _state = State.DcsParam;
            return;
        }

        if (b is >= 0x20 and <= 0x2F)
        {
            _intermediate = _intermediate == '\0' ? (char)b : _intermediate;
            _state = State.DcsIntermediate;
            return;
        }

        if (_paramStarted) PushParam();
        _dcsFinal = (char)b;
        _stringKind = StringKind.Dcs;
        _state = State.DcsPassthrough;
        _stringLength = 0;
    }

    private char _dcsFinal;

    private void StringByte(byte b)
    {
        if (_state == State.OscString && b == 0x07)
        {
            FinishString();
            _state = State.Ground;
            return;
        }

        if (_stringLength >= MaxStringBytes)
        {
            // Too long to be meant: it is read to its end and thrown away.
            _stringOverflow = true;
            return;
        }

        if (_state == State.OscString || _state == State.DcsPassthrough)
        {
            if (_stringLength == _string.Length) Array.Resize(ref _string, _string.Length * 2);
            _string[_stringLength++] = b;
        }
    }

    private void FinishString()
    {
        var overflow = _stringOverflow;
        _stringOverflow = false;
        if (overflow)
        {
            _stringLength = 0;
            _stringKind = StringKind.None;
            return;
        }

        if (_stringKind == StringKind.Osc) DispatchOsc(Encoding.UTF8.GetString(_string, 0, _stringLength));
        else if (_stringKind == StringKind.Dcs) DispatchDcs();
        _stringLength = 0;
        _stringKind = StringKind.None;
    }

    private enum StringKind
    {
        None,
        Osc,
        Dcs,
    }

    private StringKind _stringKind;
    private bool _stringOverflow;
}
