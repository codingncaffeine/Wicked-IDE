namespace Wicked.Core.Terminal;

public enum CursorShape
{
    Block,
    Underline,
    Bar,
}

public enum MouseTracking
{
    None,

    /// <summary>Button presses and releases (1000).</summary>
    Buttons,

    /// <summary>Plus motion while a button is down (1002).</summary>
    Drag,

    /// <summary>Plus all motion (1003).</summary>
    Any,
}

public enum MouseEncoding
{
    /// <summary>The original single-byte form, which cannot say anything past column 223.</summary>
    X10,
    Utf8,
    Sgr,
    Urxvt,
}

/// <summary>What the running program has asked the terminal to do, as an immutable snapshot (swapped, never edited).</summary>
public sealed record TerminalModes
{
    /// <summary>DECCKM: arrow keys send SS3 instead of CSI.</summary>
    public bool ApplicationCursor { get; init; }

    /// <summary>DECKPAM / DECNKM: the keypad sends application sequences.</summary>
    public bool ApplicationKeypad { get; init; }

    public bool BracketedPaste { get; init; }

    public MouseTracking Mouse { get; init; }

    public MouseEncoding MouseEncoding { get; init; }

    public bool FocusReporting { get; init; }

    public bool AlternateScreen { get; init; }

    /// <summary>The wheel sends arrow keys on the alternate screen when no mouse mode is on (1007).</summary>
    public bool AlternateScroll { get; init; } = true;

    public bool CursorVisible { get; init; } = true;

    /// <summary>The program asked for a blinking (true) or steady (false) cursor; null leaves it to the setting.</summary>
    public bool? CursorBlink { get; init; }

    /// <summary>The program's DECSCUSR choice; null leaves it to the setting.</summary>
    public CursorShape? CursorShape { get; init; }

    public bool AutoWrap { get; init; } = true;

    public bool ReverseVideo { get; init; }

    public bool SynchronizedOutput { get; init; }

    public bool NewLineMode { get; init; }
}

/// <summary>How a terminal is set up.</summary>
public sealed record TerminalOptions
{
    /// <summary>Lines kept above the screen.</summary>
    public int Scrollback { get; init; } = 10_000;

    /// <summary>Whether a program may put text on the clipboard (OSC 52). Off unless the user turns it on.</summary>
    public bool AllowClipboardWrite { get; init; }

    /// <summary>The default text and background colours (0xAARRGGBB), told to programs that ask (OSC 10 / 11).</summary>
    public uint Foreground { get; init; } = 0xFFD4D4D4;

    public uint Background { get; init; } = 0xFF111216;

    /// <summary>The 16 ANSI colours (0xAARRGGBB); null for the standard ones.</summary>
    public uint[]? Ansi { get; init; }

    /// <summary>The application name and version programs get from an XTVERSION query.</summary>
    public string Name { get; init; } = "Wicked";
}

/// <summary>One row of a frame: the cells to draw (possibly fewer than the width; the rest is blank) and what the row carries.</summary>
public readonly record struct FrameRow(Cell[] Cells, bool Wrapped, CommandRecord? Command);

/// <summary>
/// What the terminal shows at one moment: the visible rows, the cursor, and the tables the cells refer to. Built on the
/// emulator's thread and never changed afterwards, so the UI thread can take it and the render thread can paint it.
/// </summary>
public sealed class TerminalFrame
{
    public required int Columns { get; init; }

    public required int Rows { get; init; }

    public required FrameRow[] Lines { get; init; }

    /// <summary>Counts every frame built; a newer frame has a larger number.</summary>
    public required long Version { get; init; }

    /// <summary>The absolute number of the first visible row (see <see cref="TerminalEmulator.BaseLine"/>).</summary>
    public required long TopLine { get; init; }

    /// <summary>Changes when the buffer was rebuilt (a resize reflowed it, the scrollback was cleared): absolute line numbers from an older epoch mean nothing.</summary>
    public required int Epoch { get; init; }

    /// <summary>How many lines are in the scrollback.</summary>
    public required int ScrollbackLines { get; init; }

    /// <summary>How many lines above the live screen the view is scrolled; 0 at the bottom.</summary>
    public required int ViewportOffset { get; init; }

    /// <summary>The cursor's row in the frame, or -1 when it is not on screen or hidden.</summary>
    public required int CursorRow { get; init; }

    public required int CursorColumn { get; init; }

    public required TerminalModes Modes { get; init; }

    public required ClusterTable Clusters { get; init; }

    public required LinkTable Links { get; init; }

    /// <summary>Colours programs set with OSC 4 / 10 / 11 / 12 (0xAARRGGBB, 0 for none), or null when there are none.</summary>
    public uint[]? PaletteOverrides { get; init; }

    public uint ForegroundOverride { get; init; }

    public uint BackgroundOverride { get; init; }

    public uint CursorColorOverride { get; init; }

    public string Title { get; init; } = "";

    /// <summary>Counts the bell's rings; the view flashes when it changes.</summary>
    public int BellCount { get; init; }

    public string TextOf(in Cell cell) => (cell.Flags & CellFlags.Cluster) != 0 ? Clusters[cell.Rune] : cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune);
}

/// <summary>A place in the terminal's text: an absolute line number and a column.</summary>
public readonly record struct TerminalPoint(long Line, int Column) : IComparable<TerminalPoint>
{
    public int CompareTo(TerminalPoint other) => Line != other.Line ? Line.CompareTo(other.Line) : Column.CompareTo(other.Column);

    public static bool operator <(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) < 0;

    public static bool operator >(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) > 0;

    public static bool operator <=(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) <= 0;

    public static bool operator >=(TerminalPoint a, TerminalPoint b) => a.CompareTo(b) >= 0;
}

public enum SelectionKind
{
    /// <summary>From the start to the end, wrapping line by line.</summary>
    Stream,

    /// <summary>Whole lines.</summary>
    Lines,

    /// <summary>A rectangle of cells (Alt+drag).</summary>
    Block,
}

/// <summary>A selection: both ends are inclusive cells, in any order.</summary>
public readonly record struct TerminalSelection(TerminalPoint Anchor, TerminalPoint Active, SelectionKind Kind = SelectionKind.Stream)
{
    public TerminalPoint Start => Anchor <= Active ? Anchor : Active;

    public TerminalPoint End => Anchor <= Active ? Active : Anchor;

    public bool IsEmpty => Kind == SelectionKind.Stream && Anchor == Active;
}

/// <summary>A match of a search in the scrollback: inclusive start cell, exclusive end cell, on possibly different lines.</summary>
public readonly record struct TerminalMatch(TerminalPoint Start, TerminalPoint End);

/// <summary>A command of the shell's, with where its prompt is.</summary>
public readonly record struct TerminalCommand(long Line, CommandRecord Record);
