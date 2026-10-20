using SkiaSharp;
using Wicked.Editor.Layout;
using Wicked.Editor.Syntax;

namespace Wicked.Editor.View;

/// <summary>A line as a frame shows it: shaped when the layout thread has done it, otherwise its text and tokens for a plain draw.</summary>
public readonly record struct FrameLine(int Line, float Y, ShapedLine? Shaped, string Text, LineTokens? Tokens, ColorTable? Colors, bool Current);

/// <summary>
/// Everything one frame of the editor draws, decided on the UI thread and handed to the render thread unchanged:
/// positions are final, nothing is measured or shaped while drawing except a line the layout thread has not reached.
/// </summary>
public sealed record EditorFrame
{
    public required long Generation { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    public required EditorTheme Theme { get; init; }

    public required EditorFonts Fonts { get; init; }

    public required int TabSize { get; init; }

    public required float GutterWidth { get; init; }

    /// <summary>Where column 0 is drawn when not scrolled: the gutter plus a little air.</summary>
    public required float TextLeft { get; init; }

    public required float ScrollX { get; init; }

    public required IReadOnlyList<FrameLine> Lines { get; init; }

    public required IReadOnlyList<SKRect> Selections { get; init; }

    public required IReadOnlyList<SKRect> Carets { get; init; }

    /// <summary>Find matches in sight; the current one drawn brighter.</summary>
    public IReadOnlyList<SKRect> Matches { get; init; } = [];

    public IReadOnlyList<SKRect> CurrentMatch { get; init; } = [];

    public required bool CaretsVisible { get; init; }

    public required bool Focused { get; init; }

    public SKRect VerticalThumb { get; init; }

    public SKRect HorizontalThumb { get; init; }

    public bool ThumbActive { get; init; }

    /// <summary>Text an input method is composing, drawn underlined at <see cref="PreeditAt"/> until it is committed.</summary>
    public string? Preedit { get; init; }

    public SKPoint PreeditAt { get; init; }
}
