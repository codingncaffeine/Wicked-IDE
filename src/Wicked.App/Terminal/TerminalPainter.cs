using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;
using Wicked.Core.Terminal;
using Wicked.Editor.Layout;

namespace Wicked.App.Terminal;

/// <summary>The size of one character cell, from the font.</summary>
/// <param name="Width">The advance of the monospaced grid.</param>
/// <param name="Height">From the top of one row to the top of the next; the font's own height, so box-drawing characters meet.</param>
/// <param name="Baseline">From the top of a row to the text's baseline.</param>
public readonly record struct TerminalMetrics(float Width, float Height, float Baseline)
{
    public static TerminalMetrics For(EditorFonts fonts)
    {
        var height = MathF.Ceiling(fonts.Ascent + fonts.Descent);
        return new TerminalMetrics(fonts.CharAdvance, height, MathF.Round(fonts.Ascent));
    }
}

/// <summary>Everything the painter needs to draw one frame. Made on the UI thread from values it already holds; drawn on the render thread.</summary>
public sealed class TerminalPaintState
{
    public required TerminalFrame Frame { get; init; }

    public required TerminalTheme Theme { get; init; }

    public required EditorFonts Fonts { get; init; }

    public required TerminalMetrics Metrics { get; init; }

    public required float Width { get; init; }

    public required float Height { get; init; }

    /// <summary>Room on the left for the marks beside commands.</summary>
    public float Gutter { get; init; }

    public bool Focused { get; init; }

    /// <summary>The blinking cursor's phase: shown or hidden.</summary>
    public bool CursorOn { get; init; } = true;

    public CursorShape Shape { get; init; } = CursorShape.Block;

    public TerminalSelection? Selection { get; init; }

    public IReadOnlyList<TerminalMatch> Matches { get; init; } = [];

    public int CurrentMatch { get; init; } = -1;

    /// <summary>The link under the pointer: its first cell and the cell after its last.</summary>
    public (TerminalPoint Start, TerminalPoint End)? HoverLink { get; init; }

    /// <summary>Text the input method is composing, drawn at the cursor.</summary>
    public string? Preedit { get; init; }

    /// <summary>0 to 1: how much of the bell's flash is left.</summary>
    public float BellFlash { get; init; }

    public bool ScrollbarVisible { get; init; }

    public bool ScrollbarActive { get; init; }
}

/// <summary>Draws a terminal frame onto a Skia canvas: on the render thread, or an offscreen canvas in a test.</summary>
public static class TerminalPainter
{
    // Skia fonts and paints belong to the thread that draws with them.
    [ThreadStatic]
    private static Dictionary<(SKTypeface, float), SKFont>? _fonts;

    [ThreadStatic]
    private static SKPaint? _paint;

    private static readonly ConcurrentDictionary<(SKTypeface, int), bool> HasGlyph = new();
    private static readonly ConcurrentDictionary<int, SKTypeface?> Fallbacks = new();

    public const float ScrollbarWidth = 10;

    public static void Paint(SKCanvas canvas, TerminalPaintState s)
    {
        var paint = _paint ??= new SKPaint { IsAntialias = true };
        var frame = s.Frame;
        var theme = s.Theme;
        var (cw, ch, baseline) = (s.Metrics.Width, s.Metrics.Height, s.Metrics.Baseline);
        var gutter = s.Gutter;

        canvas.Save();
        canvas.ClipRect(new SKRect(0, 0, s.Width, s.Height));
        var defaultBg = DefaultBackground(frame, theme);
        Fill(canvas, paint, new SKRect(0, 0, s.Width, s.Height), defaultBg);

        for (var r = 0; r < frame.Rows; r++)
        {
            var y = r * ch;
            var row = frame.Lines[r];
            PaintBackgrounds(canvas, paint, s, row, gutter, y, defaultBg);
            PaintHighlights(canvas, paint, s, r, gutter, y);
            PaintText(canvas, paint, s, row, gutter, y, baseline);
            PaintDecorations(canvas, paint, s, r, row, gutter, y);
            if (row.Command is { } command) PaintCommandMark(canvas, paint, s, command, y);
        }

        PaintCursor(canvas, paint, s, defaultBg);
        if (s.ScrollbarVisible) PaintScrollbar(canvas, paint, s);
        if (s.BellFlash > 0) Fill(canvas, paint, new SKRect(0, 0, s.Width, s.Height), ((uint)(s.BellFlash * 56) << 24) | 0x00FFFFFF);
        canvas.Restore();
        _ = cw;
    }

