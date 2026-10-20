using System.Globalization;
using SkiaSharp;
using Wicked.Editor.Layout;
using Wicked.Editor.Syntax;

namespace Wicked.Editor.View;

/// <summary>
/// Draws an <see cref="EditorFrame"/> onto a Skia canvas: the window's, on the render thread, or an offscreen one in a
/// test. A line the layout thread has not shaped yet is drawn plainly from its text and tokens (no ligatures, no font
/// fallback) for the frame or two until its shaped form arrives.
/// </summary>
public static class EditorPainter
{
    // The render thread's own fonts: Skia fonts are not to be shared between threads.
    [ThreadStatic]
    private static Dictionary<(SKTypeface, float), SKFont>? _fonts;

    [ThreadStatic]
    private static SKPaint? _paint;

    public static void Paint(SKCanvas canvas, EditorFrame frame)
    {
        var paint = _paint ??= new SKPaint { IsAntialias = true };
        var theme = frame.Theme;
        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, frame.Width, frame.Height));
        Fill(canvas, paint, new SKRect(0, 0, frame.Width, frame.Height), theme.Background);

        var lineHeight = frame.Fonts.LineHeight;
        foreach (var line in frame.Lines)
        {
            if (line.Current) Fill(canvas, paint, new SKRect(frame.GutterWidth, line.Y, frame.Width, line.Y + lineHeight), theme.CurrentLine);
        }

        // The text area: selections, text and carets, clipped so nothing scrolls under the gutter.
        canvas.Save();
        canvas.ClipRect(new SKRect(frame.GutterWidth, 0, frame.Width, frame.Height));
        var selection = frame.Focused ? theme.Selection : theme.SelectionUnfocused;
        foreach (var rect in frame.Selections) FillRound(canvas, paint, rect, selection, 3);

        var origin = frame.TextLeft - frame.ScrollX;
        foreach (var line in frame.Lines)
        {
            var baseline = line.Y + frame.Fonts.Baseline;
            if (line.Shaped is { } shaped)
            {
                foreach (var run in shaped.Runs)
                {
                    paint.Color = new SKColor(run.Color);
                    canvas.DrawText(run.Blob, origin, baseline, paint);
                }

                foreach (var d in shaped.Decorations) Fill(canvas, paint, new SKRect(origin + d.Rect.Left, baseline + d.Rect.Top, origin + d.Rect.Right, baseline + d.Rect.Bottom), d.Color);
            }
            else if (line.Text.Length > 0)
            {
                DrawPlain(canvas, paint, frame, line, origin, baseline);
            }
        }

        if (frame.CaretsVisible)
        {
            foreach (var rect in frame.Carets) Fill(canvas, paint, rect, theme.Caret);
        }

        canvas.Restore();

        // The gutter: line numbers, the current one brighter.
        var numbers = Font(frame.Fonts.Regular, frame.Fonts.Size, frame.Fonts);
        foreach (var line in frame.Lines)
        {
            paint.Color = new SKColor(line.Current ? theme.LineNumberActive : theme.LineNumber);
            var label = (line.Line + 1).ToString(CultureInfo.InvariantCulture);
            canvas.DrawText(label, frame.GutterWidth - 14, line.Y + frame.Fonts.Baseline, SKTextAlign.Right, numbers, paint);
        }

        if (frame.Preedit is { } preedit)
        {
            // The composition covers what is under it and is underlined, as every input method expects.
            var font = Font(frame.Fonts.Regular, frame.Fonts.Size, frame.Fonts);
            var width = font.MeasureText(preedit);
            var at = frame.PreeditAt;
            Fill(canvas, paint, new SKRect(at.X, at.Y, at.X + width, at.Y + lineHeight), theme.CurrentLine);
            paint.Color = new SKColor(theme.Foreground);
            canvas.DrawText(preedit, at.X, at.Y + frame.Fonts.Baseline, SKTextAlign.Left, font, paint);
            Fill(canvas, paint, new SKRect(at.X, at.Y + frame.Fonts.Baseline + 2, at.X + width, at.Y + frame.Fonts.Baseline + 3), theme.Foreground);
        }

        var thumb = frame.ThumbActive ? theme.ScrollbarThumbActive : theme.ScrollbarThumb;
        if (!frame.VerticalThumb.IsEmpty) FillRound(canvas, paint, frame.VerticalThumb, thumb, 4);
        if (!frame.HorizontalThumb.IsEmpty) FillRound(canvas, paint, frame.HorizontalThumb, thumb, 4);
        canvas.Restore();
        LayoutService.FrameRendered(frame.Generation);
    }

    /// <summary>A line without its shaped form: each token's text drawn in its colour, tabs to their stops.</summary>
    private static void DrawPlain(SKCanvas canvas, SKPaint paint, EditorFrame frame, FrameLine line, float origin, float baseline)
    {
        var text = line.Text;
        var tokens = line.Tokens;
        var tabWidth = frame.TabSize * frame.Fonts.SpaceAdvance;
        var x = 0f;
        var segments = tokens is { Count: > 0 } ? tokens.Count : 1;
        for (var t = 0; t < segments; t++)
        {
            var start = tokens is { Count: > 0 } ? Math.Min(tokens.Start(t), text.Length) : 0;
            var end = tokens is { Count: > 0 } && t + 1 < tokens.Count ? Math.Min(tokens.Start(t + 1), text.Length) : text.Length;
            if (end <= start) continue;
            var style = tokens is { Count: > 0 } ? tokens.Style(t) : TokenStyle.None;
            var font = Font(frame.Fonts.For(style), frame.Fonts.Size, frame.Fonts);
            paint.Color = new SKColor(tokens is { Count: > 0 } && line.Colors is not null ? line.Colors[tokens.Foreground(t)] : frame.Theme.Foreground);
            var pos = start;
            while (pos < end)
            {
                if (text[pos] == '\t')
                {
                    x = (MathF.Floor(x / tabWidth + 0.001f) + 1) * tabWidth;
                    pos++;
                    continue;
                }

                var stop = text.IndexOf('\t', pos, end - pos);
                if (stop < 0) stop = end;
                var piece = text.AsSpan(pos, stop - pos);
                canvas.DrawText(piece.ToString(), origin + x, baseline, SKTextAlign.Left, font, paint);
                x += font.MeasureText(piece);
                pos = stop;
            }
        }
    }

    private static SKFont Font(SKTypeface typeface, float size, EditorFonts fonts)
    {
        var cache = _fonts ??= [];
        if (!cache.TryGetValue((typeface, size), out var font)) cache[(typeface, size)] = font = fonts.CreateFont(typeface);
        return font;
    }

    private static void Fill(SKCanvas canvas, SKPaint paint, SKRect rect, uint color)
    {
        paint.Color = new SKColor(color);
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawRect(rect, paint);
    }

    private static void FillRound(SKCanvas canvas, SKPaint paint, SKRect rect, uint color, float radius)
    {
        paint.Color = new SKColor(color);
        paint.Style = SKPaintStyle.Fill;
        canvas.DrawRoundRect(rect, radius, radius, paint);
    }
}
