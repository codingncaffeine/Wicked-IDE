using System.Text;

namespace Wicked.Core.Terminal;

/// <summary>Keys that are not just a character.</summary>
public enum TermKey
{
    /// <summary>A key that types a character; the character is given alongside.</summary>
    Character,
    Enter,
    Tab,
    Backspace,
    Escape,
    Up,
    Down,
    Left,
    Right,
    Home,
    End,
    PageUp,
    PageDown,
    Insert,
    Delete,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    F13, F14, F15, F16, F17, F18, F19, F20,
    Keypad0, Keypad1, Keypad2, Keypad3, Keypad4, Keypad5, Keypad6, Keypad7, Keypad8, Keypad9,
    KeypadEnter,
    KeypadAdd,
    KeypadSubtract,
    KeypadMultiply,
    KeypadDivide,
    KeypadDecimal,
    KeypadEquals,
}

[Flags]
public enum TermMods
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Ctrl = 4,
    Meta = 8,
}

/// <summary>What a key press sends to the program, as xterm would send it.</summary>
public static class KeyEncoder
{
    /// <summary>The bytes for a key, or null when it sends nothing (the character is then typed through text input).</summary>
    /// <param name="key">The key.</param>
    /// <param name="character">For <see cref="TermKey.Character"/>, the character the key types with the modifiers applied (Shift included).</param>
    /// <param name="mods">The modifier keys held.</param>
    /// <param name="modes">The program's mode settings (application cursor and keypad keys).</param>
    public static byte[]? Encode(TermKey key, char character, TermMods mods, TerminalModes modes)
    {
        var alt = (mods & (TermMods.Alt | TermMods.Meta)) != 0;
        var ctrl = (mods & TermMods.Ctrl) != 0;
        var shift = (mods & TermMods.Shift) != 0;
        var modifier = 1 + (shift ? 1 : 0) + ((mods & TermMods.Alt) != 0 ? 2 : 0) + (ctrl ? 4 : 0) + ((mods & TermMods.Meta) != 0 ? 8 : 0);

        switch (key)
        {
            case TermKey.Character:
                return EncodeCharacter(character, alt, ctrl);
            case TermKey.Enter:
            case TermKey.KeypadEnter:
                if (key == TermKey.KeypadEnter && modes.ApplicationKeypad && modifier == 1) return Esc("OM");
                return alt ? Esc("\r") : [(byte)'\r'];
            case TermKey.Tab:
                if (shift) return Esc("[Z");
                return alt ? Esc("\t") : [(byte)'\t'];
            case TermKey.Backspace:
                // DEL, as a terminal with the usual erase character expects; Ctrl asks for the older code.
                var back = ctrl ? (byte)0x08 : (byte)0x7F;
                return alt ? [0x1B, back] : [back];
            case TermKey.Escape:
                return alt ? [0x1B, 0x1B] : [0x1B];
            case TermKey.Up: return Cursor('A', modifier, modes);
            case TermKey.Down: return Cursor('B', modifier, modes);
            case TermKey.Right: return Cursor('C', modifier, modes);
            case TermKey.Left: return Cursor('D', modifier, modes);
            case TermKey.Home: return Cursor('H', modifier, modes);
            case TermKey.End: return Cursor('F', modifier, modes);
            case TermKey.PageUp: return Tilde(5, modifier);
            case TermKey.PageDown: return Tilde(6, modifier);
            case TermKey.Insert: return Tilde(2, modifier);
            case TermKey.Delete: return Tilde(3, modifier);
            case >= TermKey.F1 and <= TermKey.F4:
                var final = (char)('P' + (key - TermKey.F1));
                return modifier == 1 ? Esc($"O{final}") : Esc($"[1;{modifier}{final}");
            case >= TermKey.F5 and <= TermKey.F20:
                int[] codes = [15, 17, 18, 19, 20, 21, 23, 24, 25, 26, 28, 29, 31, 32, 33, 34];
                return Tilde(codes[key - TermKey.F5], modifier);
            case >= TermKey.Keypad0 and <= TermKey.KeypadEquals:
                return Keypad(key, modifier, modes);
        }

        return null;
    }

    private static byte[]? EncodeCharacter(char c, bool alt, bool ctrl)
    {
        if (ctrl)
        {
            var control = ControlCode(c);
            if (control < 0) return alt && c != 0 ? Esc(c.ToString()) : null;
            return alt ? [0x1B, (byte)control] : [(byte)control];
        }

        if (!alt) return null;
        // Alt sends Escape first, then the character; the program sees the Meta key.
        return Esc(c.ToString());
    }

    /// <summary>The control code Ctrl turns a character into, or -1 when it has none.</summary>
    private static int ControlCode(char c)
    {
        if (c is >= 'a' and <= 'z') return c - 'a' + 1;
        if (c is >= 'A' and <= 'Z') return c - 'A' + 1;
        return c switch
        {
            ' ' or '@' or '2' => 0,
            '[' or '3' => 0x1B,
            '\\' or '4' => 0x1C,
            ']' or '5' => 0x1D,
            '^' or '6' or '~' => 0x1E,
            '_' or '7' or '/' => 0x1F,
            '?' or '8' => 0x7F,
            _ => -1,
        };
    }

    private static byte[] Cursor(char final, int modifier, TerminalModes modes)
    {
        if (modifier != 1) return Esc($"[1;{modifier}{final}");
        return modes.ApplicationCursor ? Esc($"O{final}") : Esc($"[{final}");
    }

    private static byte[] Tilde(int code, int modifier) => modifier == 1 ? Esc($"[{code}~") : Esc($"[{code};{modifier}~");

    private static byte[] Keypad(TermKey key, int modifier, TerminalModes modes)
    {
        if (!modes.ApplicationKeypad || modifier != 1)
        {
            var plain = key switch
            {
                >= TermKey.Keypad0 and <= TermKey.Keypad9 => (char)('0' + (key - TermKey.Keypad0)),
                TermKey.KeypadAdd => '+',
                TermKey.KeypadSubtract => '-',
                TermKey.KeypadMultiply => '*',
                TermKey.KeypadDivide => '/',
                TermKey.KeypadDecimal => '.',
                TermKey.KeypadEquals => '=',
                _ => '\0',
            };
            return plain == '\0' ? [] : [(byte)plain];
        }

        var application = key switch
        {
            >= TermKey.Keypad0 and <= TermKey.Keypad9 => (char)('p' + (key - TermKey.Keypad0)),
            TermKey.KeypadAdd => 'k',
            TermKey.KeypadSubtract => 'm',
            TermKey.KeypadMultiply => 'j',
            TermKey.KeypadDivide => 'o',
            TermKey.KeypadDecimal => 'n',
            TermKey.KeypadEquals => 'X',
            _ => '\0',
        };
        return Esc($"O{application}");
    }

    private static byte[] Esc(string rest)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(rest) + 1];
        bytes[0] = 0x1B;
        Encoding.UTF8.GetBytes(rest, 0, rest.Length, bytes, 1);
        return bytes;
    }
}