    // ── Colours ──────────────────────────────────────────────────────────────────────────────────

    private static uint DefaultForeground(TerminalFrame f, TerminalTheme t)
    {
        var fg = f.ForegroundOverride != 0 ? f.ForegroundOverride : t.Foreground;
        var bg = f.BackgroundOverride != 0 ? f.BackgroundOverride : t.Background;
        return f.Modes.ReverseVideo ? bg : fg;
    }

    private static uint DefaultBackground(TerminalFrame f, TerminalTheme t)
    {
        var fg = f.ForegroundOverride != 0 ? f.ForegroundOverride : t.Foreground;
        var bg = f.BackgroundOverride != 0 ? f.BackgroundOverride : t.Background;
        return f.Modes.ReverseVideo ? fg : bg;
    }

    private static uint Resolve(uint color, TerminalFrame frame, TerminalTheme theme, uint fallback)
    {
        if (CellColor.IsRgb(color)) return CellColor.ToArgb(color);
        if (CellColor.IsPalette(color))
        {
            var i = CellColor.PaletteIndex(color);
            return frame.PaletteOverrides is { } o && o[i] != 0 ? o[i] : theme.Palette[i];
        }

        return fallback;
    }

    /// <summary>The colours a cell is drawn in, inverse and dim applied.</summary>
    private static (uint Fg, uint Bg) Colors(in Cell cell, TerminalFrame frame, TerminalTheme theme, uint defaultFg, uint defaultBg)
    {
        var fg = Resolve(cell.Fg, frame, theme, defaultFg);
        var bg = Resolve(cell.Bg, frame, theme, defaultBg);
        if ((cell.Flags & CellFlags.Inverse) != 0) (fg, bg) = (bg, fg);
        if ((cell.Flags & CellFlags.Dim) != 0) fg = Blend(fg, bg, 0.5f);
        return (fg, bg);
    }

