namespace Wicked.Editor.View;

/// <summary>The editor's own colours (0xAARRGGBB), apart from the code's, which come from the syntax theme.</summary>
public sealed record EditorTheme
{
    public uint Background { get; init; } = 0xFF111216;

    /// <summary>Text the syntax theme gives no colour.</summary>
    public uint Foreground { get; init; } = 0xFFD4D4D4;

    public uint CurrentLine { get; init; } = 0xFF181A20;

    public uint Selection { get; init; } = 0x598B7BF7;

    public uint SelectionUnfocused { get; init; } = 0x332C3038;

    public uint Caret { get; init; } = 0xFFA194FA;

    public uint LineNumber { get; init; } = 0xFF4B5160;

    public uint LineNumberActive { get; init; } = 0xFFC9CED8;

    public uint IndentGuide { get; init; } = 0xFF23262D;

    public uint ScrollbarThumb { get; init; } = 0x26FFFFFF;

    public uint ScrollbarThumbActive { get; init; } = 0x40FFFFFF;

    /// <summary>The caret's width in pixels (VS Code's default is 2).</summary>
    public float CaretWidth { get; init; } = 2;

    public static readonly EditorTheme WickedDark = new();
}
