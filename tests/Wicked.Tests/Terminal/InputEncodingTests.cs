using System.Text;
using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class InputEncodingTests
{
    private static string Keys(TermKey key, char c = '\0', TermMods mods = TermMods.None, TerminalModes? modes = null)
    {
        var bytes = KeyEncoder.Encode(key, c, mods, modes ?? new TerminalModes());
        return bytes is null ? "<none>" : Encoding.Latin1.GetString(bytes).Replace("\e", "ESC", StringComparison.Ordinal);
    }

    public static TheoryData<TermKey, char, TermMods, bool, bool, string> Table => new()
    {
        { TermKey.Enter, '\0', TermMods.None, false, false, "\r" },
        { TermKey.Enter, '\0', TermMods.Alt, false, false, "ESC\r" },
        { TermKey.Tab, '\0', TermMods.None, false, false, "\t" },
        { TermKey.Tab, '\0', TermMods.Shift, false, false, "ESC[Z" },
        { TermKey.Backspace, '\0', TermMods.None, false, false, "\u007f" },
        { TermKey.Backspace, '\0', TermMods.Ctrl, false, false, "\b" },
        { TermKey.Backspace, '\0', TermMods.Alt, false, false, "ESC\u007f" },
        { TermKey.Escape, '\0', TermMods.None, false, false, "ESC" },
        { TermKey.Up, '\0', TermMods.None, false, false, "ESC[A" },
        { TermKey.Down, '\0', TermMods.None, false, false, "ESC[B" },
        { TermKey.Right, '\0', TermMods.None, false, false, "ESC[C" },
        { TermKey.Left, '\0', TermMods.None, false, false, "ESC[D" },
        { TermKey.Up, '\0', TermMods.None, true, false, "ESCOA" },
        { TermKey.Left, '\0', TermMods.None, true, false, "ESCOD" },
        { TermKey.Up, '\0', TermMods.Shift, false, false, "ESC[1;2A" },
        { TermKey.Right, '\0', TermMods.Alt, false, false, "ESC[1;3C" },
        { TermKey.Left, '\0', TermMods.Ctrl, false, false, "ESC[1;5D" },
        { TermKey.Left, '\0', TermMods.Ctrl | TermMods.Shift, false, false, "ESC[1;6D" },
        { TermKey.Up, '\0', TermMods.Ctrl, true, false, "ESC[1;5A" },
        { TermKey.Home, '\0', TermMods.None, false, false, "ESC[H" },
        { TermKey.End, '\0', TermMods.None, false, false, "ESC[F" },
        { TermKey.Home, '\0', TermMods.None, true, false, "ESCOH" },
        { TermKey.End, '\0', TermMods.Ctrl, false, false, "ESC[1;5F" },
        { TermKey.PageUp, '\0', TermMods.None, false, false, "ESC[5~" },
        { TermKey.PageDown, '\0', TermMods.None, false, false, "ESC[6~" },
        { TermKey.Insert, '\0', TermMods.None, false, false, "ESC[2~" },
        { TermKey.Delete, '\0', TermMods.None, false, false, "ESC[3~" },
        { TermKey.Delete, '\0', TermMods.Ctrl, false, false, "ESC[3;5~" },
        { TermKey.PageUp, '\0', TermMods.Shift, false, false, "ESC[5;2~" },
        { TermKey.F1, '\0', TermMods.None, false, false, "ESCOP" },
        { TermKey.F2, '\0', TermMods.None, false, false, "ESCOQ" },
        { TermKey.F3, '\0', TermMods.None, false, false, "ESCOR" },
        { TermKey.F4, '\0', TermMods.None, false, false, "ESCOS" },
        { TermKey.F1, '\0', TermMods.Shift, false, false, "ESC[1;2P" },
        { TermKey.F4, '\0', TermMods.Ctrl, false, false, "ESC[1;5S" },
        { TermKey.F5, '\0', TermMods.None, false, false, "ESC[15~" },
        { TermKey.F6, '\0', TermMods.None, false, false, "ESC[17~" },
        { TermKey.F7, '\0', TermMods.None, false, false, "ESC[18~" },
        { TermKey.F8, '\0', TermMods.None, false, false, "ESC[19~" },
        { TermKey.F9, '\0', TermMods.None, false, false, "ESC[20~" },
        { TermKey.F10, '\0', TermMods.None, false, false, "ESC[21~" },
        { TermKey.F11, '\0', TermMods.None, false, false, "ESC[23~" },
        { TermKey.F12, '\0', TermMods.None, false, false, "ESC[24~" },
        { TermKey.F5, '\0', TermMods.Shift, false, false, "ESC[15;2~" },
        { TermKey.F12, '\0', TermMods.Ctrl | TermMods.Alt, false, false, "ESC[24;7~" },
        { TermKey.F13, '\0', TermMods.None, false, false, "ESC[25~" },
        { TermKey.F20, '\0', TermMods.None, false, false, "ESC[34~" },
        { TermKey.Character, 'a', TermMods.Ctrl, false, false, "\u0001" },
        { TermKey.Character, 'c', TermMods.Ctrl, false, false, "\u0003" },
        { TermKey.Character, 'd', TermMods.Ctrl, false, false, "\u0004" },
        { TermKey.Character, 'z', TermMods.Ctrl, false, false, "\u001a" },
        { TermKey.Character, 'C', TermMods.Ctrl | TermMods.Shift, false, false, "\u0003" },
        { TermKey.Character, ' ', TermMods.Ctrl, false, false, "\0" },
        { TermKey.Character, '@', TermMods.Ctrl, false, false, "\0" },
        { TermKey.Character, '[', TermMods.Ctrl, false, false, "ESC" },
        { TermKey.Character, '\\', TermMods.Ctrl, false, false, "\u001c" },
        { TermKey.Character, ']', TermMods.Ctrl, false, false, "\u001d" },
        { TermKey.Character, '^', TermMods.Ctrl, false, false, "\u001e" },
        { TermKey.Character, '_', TermMods.Ctrl, false, false, "\u001f" },
        { TermKey.Character, '/', TermMods.Ctrl, false, false, "\u001f" },
        { TermKey.Character, '?', TermMods.Ctrl, false, false, "\u007f" },
        { TermKey.Character, '2', TermMods.Ctrl, false, false, "\0" },
        { TermKey.Character, 'x', TermMods.Alt, false, false, "ESCx" },
        { TermKey.Character, 'X', TermMods.Alt | TermMods.Shift, false, false, "ESCX" },
        { TermKey.Character, '.', TermMods.Alt, false, false, "ESC." },
        { TermKey.Character, 'b', TermMods.Ctrl | TermMods.Alt, false, false, "ESC\u0002" },
        { TermKey.Character, 'a', TermMods.None, false, false, "<none>" },
        { TermKey.Character, 'A', TermMods.Shift, false, false, "<none>" },
        { TermKey.Keypad5, '\0', TermMods.None, false, false, "5" },
        { TermKey.Keypad5, '\0', TermMods.None, false, true, "ESCOu" },
        { TermKey.Keypad0, '\0', TermMods.None, false, true, "ESCOp" },
        { TermKey.KeypadAdd, '\0', TermMods.None, false, false, "+" },
        { TermKey.KeypadAdd, '\0', TermMods.None, false, true, "ESCOk" },
        { TermKey.KeypadSubtract, '\0', TermMods.None, false, true, "ESCOm" },
        { TermKey.KeypadMultiply, '\0', TermMods.None, false, true, "ESCOj" },
        { TermKey.KeypadDivide, '\0', TermMods.None, false, true, "ESCOo" },
        { TermKey.KeypadDecimal, '\0', TermMods.None, false, true, "ESCOn" },
        { TermKey.KeypadEnter, '\0', TermMods.None, false, false, "\r" },
        { TermKey.KeypadEnter, '\0', TermMods.None, false, true, "ESCOM" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void EveryKeyXtermSendsIsSentTheSameWay(TermKey key, char c, TermMods mods, bool appCursor, bool appKeypad, string expected) =>
        Assert.Equal(expected, Keys(key, c, mods, new TerminalModes { ApplicationCursor = appCursor, ApplicationKeypad = appKeypad }));

    // ── Mouse ────────────────────────────────────────────────────────────────────────────────────

    private static string Mouse(MouseAction action, int button, TermMods mods, int col, int row, MouseTracking tracking, MouseEncoding encoding)
    {
        var bytes = MouseEncoder.Encode(action, button, mods, col, row, new TerminalModes { Mouse = tracking, MouseEncoding = encoding });
        return bytes is null ? "<none>" : Encoding.Latin1.GetString(bytes).Replace("\e", "ESC", StringComparison.Ordinal);
    }

    public static TheoryData<MouseAction, int, TermMods, int, int, MouseTracking, MouseEncoding, string> MouseTable => new()
    {
        { MouseAction.Press, 0, TermMods.None, 0, 0, MouseTracking.Buttons, MouseEncoding.Sgr, "ESC[<0;1;1M" },
        { MouseAction.Release, 0, TermMods.None, 0, 0, MouseTracking.Buttons, MouseEncoding.Sgr, "ESC[<0;1;1m" },
        { MouseAction.Press, 2, TermMods.None, 9, 4, MouseTracking.Buttons, MouseEncoding.Sgr, "ESC[<2;10;5M" },
        { MouseAction.Press, 1, TermMods.Ctrl | TermMods.Shift, 0, 0, MouseTracking.Buttons, MouseEncoding.Sgr, "ESC[<21;1;1M" },
        { MouseAction.Press, 0, TermMods.Alt, 0, 0, MouseTracking.Buttons, MouseEncoding.Sgr, "ESC[<8;1;1M" },
        { MouseAction.WheelUp, -1, TermMods.None, 4, 4, MouseTracking.Buttons, MouseEncoding.Sgr, "ESC[<64;5;5M" },
        { MouseAction.WheelDown, -1, TermMods.None, 4, 4, MouseTracking.Buttons, MouseEncoding.Sgr, "ESC[<65;5;5M" },
        { MouseAction.Move, 0, TermMods.None, 2, 2, MouseTracking.Drag, MouseEncoding.Sgr, "ESC[<32;3;3M" },
        { MouseAction.Move, -1, TermMods.None, 2, 2, MouseTracking.Drag, MouseEncoding.Sgr, "<none>" },
        { MouseAction.Move, -1, TermMods.None, 2, 2, MouseTracking.Any, MouseEncoding.Sgr, "ESC[<35;3;3M" },
        { MouseAction.Move, 0, TermMods.None, 2, 2, MouseTracking.Buttons, MouseEncoding.Sgr, "<none>" },
        { MouseAction.Press, 0, TermMods.None, 0, 0, MouseTracking.None, MouseEncoding.Sgr, "<none>" },
        { MouseAction.Press, 0, TermMods.None, 0, 0, MouseTracking.Buttons, MouseEncoding.X10, "ESC[M !!" },
        { MouseAction.Release, 0, TermMods.None, 0, 0, MouseTracking.Buttons, MouseEncoding.X10, "ESC[M#!!" },
        { MouseAction.Press, 2, TermMods.None, 9, 4, MouseTracking.Buttons, MouseEncoding.X10, "ESC[M\"*%" },
        { MouseAction.Press, 0, TermMods.None, 300, 0, MouseTracking.Buttons, MouseEncoding.X10, "<none>" },
        { MouseAction.Press, 0, TermMods.None, 0, 0, MouseTracking.Buttons, MouseEncoding.Urxvt, "ESC[32;1;1M" },
        { MouseAction.WheelUp, -1, TermMods.None, 0, 0, MouseTracking.Buttons, MouseEncoding.X10, "ESC[M`!!" },
    };

    [Theory]
    [MemberData(nameof(MouseTable))]
    public void AMouseEventIsEncodedAsTheProgramAsked(MouseAction action, int button, TermMods mods, int col, int row, MouseTracking tracking, MouseEncoding encoding, string expected) =>
        Assert.Equal(expected, Mouse(action, button, mods, col, row, tracking, encoding));

    [Fact]
    public void Utf8MouseCoordinatesReachPastTheSingleByteLimit()
    {
        var bytes = MouseEncoder.Encode(MouseAction.Press, 0, TermMods.None, 299, 0, new TerminalModes { Mouse = MouseTracking.Buttons, MouseEncoding = MouseEncoding.Utf8 })!;
        Assert.Equal("\e[M " + char.ConvertFromUtf32(32 + 300) + "!", Encoding.UTF8.GetString(bytes));
    }
}
