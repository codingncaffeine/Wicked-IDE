using SkiaSharp;

namespace Wicked.Editor.Layout;

/// <summary>
/// One line, shaped and ready to draw: glyph runs grouped by colour, the x of every caret position, its width. Built on
/// the layout thread, read by the UI thread (positions) and drawn by the render thread (runs); never changed after.
/// </summary>
public sealed class ShapedLine
{
    internal ShapedLine(string text, IReadOnlyList<ColoredRun> runs, float[] caretX, IReadOnlyList<Decoration> decorations)
    {
        Text = text;
        Runs = runs;
        CaretX = caretX;
        Decorations = decorations;
    }

    public string Text { get; }

    public IReadOnlyList<ColoredRun> Runs { get; }

    public IReadOnlyList<Decoration> Decorations { get; }

    /// <summary>The x of the caret before each UTF-16 index, and after the last (length + 1 entries).</summary>
    public float[] CaretX { get; }

    public float Width => CaretX[^1];

    /// <summary>When the line was last looked up, for the cache's least-recently-used eviction.</summary>
    internal long LastUsed;

    public float XOf(int column) => CaretX[Math.Clamp(column, 0, CaretX.Length - 1)];

    /// <summary>The caret position nearest to <paramref name="x"/>, never between the halves of a surrogate pair.</summary>
    public int ColumnAt(float x)
    {
        if (x <= 0) return 0;
        var best = 0;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < CaretX.Length; i++)
        {
            if (i > 0 && i < Text.Length && char.IsLowSurrogate(Text[i])) continue;
            var d = MathF.Abs(CaretX[i] - x);
            if (d < bestDistance)
            {
                best = i;
                bestDistance = d;
            }
            else if (CaretX[i] > x)
            {
                break;
            }
        }

        return best;
    }

    internal void Dispose()
    {
        foreach (var run in Runs) run.Blob.Dispose();
    }
}

/// <summary>Glyphs drawn in one colour.</summary>
public readonly record struct ColoredRun(SKTextBlob Blob, uint Color);

/// <summary>An underline or strikethrough, relative to the line's baseline.</summary>
public readonly record struct Decoration(SKRect Rect, uint Color);
