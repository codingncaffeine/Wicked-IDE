using System.Globalization;
using SkiaSharp;
using Wicked.Editor.Features;
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

        foreach (var (y, style) in frame.LineFills) Fill(canvas, paint, new SKRect(frame.GutterWidth, y, frame.Width, y + lineHeight), LineColor(theme, style));

        // The text area: selections, text and carets, clipped so nothing scrolls under the gutter.
        canvas.Save();
        canvas.ClipRect(new SKRect(frame.GutterWidth, 0, frame.Width, frame.Height));
        foreach (var (x, top, bottom, active) in frame.Guides) Fill(canvas, paint, new SKRect(x, top, x + 1, bottom), active ? theme.IndentGuideActive : theme.IndentGuide);
        foreach (var rect in frame.Matches) FillRound(canvas, paint, rect, theme.FindMatch, 3);
        foreach (var rect in frame.CurrentMatch) FillRound(canvas, paint, rect, theme.FindMatchCurrent, 3);
        foreach (var (rect, style) in frame.RangeMarks)
        {
            var fill = style switch
            {
                RangeStyle.Highlight => theme.WordHighlight,
                RangeStyle.HighlightWrite => theme.WordHighlightWrite,
                RangeStyle.Inserted => theme.DiffInserted,
                RangeStyle.Removed => theme.DiffRemoved,
                _ => 0u,
            };
            if (fill != 0) FillRound(canvas, paint, rect, fill, 2);
        }

        var selection = frame.Focused ? theme.Selection : theme.SelectionUnfocused;
        foreach (var rect in frame.Selections) FillRound(canvas, paint, rect, selection, 3);

        var origin = frame.TextLeft - frame.ScrollX;
        foreach (var line in frame.Lines)
        {
            var baseline = line.Y + frame.Fonts.Baseline;
            var x = origin + line.Shift;
            if (line.IsPiece)
            {
                // A row of a wrapped line: the whole line moved left, only this row's piece showing.
                canvas.Save();
                var right = float.IsPositiveInfinity(line.ClipRight) ? frame.Width : x + line.ClipRight;
                canvas.ClipRect(new SKRect(x + line.ClipLeft, line.Y, right, line.Y + lineHeight));
            }

            if (line.Shaped is { } shaped)
            {
                foreach (var run in shaped.Runs)
                {
                    paint.Color = new SKColor(run.Color);
                    canvas.DrawText(run.Blob, x, baseline, paint);
                }

                foreach (var d in shaped.Decorations) Fill(canvas, paint, new SKRect(x + d.Rect.Left, baseline + d.Rect.Top, x + d.Rect.Right, baseline + d.Rect.Bottom), d.Color);
            }
            else if (line.Text.Length > 0)
            {
                DrawPlain(canvas, paint, frame, line, x, baseline);
            }

            if (line.IsPiece) canvas.Restore();

            if (line.Fold == FoldMark.Folded && line.LastRow)
            {
                var end = x + (line.Shaped?.Width ?? line.Text.Length * frame.Fonts.CharAdvance) + frame.Fonts.CharAdvance;
                var badge = new SKRect(end, line.Y + 4, end + frame.Fonts.CharAdvance * 3, line.Y + lineHeight - 4);
                FillRound(canvas, paint, badge, theme.Selection, 4);
                paint.Color = new SKColor(theme.Foreground);
                for (var dot = 0; dot < 3; dot++) canvas.DrawCircle(badge.Left + badge.Width * (dot + 1) / 4, badge.MidY, 1.2f, paint);
            }
        }

        DrawRangeMarks(canvas, paint, frame);

        foreach (var box in frame.BracketBoxes)
        {
            paint.Color = new SKColor(theme.BracketMatch);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            canvas.DrawRect(box, paint);
            paint.Style = SKPaintStyle.Fill;
        }

        foreach (var (at, tab) in frame.Whitespace)
        {
            paint.Color = new SKColor(theme.Whitespace);
            if (tab)
            {
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                canvas.DrawLine(at.X, at.Y, at.X + frame.Fonts.CharAdvance * 1.6f, at.Y, paint);
                canvas.DrawLine(at.X + frame.Fonts.CharAdvance * 1.6f - 3, at.Y - 3, at.X + frame.Fonts.CharAdvance * 1.6f, at.Y, paint);
                canvas.DrawLine(at.X + frame.Fonts.CharAdvance * 1.6f - 3, at.Y + 3, at.X + frame.Fonts.CharAdvance * 1.6f, at.Y, paint);
                paint.Style = SKPaintStyle.Fill;
            }
            else
            {
                canvas.DrawCircle(at, 1.1f, paint);
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
            if (line.Sub > 0) continue; // a wrapped line's number and fold arrow go on its first row only
            paint.Color = new SKColor(line.Current ? theme.LineNumberActive : theme.LineNumber);
            var label = (line.Line + 1).ToString(CultureInfo.InvariantCulture);
            canvas.DrawText(label, frame.GutterWidth - 22, line.Y + frame.Fonts.Baseline, SKTextAlign.Right, numbers, paint);
            if (line.Fold == FoldMark.Folded || (line.Fold == FoldMark.Open && frame.GutterHover)) DrawFoldArrow(canvas, paint, frame, line);
        }

        foreach (var (y, glyph) in frame.GutterMarks) DrawGlyph(canvas, paint, frame, y, glyph);

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

        if (frame.Minimap is { } minimap) DrawMinimap(canvas, paint, frame, minimap);

        foreach (var (y, color) in frame.Overview) Fill(canvas, paint, new SKRect(frame.Width - 10, y - 1, frame.Width - 2, y + 2), color);

        var thumb = frame.ThumbActive ? theme.ScrollbarThumbActive : theme.ScrollbarThumb;
        if (!frame.VerticalThumb.IsEmpty) FillRound(canvas, paint, frame.VerticalThumb, thumb, 4);
        if (!frame.HorizontalThumb.IsEmpty) FillRound(canvas, paint, frame.HorizontalThumb, thumb, 4);
        canvas.Restore();
        LayoutService.FrameRendered(frame.Generation);
    }

    /// <summary>
    /// VS Code's "blocks" minimap: each line two pixels tall, each run of text a block in its colour (a column a pixel
    /// wide), and the slider over the lines in view.
    /// </summary>
    private static void DrawMinimap(SKCanvas canvas, SKPaint paint, EditorFrame frame, MinimapFrame minimap)
    {
        const float row = TextView.MinimapLineHeight;
        var model = minimap.Model;
        Fill(canvas, paint, new SKRect(minimap.Left, 0, minimap.Left + TextView.MinimapWidth, frame.Height), frame.Theme.Background);
        canvas.Save();
        canvas.ClipRect(new SKRect(minimap.Left, 0, minimap.Left + TextView.MinimapWidth, frame.Height));
        for (var i = 0; i < minimap.Lines.Length; i++)
        {
            var line = minimap.Lines[i];
            if (line < 0 || line >= model.LineCount) continue;
            var y = minimap.Top + i * row;
            for (var r = 0; r < model.RunCount(line); r++)
            {
                var run = model.Run(line, r);
                var color = model.ColorOf(run.ColorId);
                // Dimmed a little, as minimaps draw, so the slider reads over it.
                paint.Color = new SKColor(color).WithAlpha(150);
                canvas.DrawRect(new SKRect(minimap.Left + 6 + run.StartColumn * 0.8f, y, minimap.Left + 6 + (run.StartColumn + run.Length) * 0.8f, y + row - 0.4f), paint);
            }
        }

        canvas.Restore();
        Fill(canvas, paint, minimap.Slider, frame.ThumbActive ? frame.Theme.ScrollbarThumbActive : frame.Theme.ScrollbarThumb);
    }

    private static uint LineColor(EditorTheme theme, LineStyle style) => style switch
    {
        LineStyle.Inserted => theme.DiffInsertedLine,
        LineStyle.Removed => theme.DiffRemovedLine,
        LineStyle.ConflictCurrent => theme.ConflictCurrent,
        LineStyle.ConflictIncoming => theme.ConflictIncoming,
        LineStyle.ConflictBase => theme.ConflictBase,
        _ => theme.StoppedLine,
    };

    /// <summary>The marks drawn over text: wavy underlines for problems, dots for hints, a strike, a fade, a link's underline.</summary>
    private static void DrawRangeMarks(SKCanvas canvas, SKPaint paint, EditorFrame frame)
    {
        var theme = frame.Theme;
        foreach (var (rect, style) in frame.RangeMarks)
        {
            switch (style)
            {
                case RangeStyle.Error or RangeStyle.Warning or RangeStyle.Info:
                    var color = style == RangeStyle.Error ? theme.ErrorColor : style == RangeStyle.Warning ? theme.WarningColor : theme.InfoColor;
                    DrawSquiggle(canvas, paint, rect.Left, rect.Right, rect.Bottom - 2.5f, color);
                    break;
                case RangeStyle.Hint:
                    paint.Color = new SKColor(theme.HintColor);
                    for (var dot = 0; dot < 3; dot++) canvas.DrawCircle(rect.Left + 1.5f + dot * 3, rect.Bottom - 1.5f, 0.8f, paint);
                    break;
                case RangeStyle.Unnecessary:
                    // Faded: the background laid over the text at half strength.
                    Fill(canvas, paint, rect, (theme.Background & 0x00FFFFFF) | 0x80000000);
                    break;
                case RangeStyle.Deprecated:
                    Fill(canvas, paint, new SKRect(rect.Left, rect.MidY, rect.Right, rect.MidY + 1), theme.Foreground);
                    break;
                case RangeStyle.Link:
                    Fill(canvas, paint, new SKRect(rect.Left, rect.Bottom - 2, rect.Right, rect.Bottom - 1), theme.Link);
                    break;
            }
        }
    }

    /// <summary>A wave two pixels high under a stretch of text, as every editor marks a problem.</summary>
    private static void DrawSquiggle(SKCanvas canvas, SKPaint paint, float left, float right, float y, uint color)
    {
        if (right - left < 4) right = left + 4;
        using var path = new SKPath();
        path.MoveTo(left, y + 1);
        var up = true;
        for (var x = left + 2; x <= right; x += 2)
        {
            path.LineTo(x, up ? y - 1 : y + 1);
            up = !up;
        }

        paint.Color = new SKColor(color);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        canvas.DrawPath(path, paint);
        paint.Style = SKPaintStyle.Fill;
    }

    /// <summary>A gutter glyph: breakpoints and the debugger's arrow in the glyph margin, source-control marks beside the numbers.</summary>
    private static void DrawGlyph(SKCanvas canvas, SKPaint paint, EditorFrame frame, float y, GutterGlyph glyph)
    {
        var theme = frame.Theme;
        var h = frame.Fonts.LineHeight;
        var cx = frame.GlyphMargin > 0 ? frame.GlyphMargin / 2 + 2 : 6;
        var cy = y + h / 2;
        var bar = frame.GutterWidth - 19;
        switch (glyph)
        {
            case GutterGlyph.Added or GutterGlyph.Modified:
                Fill(canvas, paint, new SKRect(bar, y, bar + 3, y + h), glyph == GutterGlyph.Added ? theme.GutterAdded : theme.GutterModified);
                break;
            case GutterGlyph.Deleted:
                using (var wedge = new SKPath())
                {
                    wedge.MoveTo(bar, y - 4);
                    wedge.LineTo(bar + 5, y);
                    wedge.LineTo(bar, y + 4);
                    wedge.Close();
                    paint.Color = new SKColor(theme.GutterDeleted);
                    canvas.DrawPath(wedge, paint);
                }

                break;
            case GutterGlyph.Breakpoint or GutterGlyph.BreakpointDisabled:
                paint.Color = new SKColor(glyph == GutterGlyph.Breakpoint ? theme.Breakpoint : theme.BreakpointDisabled);
                canvas.DrawCircle(cx, cy, 4.5f, paint);
                break;
            case GutterGlyph.BreakpointUnverified:
                paint.Color = new SKColor(theme.BreakpointDisabled);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1.5f;
                canvas.DrawCircle(cx, cy, 4, paint);
                paint.Style = SKPaintStyle.Fill;
                break;
            case GutterGlyph.BreakpointConditional:
                paint.Color = new SKColor(theme.Breakpoint);
                canvas.DrawCircle(cx, cy, 4.5f, paint);
                Fill(canvas, paint, new SKRect(cx - 2.5f, cy - 2, cx + 2.5f, cy - 1), theme.Background);
                Fill(canvas, paint, new SKRect(cx - 2.5f, cy + 1, cx + 2.5f, cy + 2), theme.Background);
                break;
            case GutterGlyph.Logpoint:
                paint.Color = new SKColor(theme.Breakpoint);
                using (var diamond = new SKPath())
                {
                    diamond.MoveTo(cx, cy - 5);
                    diamond.LineTo(cx + 5, cy);
                    diamond.LineTo(cx, cy + 5);
                    diamond.LineTo(cx - 5, cy);
                    diamond.Close();
                    canvas.DrawPath(diamond, paint);
                }

                break;
            case GutterGlyph.Stopped:
                paint.Color = new SKColor(theme.StoppedGlyph);
                using (var arrow = new SKPath())
                {
                    arrow.MoveTo(cx - 5, cy - 3);
                    arrow.LineTo(cx, cy - 3);
                    arrow.LineTo(cx, cy - 6);
                    arrow.LineTo(cx + 6, cy);
                    arrow.LineTo(cx, cy + 6);
                    arrow.LineTo(cx, cy + 3);
                    arrow.LineTo(cx - 5, cy + 3);
                    arrow.Close();
                    canvas.DrawPath(arrow, paint);
                }

                break;
            case GutterGlyph.Lightbulb:
                paint.Color = new SKColor(theme.Lightbulb);
                canvas.DrawCircle(cx, cy - 1.5f, 3.5f, paint);
                Fill(canvas, paint, new SKRect(cx - 1.8f, cy + 2.5f, cx + 1.8f, cy + 4.5f), theme.Lightbulb);
                break;
        }
    }

    /// <summary>The fold column's arrow: down for an open range, right for a folded one.</summary>
    private static void DrawFoldArrow(SKCanvas canvas, SKPaint paint, EditorFrame frame, FrameLine line)
    {
        var cx = frame.GutterWidth - 10;
        var cy = line.Y + frame.Fonts.LineHeight / 2;
        paint.Color = new SKColor(line.Fold == FoldMark.Folded ? frame.Theme.LineNumberActive : frame.Theme.LineNumber);
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1.4f;
        paint.StrokeCap = SKStrokeCap.Round;
        using var path = new SKPath();
        if (line.Fold == FoldMark.Folded)
        {
            path.MoveTo(cx - 1.5f, cy - 3.5f);
            path.LineTo(cx + 2f, cy);
            path.LineTo(cx - 1.5f, cy + 3.5f);
        }
        else
        {
            path.MoveTo(cx - 3.5f, cy - 1.5f);
            path.LineTo(cx, cy + 2f);
            path.LineTo(cx + 3.5f, cy - 1.5f);
        }

        canvas.DrawPath(path, paint);
        paint.Style = SKPaintStyle.Fill;
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
