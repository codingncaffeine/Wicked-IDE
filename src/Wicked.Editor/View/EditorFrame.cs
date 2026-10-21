using SkiaSharp;
using Wicked.Editor.Layout;
using Wicked.Editor.Syntax;

namespace Wicked.Editor.View;

/// <summary>
/// A row as a frame shows it: its line, shaped when the layout thread has done it, otherwise its text and tokens for a
/// plain draw. A wrapped line has a row per piece: <see cref="Sub"/> says which, the line is drawn moved by
/// <see cref="Shift"/> and clipped to [<see cref="ClipLeft"/>, <see cref="ClipRight"/>) of its own x.
/// </summary>
public readonly record struct FrameLine(
    int Line,
    float Y,
    ShapedLine? Shaped,
    string Text,
    LineTokens? Tokens,
    ColorTable? Colors,
    bool Current,
    FoldMark Fold = FoldMark.None,
    int Sub = 0,
    bool LastRow = true,
    float ClipLeft = 0,
    float ClipRight = float.PositiveInfinity,
    float Shift = 0)
{
    /// <summary>Whether this row is a piece of a wrapped line (and so needs its clip).</summary>
    public bool IsPiece => Sub > 0 || !float.IsPositiveInfinity(ClipRight);
}

/// <summary>The minimap's part of a frame: the model, the document line on each row (folds skipped), where it starts, the slider.</summary>
public sealed record MinimapFrame(Wicked.Editor.Features.MinimapModel Model, int[] Lines, float Left, float Top, SKRect Slider);

/// <summary>A line's mark in the gutter's fold column.</summary>
public enum FoldMark
{
    None,

    /// <summary>A range starts here and is open: an arrow down, shown while the pointer is over the gutter.</summary>
    Open,

    /// <summary>A range starts here and is folded: an arrow right, always shown, and "…" after the line.</summary>
    Folded,
}

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

    public bool GutterHover { get; init; }

    /// <summary>Where column 0 is drawn when not scrolled: the gutter plus a little air.</summary>
    public required float TextLeft { get; init; }

    public required float ScrollX { get; init; }

    public required IReadOnlyList<FrameLine> Lines { get; init; }

    public required IReadOnlyList<SKRect> Selections { get; init; }

    public required IReadOnlyList<SKRect> Carets { get; init; }

    /// <summary>The minimap, when shown.</summary>
    public MinimapFrame? Minimap { get; init; }

    /// <summary>Boxes round the bracket at the caret and its partner.</summary>
    public IReadOnlyList<SKRect> BracketBoxes { get; init; } = [];

    /// <summary>Indent guides in sight: x, top, bottom, and whether the guide is the caret's block's.</summary>
    public IReadOnlyList<(float X, float Top, float Bottom, bool Active)> Guides { get; init; } = [];

    /// <summary>Spaces (a dot) and tabs (an arrow) made visible: where, and which.</summary>
    public IReadOnlyList<(SKPoint At, bool Tab)> Whitespace { get; init; } = [];

    /// <summary>Find matches in sight; the current one drawn brighter.</summary>
    public IReadOnlyList<SKRect> Matches { get; init; } = [];

    public IReadOnlyList<SKRect> CurrentMatch { get; init; } = [];

    /// <summary>Decorated text in sight (squiggles, highlights, diff ranges): a rectangle per row it covers.</summary>
    public IReadOnlyList<(SKRect Rect, Wicked.Editor.Features.RangeStyle Style)> RangeMarks { get; init; } = [];

    /// <summary>Rows with a background of their own (diff lines, conflict sides, the debugger's line).</summary>
    public IReadOnlyList<(float Y, Wicked.Editor.Features.LineStyle Style)> LineFills { get; init; } = [];

    /// <summary>Gutter glyphs by row: breakpoints in the glyph margin, source-control bars beside the numbers.</summary>
    public IReadOnlyList<(float Y, Wicked.Editor.Features.GutterGlyph Glyph)> GutterMarks { get; init; } = [];

    /// <summary>Ticks in the vertical scrollbar's track: where the problems, changes and matches are in the whole text.</summary>
    public IReadOnlyList<(float Y, uint Color)> Overview { get; init; } = [];

    /// <summary>The number shown beside a document line, when it is not the line's own (a diff side's filler rows show none).</summary>
    public Func<int, int?>? LineNumber { get; init; }

    /// <summary>The glyph margin's width at the gutter's left (0 when it is off).</summary>
    public float GlyphMargin { get; init; }

    public required bool CaretsVisible { get; init; }

    public required bool Focused { get; init; }

    public SKRect VerticalThumb { get; init; }

    public SKRect HorizontalThumb { get; init; }

    public bool ThumbActive { get; init; }

    /// <summary>Text an input method is composing, drawn underlined at <see cref="PreeditAt"/> until it is committed.</summary>
    public string? Preedit { get; init; }

    public SKPoint PreeditAt { get; init; }
}
