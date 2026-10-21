using SkiaSharp;

namespace Wicked.Editor.Layout;

/// <summary>
/// One line, shaped and ready to draw: glyph runs grouped by colour, the x of every caret position, its width. Built on
/// the layout thread, read by the UI thread (positions) and drawn by the render thread (runs); never changed after.
/// </summary>
public sealed class ShapedLine
{
    internal ShapedLine(string text, IReadOnlyList<ColoredRun> runs, float[] caretX, IReadOnlyList<Decoration> decorations, IReadOnlyList<InlaySpan>? inlays = null)
    {
        Text = text;
        Runs = runs;
        CaretX = caretX;
        Decorations = decorations;
        Inlays = inlays ?? [];
    }

    /// <summary>Text drawn in the line that is not the document's (inlay hints, a suggestion's ghost text), where it sits.</summary>
    public IReadOnlyList<InlaySpan> Inlays { get; }

    public string Text { get; }

    public IReadOnlyList<ColoredRun> Runs { get; }

    public IReadOnlyList<Decoration> Decorations { get; }

    /// <summary>The x of the caret before each UTF-16 index, and after the last (length + 1 entries).</summary>
    public float[] CaretX { get; }

    public float Width => Math.Max(CaretX[^1], DrawnWidth);

    /// <summary>How far the line draws, inlays at its end included (the caret's last place can be before them).</summary>
    internal float DrawnWidth { get; init; }

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

/// <summary>An inlay's place in its line: from <see cref="Left"/> to <see cref="Right"/>, before column <see cref="Column"/>.</summary>
public readonly record struct InlaySpan(int Column, float Left, float Right, Wicked.Editor.Features.InlineStyle Style);

/// <summary>Text to draw at a column of a line that is not in the document (see <see cref="Wicked.Editor.Features.InlineDecoration"/>).</summary>
public readonly record struct Inlay(int Column, string Text, Wicked.Editor.Features.InlineStyle Style, bool PadLeft = false, bool PadRight = false);

/// <summary>A line's inlays, sorted by column, compared by content so equal lines share a cache entry.</summary>
public readonly struct Inlays(Inlay[]? items) : IEquatable<Inlays>
{
    private readonly Inlay[] _items = items ?? [];

    public int Count => _items?.Length ?? 0;

    public Inlay this[int i] => _items[i];

    public bool Equals(Inlays other) => (_items ?? []).AsSpan().SequenceEqual(other._items ?? []);

    public override bool Equals(object? obj) => obj is Inlays other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        for (var i = 0; i < Count; i++) hash.Add(_items[i]);
        return hash.ToHashCode();
    }
}
