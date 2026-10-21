using System.Globalization;
using System.Text;

namespace Wicked.Core.Terminal;

public enum MouseAction
{
    Press,
    Release,
    Move,
    WheelUp,
    WheelDown,
    WheelLeft,
    WheelRight,
}

/// <summary>What a mouse event sends to a program that asked for the mouse (modes 1000, 1002, 1003 with the encodings 1005, 1006, 1015).</summary>
public static class MouseEncoder
{
    /// <summary>The bytes for an event, or null when the program's mode does not want it.</summary>
    /// <param name="action">What happened.</param>
    /// <param name="button">0 left, 1 middle, 2 right (the button pressed, released, or held during a move; -1 for none).</param>
    /// <param name="mods">The modifier keys held.</param>
    /// <param name="column">The cell, counted from 0.</param>
    /// <param name="row">The cell, counted from 0.</param>
    /// <param name="modes">The program's mouse settings.</param>
    public static byte[]? Encode(MouseAction action, int button, TermMods mods, int column, int row, TerminalModes modes)
    {
        if (modes.Mouse == MouseTracking.None) return null;
        if (action == MouseAction.Move)
        {
            if (modes.Mouse == MouseTracking.Buttons) return null;
            if (modes.Mouse == MouseTracking.Drag && button < 0) return null;
        }

        int code;
        switch (action)
        {
            case MouseAction.WheelUp: code = 64; break;
            case MouseAction.WheelDown: code = 65; break;
            case MouseAction.WheelLeft: code = 66; break;
            case MouseAction.WheelRight: code = 67; break;
            default:
                code = button < 0 ? 3 : Math.Min(button, 2);
                if (action == MouseAction.Release && modes.MouseEncoding != MouseEncoding.Sgr) code = 3;
                if (action == MouseAction.Move) code += 32;
                if (action == MouseAction.Move && button < 0) code = 35;
                break;
        }

        if ((mods & TermMods.Shift) != 0) code += 4;
        if ((mods & (TermMods.Alt | TermMods.Meta)) != 0) code += 8;
        if ((mods & TermMods.Ctrl) != 0) code += 16;

        var x = column + 1;
        var y = row + 1;
        switch (modes.MouseEncoding)
        {
            case MouseEncoding.Sgr:
                var final = action == MouseAction.Release ? 'm' : 'M';
                return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\u001b[<{code};{x};{y}{final}"));
            case MouseEncoding.Urxvt:
                return Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"\u001b[{code + 32};{x};{y}M"));
            case MouseEncoding.Utf8:
                var utf8 = new List<byte> { 0x1B, (byte)'[', (byte)'M' };
                utf8.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(32 + code)));
                utf8.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(32 + Math.Min(x, 2015))));
                utf8.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(32 + Math.Min(y, 2015))));
                return utf8.ToArray();
            default:
                // The single-byte form cannot say anything past column or row 223.
                if (x > 223 || y > 223) return null;
                return [0x1B, (byte)'[', (byte)'M', (byte)(32 + code), (byte)(32 + x), (byte)(32 + y)];
        }
    }
}