    private static uint Blend(uint a, uint b, float t)
    {
        uint Channel(int shift) => (uint)Math.Round(((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t);
        return 0xFF000000 | (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }

    private static void Fill(SKCanvas canvas, SKPaint paint, SKRect rect, uint argb)
    {
        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(argb);
        canvas.DrawRect(rect, paint);
    }

    // ── Rows ─────────────────────────────────────────────────────────────────────────────────────

    private static void PaintBackgrounds(SKCanvas canvas, SKPaint paint, TerminalPaintState s, in FrameRow row, float gutter, float y, uint defaultBg)
    {
        var cells = row.Cells;
        var (cw, ch) = (s.Metrics.Width, s.Metrics.Height);
        var defaultFg = DefaultForeground(s.Frame, s.Theme);
        var col = 0;
        while (col < cells.Length)
        {
            var (_, bg) = Colors(cells[col], s.Frame, s.Theme, defaultFg, defaultBg);
            var end = col + 1;
            while (end < cells.Length && Colors(cells[end], s.Frame, s.Theme, defaultFg, defaultBg).Bg == bg) end++;
            if (bg != defaultBg) Fill(canvas, paint, new SKRect(gutter + col * cw, y, gutter + end * cw, y + ch), bg);
            col = end;
        }
    }

    private static void PaintHighlights(SKCanvas canvas, SKPaint paint, TerminalPaintState s, int r, float gutter, float y)
    {
        var frame = s.Frame;
        var line = frame.TopLine + r;
        var (cw, ch) = (s.Metrics.Width, s.Metrics.Height);

        for (var i = 0; i < s.Matches.Count; i++)
        {
            var m = s.Matches[i];
            if (line < m.Start.Line || line > m.End.Line) continue;
            var from = line == m.Start.Line ? m.Start.Column : 0;
            var to = line == m.End.Line ? m.End.Column : frame.Columns;
            if (to > from) Fill(canvas, paint, new SKRect(gutter + from * cw, y, gutter + to * cw, y + ch), i == s.CurrentMatch ? s.Theme.FindMatchCurrent : s.Theme.FindMatch);
        }

        if (s.Selection is { } selection && SelectionColumns(selection, line, frame.Columns, frame.Lines[r].Cells) is var (a, b) && b > a)
        {
            Fill(canvas, paint, new SKRect(gutter + a * cw, y, gutter + b * cw, y + ch), s.Focused ? s.Theme.Selection : s.Theme.SelectionInactive);
        }
    }

    /// <summary>The columns of a row a selection covers: from the first to one past the last, or null when it does not touch the row.</summary>
    internal static (int From, int To)? SelectionColumns(TerminalSelection selection, long line, int columns, Cell[] cells)
    {
        var start = selection.Start;
        var end = selection.End;
        if (line < start.Line || line > end.Line) return null;
        int a, b;
        switch (selection.Kind)
        {
            case SelectionKind.Lines:
                return (0, columns);
            case SelectionKind.Block:
                a = Math.Min(selection.Anchor.Column, selection.Active.Column);
                b = Math.Max(selection.Anchor.Column, selection.Active.Column) + 1;
                break;
            default:
                a = line == start.Line ? start.Column : 0;
                b = line == end.Line ? end.Column + 1 : columns;
                break;
        }

        // Both halves of a wide character are selected together.
        if (a > 0 && a < cells.Length && (cells[a].Flags & CellFlags.WideTail) != 0) a--;
        if (b > 0 && b - 1 < cells.Length && (cells[b - 1].Flags & CellFlags.Wide) != 0) b++;
        return (Math.Max(0, a), Math.Min(columns, b));
    }

    private static void PaintText(SKCanvas canvas, SKPaint paint, TerminalPaintState s, in FrameRow row, float gutter, float y, float baseline)
    {
        var cells = row.Cells;
        var cw = s.Metrics.Width;
        var frame = s.Frame;
        var defaultFg = DefaultForeground(frame, s.Theme);
        var defaultBg = DefaultBackground(frame, s.Theme);
        var run = new StringBuilder();
        var col = 0;
        while (col < cells.Length)
        {
            var cell = cells[col];
            if ((cell.Flags & CellFlags.WideTail) != 0 || (cell.Rune == 0 && (cell.Flags & CellFlags.Cluster) == 0) || (cell.Flags & CellFlags.Invisible) != 0)
            {
                col++;
                continue;
            }

            var (fg, _) = Colors(cell, frame, s.Theme, defaultFg, defaultBg);
            var style = cell.Flags & (CellFlags.Bold | CellFlags.Italic);

            if ((cell.Flags & (CellFlags.Wide | CellFlags.Cluster)) != 0 || cell.Rune > 0xFFFF || NeedsFallback(s, cell.Rune, style))
            {
                DrawGlyph(canvas, paint, s, frame.TextOf(cell), cell.Rune, style, fg, gutter + col * cw, y + baseline);
                col++;
                continue;
            }

            // A run of plain cells in one style is drawn as one string: the grid and the font's advance agree.
            run.Clear();
            var start = col;
            while (col < cells.Length)
            {
                var c = cells[col];
                if ((c.Flags & (CellFlags.Wide | CellFlags.WideTail | CellFlags.Cluster | CellFlags.Invisible)) != 0 || c.Rune > 0xFFFF) break;
                if ((c.Flags & (CellFlags.Bold | CellFlags.Italic)) != style) break;
                var blank = c.Rune == 0;
                if (!blank && (NeedsFallback(s, c.Rune, style) || Colors(c, frame, s.Theme, defaultFg, defaultBg).Fg != fg)) break;
                if (blank && col + 1 >= cells.Length) break;
                run.Append(blank ? ' ' : (char)c.Rune);
                col++;
            }

            // Trailing blanks carry no ink.
            var text = run.ToString().TrimEnd();
            if (text.Length > 0) DrawRun(canvas, paint, s, text, style, fg, gutter + start * cw, y + baseline);
        }
    }

    private static bool NeedsFallback(TerminalPaintState s, int rune, CellFlags style)
    {
        if (rune < 0x300) return false;
        var face = s.Fonts.For(ToTokenStyle(style));
        return !HasGlyph.GetOrAdd((face, rune), key => key.Item1.ContainsGlyph(key.Item2));
    }

    private static Wicked.Editor.Syntax.TokenStyle ToTokenStyle(CellFlags style)
    {
        var t = Wicked.Editor.Syntax.TokenStyle.None;
        if ((style & CellFlags.Bold) != 0) t |= Wicked.Editor.Syntax.TokenStyle.Bold;
        if ((style & CellFlags.Italic) != 0) t |= Wicked.Editor.Syntax.TokenStyle.Italic;
        return t;
    }

    private static SKFont FontFor(SKTypeface face, float size)
    {
        var fonts = _fonts ??= [];
        if (!fonts.TryGetValue((face, size), out var font))
        {
            font = new SKFont(face, size) { Subpixel = true, Edging = SKFontEdging.Antialias, Hinting = SKFontHinting.Slight, LinearMetrics = true };
            fonts[(face, size)] = font;
        }

        return font;
    }

    private static void DrawRun(SKCanvas canvas, SKPaint paint, TerminalPaintState s, string text, CellFlags style, uint fg, float x, float baseline)
    {
        var font = FontFor(s.Fonts.For(ToTokenStyle(style)), s.Fonts.Size);
        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(fg);
        canvas.DrawText(text, x, baseline, font, paint);
    }

    private static void DrawGlyph(SKCanvas canvas, SKPaint paint, TerminalPaintState s, string text, int rune, CellFlags style, uint fg, float x, float baseline)
    {
        var face = s.Fonts.For(ToTokenStyle(style));
        if (rune >= 0x300 && !HasGlyph.GetOrAdd((face, rune), key => key.Item1.ContainsGlyph(key.Item2)))
        {
            var fallback = Fallbacks.GetOrAdd(rune, r => SKFontManager.Default.MatchCharacter(r));
            if (fallback is not null) face = fallback;
        }

        var font = FontFor(face, s.Fonts.Size);
        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(fg);
        canvas.DrawText(text, x, baseline, font, paint);
    }

    // ── Underlines, strike-through, links, cursor, marks ─────────────────────────────────────────

    private static void PaintDecorations(SKCanvas canvas, SKPaint paint, TerminalPaintState s, int r, in FrameRow row, float gutter, float y)
    {
        var cells = row.Cells;
        var (cw, ch, baseline) = (s.Metrics.Width, s.Metrics.Height, s.Metrics.Baseline);
        var frame = s.Frame;
        var defaultFg = DefaultForeground(frame, s.Theme);
        var defaultBg = DefaultBackground(frame, s.Theme);
        var line = frame.TopLine + r;
        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeWidth = 1;
        var underlineY = y + baseline + MathF.Max(1.5f, s.Fonts.Descent / 3);

        for (var col = 0; col < cells.Length; col++)
        {
            var cell = cells[col];
            var hovered = s.HoverLink is { } link && new TerminalPoint(line, col) >= link.Start && new TerminalPoint(line, col) < link.End;
            var flags = cell.Flags & (CellFlags.AnyUnderline | CellFlags.Strike);
            if (flags == 0 && !hovered) continue;
            if ((cell.Flags & CellFlags.WideTail) != 0) continue;
            var (fg, _) = Colors(cell, frame, s.Theme, defaultFg, defaultBg);
            var width = (cell.Flags & CellFlags.Wide) != 0 ? 2 * cw : cw;
            var x0 = gutter + col * cw;
            paint.Color = new SKColor(fg);
            if ((flags & CellFlags.Strike) != 0) canvas.DrawLine(x0, y + ch * 0.5f, x0 + width, y + ch * 0.5f, paint);
            var under = flags & CellFlags.AnyUnderline;
            if (under == 0 && hovered) under = CellFlags.Underline;
            if (under == 0) continue;
            switch (under)
            {
                case CellFlags.DoubleUnderline:
                    canvas.DrawLine(x0, underlineY - 1.5f, x0 + width, underlineY - 1.5f, paint);
                    canvas.DrawLine(x0, underlineY + 1, x0 + width, underlineY + 1, paint);
                    break;
                case CellFlags.DottedUnderline:
                    for (var x = x0; x < x0 + width; x += 2) canvas.DrawPoint(x + 0.5f, underlineY, paint);
                    break;
                case CellFlags.DashedUnderline:
                    canvas.DrawLine(x0, underlineY, x0 + width * 0.7f, underlineY, paint);
                    break;
                case CellFlags.CurlyUnderline:
                    using (var path = new SKPath())
                    {
                        path.MoveTo(x0, underlineY + 1);
                        var steps = (int)MathF.Max(2, width / 2);
                        for (var i = 1; i <= steps; i++) path.LineTo(x0 + width * i / steps, underlineY + (i % 2 == 0 ? 1.5f : -1));
                        canvas.DrawPath(path, paint);
                    }

                    break;
                default:
                    canvas.DrawLine(x0, underlineY, x0 + width, underlineY, paint);
                    break;
            }
        }

        paint.Style = SKPaintStyle.Fill;
    }

    private static void PaintCommandMark(SKCanvas canvas, SKPaint paint, TerminalPaintState s, CommandRecord command, float y)
    {
        if (s.Gutter <= 0 || !command.OutputStarted) return;
        var centre = new SKPoint(s.Gutter / 2, y + s.Metrics.Height / 2);
        if (command.Finished)
        {
            paint.Style = SKPaintStyle.Fill;
            paint.Color = new SKColor(command.ExitCode is null or 0 ? s.Theme.CommandSuccess : s.Theme.CommandFailure);
            canvas.DrawCircle(centre, 3.5f, paint);
        }
        else
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1.2f;
            paint.Color = new SKColor(s.Theme.CommandRunning);
            canvas.DrawCircle(centre, 3f, paint);
            paint.Style = SKPaintStyle.Fill;
        }
    }

    private static void PaintCursor(SKCanvas canvas, SKPaint paint, TerminalPaintState s, uint defaultBg)
    {
        var frame = s.Frame;
        if (frame.CursorRow < 0 || frame.CursorRow >= frame.Rows) return;
        var (cw, ch, baseline) = (s.Metrics.Width, s.Metrics.Height, s.Metrics.Baseline);
        var cells = frame.Lines[frame.CursorRow].Cells;
        var cell = frame.CursorColumn < cells.Length ? cells[frame.CursorColumn] : default;
        var x = s.Gutter + frame.CursorColumn * cw;
        var y = frame.CursorRow * ch;
        var width = (cell.Flags & CellFlags.Wide) != 0 ? 2 * cw : cw;
        var color = frame.CursorColorOverride != 0 ? frame.CursorColorOverride : s.Theme.Cursor;

        if (s.Preedit is { Length: > 0 } preedit)
        {
            var font = FontFor(s.Fonts.Regular, s.Fonts.Size);
            var measured = font.MeasureText(preedit);
            Fill(canvas, paint, new SKRect(x, y, x + measured, y + ch), defaultBg);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = new SKColor(DefaultForeground(frame, s.Theme));
            canvas.DrawText(preedit, x, y + baseline, font, paint);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            canvas.DrawLine(x, y + ch - 1, x + measured, y + ch - 1, paint);
            paint.Style = SKPaintStyle.Fill;
            return;
        }

        if (!s.Focused)
        {
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            paint.Color = new SKColor(color);
            canvas.DrawRect(new SKRect(x + 0.5f, y + 0.5f, x + width - 0.5f, y + ch - 0.5f), paint);
            paint.Style = SKPaintStyle.Fill;
            return;
        }

        if (!s.CursorOn) return;
        switch (s.Shape)
        {
            case CursorShape.Bar:
                Fill(canvas, paint, new SKRect(x, y, x + 2, y + ch), color);
                break;
            case CursorShape.Underline:
                Fill(canvas, paint, new SKRect(x, y + ch - 2, x + width, y + ch), color);
                break;
            default:
                Fill(canvas, paint, new SKRect(x, y, x + width, y + ch), color);
                // The character under a block cursor is drawn again in the surface colour so it stays readable.
                if ((cell.Rune != 0 || (cell.Flags & CellFlags.Cluster) != 0) && (cell.Flags & CellFlags.WideTail) == 0)
                {
                    var style = cell.Flags & (CellFlags.Bold | CellFlags.Italic);
                    var surface = Colors(cell, frame, s.Theme, DefaultForeground(frame, s.Theme), defaultBg).Bg;
                    DrawGlyph(canvas, paint, s, frame.TextOf(cell), cell.Rune, style, surface, x, y + baseline);
                }

                break;
        }
    }

    private static void PaintScrollbar(SKCanvas canvas, SKPaint paint, TerminalPaintState s)
    {
        var frame = s.Frame;
        if (frame.ScrollbackLines == 0) return;
        var total = frame.ScrollbackLines + frame.Rows;
        var track = s.Height;
        var thumb = MathF.Max(24, track * frame.Rows / total);
        var first = frame.ScrollbackLines - frame.ViewportOffset;
        var top = (track - thumb) * first / frame.ScrollbackLines;
        var x = s.Width - ScrollbarWidth + 2;
        paint.Style = SKPaintStyle.Fill;
        paint.Color = new SKColor(s.ScrollbarActive ? s.Theme.Scrollbar + 0x40000000 : s.Theme.Scrollbar);
        canvas.DrawRoundRect(new SKRect(x, top, x + ScrollbarWidth - 4, top + thumb), 3, 3, paint);
    }
}
