using Avalonia.Input;
using Wicked.Core.Terminal;

namespace Wicked.App.Terminal;

/// <summary>Avalonia's keys as the terminal's.</summary>
public static class TerminalKeys
{
    public static TermMods Mods(KeyModifiers m)
    {
        var mods = TermMods.None;
        if ((m & KeyModifiers.Shift) != 0) mods |= TermMods.Shift;
        if ((m & KeyModifiers.Alt) != 0) mods |= TermMods.Alt;
        if ((m & KeyModifiers.Control) != 0) mods |= TermMods.Ctrl;
        if ((m & KeyModifiers.Meta) != 0) mods |= TermMods.Meta;
        return mods;
    }

    /// <summary>The terminal key for a key that is not a character, or null.</summary>
    public static TermKey? Special(Key key) => key switch
    {
        Key.Enter => TermKey.Enter,
        Key.Tab => TermKey.Tab,
        Key.Back => TermKey.Backspace,
        Key.Escape => TermKey.Escape,
        Key.Up => TermKey.Up,
        Key.Down => TermKey.Down,
        Key.Left => TermKey.Left,
        Key.Right => TermKey.Right,
        Key.Home => TermKey.Home,
        Key.End => TermKey.End,
        Key.PageUp => TermKey.PageUp,
        Key.PageDown => TermKey.PageDown,
        Key.Insert => TermKey.Insert,
        Key.Delete => TermKey.Delete,
        >= Key.F1 and <= Key.F12 => TermKey.F1 + (key - Key.F1),
        >= Key.F13 and <= Key.F20 => TermKey.F13 + (key - Key.F13),
        _ => null,
    };

    /// <summary>The keypad key, or null. These only differ from the main keys while the program has asked for application keypad mode.</summary>
    public static TermKey? Keypad(Key key) => key switch
    {
        >= Key.NumPad0 and <= Key.NumPad9 => TermKey.Keypad0 + (key - Key.NumPad0),
        Key.Add => TermKey.KeypadAdd,
        Key.Subtract => TermKey.KeypadSubtract,
        Key.Multiply => TermKey.KeypadMultiply,
        Key.Divide => TermKey.KeypadDivide,
        Key.Decimal => TermKey.KeypadDecimal,
        _ => null,
    };

    /// <summary>The character a key stands for in a Ctrl combination (Ctrl+C, Ctrl+], Ctrl+Shift+-), or null when Ctrl does nothing with it.</summary>
    public static char? ControlCharacter(Key key, bool shift)
    {
        if (key is >= Key.A and <= Key.Z) return (char)('a' + (key - Key.A));
        return key switch
        {
            Key.Space => ' ',
            Key.D2 => shift ? '@' : '2',
            Key.D3 => '3',
            Key.D4 => '4',
            Key.D5 => '5',
            Key.D6 => shift ? '^' : '6',
            Key.D7 => '7',
            Key.D8 => '8',
            Key.OemOpenBrackets => '[',
            Key.OemBackslash or Key.Oem5 => '\\',
            Key.OemCloseBrackets => ']',
            Key.OemMinus => shift ? '_' : '-',
            Key.OemQuestion => shift ? '?' : '/',
            Key.OemTilde => shift ? '~' : '`',
            _ => null,
        };
    }
}
