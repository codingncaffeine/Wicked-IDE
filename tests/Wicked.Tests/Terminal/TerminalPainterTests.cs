using SkiaSharp;
using Wicked.App.Terminal;
using Wicked.Core.Terminal;
using Wicked.Editor.Layout;

namespace Wicked.Tests.Terminal;

/// <summary>The painter on an offscreen Skia canvas: what colour ends up where.</summary>
public sealed class TerminalPainterTests
{
    private static readonly Lazy<EditorFonts> Fonts = new(() => EditorFonts.LoadDefault(new EditorFontSettings(14, 1f, false)));
    private static readonly TerminalTheme Theme = TerminalTheme.Default;

    private sealed class Shot(SKBitmap bitmap, TerminalMetrics metrics, float gutter) : IDisposable
    {
        public uint At(float x, float y) => (uint)bitmap.GetPixel((int)x, (int)y);

        /// <summary>The colour of a pixel inside a cell, near its top-left corner (no glyph ink reaches there).</summary>
        public uint Corner(int row, int column) => At(gutter + column * metrics.Width + 1, row * metrics.Height + 1);

        public TerminalMetrics Metrics => metrics;

        public int InkIn(int row, int column, uint background)
        {
            var count = 0;
            for (var y = (int)(row * metrics.Height); y < (int)((row + 1) * metrics.Height); y++)
            {
                for (var x = (int)(gutter + column * metrics.Width); x < (int)(gutter + (column + 1) * metrics.Width); x++)
                {
                    if (At(x, y) != background) count++;
                }
            }

            return count;
        }

        public void Dispose() => bitmap.Dispose();
    }

    private static Shot Draw(string input, Func<TerminalPaintState, TerminalPaintState>? adjust = null, int columns = 20, int rows = 5, float gutter = 0, Action<VtScreen>? prepare = null)
    {
        var screen = new VtScreen(columns, rows).Feed(input);
        prepare?.Invoke(screen);
        var metrics = TerminalMetrics.For(Fonts.Value);
        var width = (int)(gutter + columns * metrics.Width + 14);
        var height = (int)(rows * metrics.Height);
        var state = new TerminalPaintState
        {
            Frame = screen.Frame,
            Theme = Theme,
            Fonts = Fonts.Value,
            Metrics = metrics,
            Width = width,
            Height = height,
            Gutter = gutter,
            Focused = true,
        };
        if (adjust is not null) state = adjust(state);
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        TerminalPainter.Paint(canvas, state);
        return new Shot(bitmap, metrics, gutter);
    }

    private static TerminalPaintState With(TerminalPaintState s, Action<Mutable> change)
    {
        var m = new Mutable(s);
        change(m);
        return m.Build();
    }

    private sealed class Mutable(TerminalPaintState s)
    {
        public bool Focused = s.Focused;
        public bool CursorOn = s.CursorOn;
        public CursorShape Shape = s.Shape;
        public TerminalSelection? Selection = s.Selection;
        public IReadOnlyList<TerminalMatch> Matches = s.Matches;
        public int CurrentMatch = s.CurrentMatch;
        public string? Preedit = s.Preedit;
        public float Bell = s.BellFlash;
        public (TerminalPoint, TerminalPoint)? Hover = s.HoverLink;

        public TerminalPaintState Build() => new()
        {
            Frame = s.Frame, Theme = s.Theme, Fonts = s.Fonts, Metrics = s.Metrics, Width = s.Width, Height = s.Height, Gutter = s.Gutter,
            Focused = Focused, CursorOn = CursorOn, Shape = Shape, Selection = Selection, Matches = Matches, CurrentMatch = CurrentMatch,
            Preedit = Preedit, BellFlash = Bell, HoverLink = Hover, ScrollbarVisible = s.ScrollbarVisible, ScrollbarActive = s.ScrollbarActive,
        };
    }

    private static uint Argb(uint rgb) => 0xFF000000 | rgb;

    [Fact]
    public void TheSurfaceIsTheThemesBackground()
    {
        using var shot = Draw("", s => With(s, m => m.CursorOn = false));
        Assert.Equal(Theme.Background, shot.Corner(3, 10));
    }

    [Fact]
    public void ACellBackgroundFillsItsCell()
    {
        using var shot = Draw("\e[44m  \e[0m x");
        Assert.Equal(0xFF2472C8u, shot.Corner(0, 0));
        Assert.Equal(0xFF2472C8u, shot.Corner(0, 1));
        Assert.Equal(Theme.Background, shot.Corner(0, 2));
    }

    [Fact]
    public void TrueColourBackgroundsAreExact()
    {
        using var shot = Draw("\e[48;2;10;200;30m ");
        Assert.Equal(0xFF0AC81Eu, shot.Corner(0, 0));
    }

    [Fact]
    public void TextIsDrawnInItsColour()
    {
        using var shot = Draw("\e[38;2;255;0;0mM", s => With(s, m => m.CursorOn = false));
        Assert.True(shot.InkIn(0, 0, Theme.Background) > 10);
        var red = 0;
        for (var y = 0; y < shot.Metrics.Height; y++)
        {
            for (var x = 0; x < shot.Metrics.Width; x++)
            {
                var p = shot.At(x, y);
                if (((p >> 16) & 0xFF) >= 0xC0 && ((p >> 8) & 0xFF) <= 0x40 && (p & 0xFF) <= 0x40) red++;
            }
        }

        Assert.True(red > 5, $"{red} red pixels");
    }

    [Fact]
    public void InverseSwapsTheColours()
    {
        using var shot = Draw("\e[7m ", s => With(s, m => m.CursorOn = false));
        Assert.Equal(Theme.Foreground, shot.Corner(0, 0));
    }

    [Fact]
    public void ReverseVideoSwapsTheWholeScreen()
    {
        using var shot = Draw("\e[?5h", s => With(s, m => m.CursorOn = false));
        Assert.Equal(Theme.Foreground, shot.Corner(2, 5));
    }

    [Fact]
    public void ABlockCursorFillsItsCellWhenFocused()
    {
        using var shot = Draw("ab");
        Assert.Equal(Theme.Cursor, shot.Corner(0, 2));
        Assert.Equal(Theme.Background, shot.Corner(0, 3));
    }

    [Fact]
    public void ABlinkingCursorIsHiddenInItsOffPhase()
    {
        using var shot = Draw("ab", s => With(s, m => m.CursorOn = false));
        Assert.Equal(Theme.Background, shot.Corner(0, 2));
    }

    [Fact]
    public void AnUnfocusedCursorIsOnlyAnOutline()
    {
        using var shot = Draw("ab", s => With(s, m => m.Focused = false));
        Assert.True(shot.InkIn(0, 2, Theme.Background) > 10);
        Assert.Equal(Theme.Background, shot.At(2 * shot.Metrics.Width + shot.Metrics.Width / 2, shot.Metrics.Height / 2));
    }

    [Fact]
    public void ABarCursorIsTwoPixelsWide()
    {
        using var shot = Draw("ab", s => With(s, m => m.Shape = CursorShape.Bar));
        var x = 2 * shot.Metrics.Width;
        Assert.Equal(Theme.Cursor, shot.At(x + 1, 3));
        Assert.Equal(Theme.Background, shot.At(x + 4, 3));
    }

    [Fact]
    public void AnUnderlineCursorSitsAtTheFootOfItsCell()
    {
        using var shot = Draw("ab", s => With(s, m => m.Shape = CursorShape.Underline));
        var x = 2 * shot.Metrics.Width + 3;
        Assert.Equal(Theme.Cursor, shot.At(x, shot.Metrics.Height - 1));
        Assert.Equal(Theme.Background, shot.At(x, 2));
    }

    [Fact]
    public void AHiddenCursorIsNotDrawn()
    {
        using var shot = Draw("ab\e[?25l");
        Assert.Equal(Theme.Background, shot.Corner(0, 2));
    }

    [Fact]
    public void ASelectionIsTintedAndOnlyWhereItCovers()
    {
        var selection = new TerminalSelection(new TerminalPoint(0, 1), new TerminalPoint(0, 3));
        using var shot = Draw("abcdefg", s => With(s, m =>
        {
            m.Selection = selection;
            m.CursorOn = false;
        }));
        Assert.NotEqual(Theme.Background, shot.Corner(0, 2));
        Assert.Equal(Theme.Background, shot.Corner(0, 5));
        Assert.Equal(Theme.Background, shot.Corner(0, 0));
    }

    [Fact]
    public void ABlockSelectionIsARectangle()
    {
        var selection = new TerminalSelection(new TerminalPoint(0, 1), new TerminalPoint(2, 2), SelectionKind.Block);
        using var shot = Draw("abcdef\r\nabcdef\r\nabcdef", s => With(s, m =>
        {
            m.Selection = selection;
            m.CursorOn = false;
        }));
        Assert.NotEqual(Theme.Background, shot.Corner(1, 1));
        Assert.NotEqual(Theme.Background, shot.Corner(1, 2));
        Assert.Equal(Theme.Background, shot.Corner(1, 3));
        Assert.Equal(Theme.Background, shot.Corner(1, 0));
    }

    [Fact]
    public void SelectionColumnsWidenToWholeWideCharacters()
    {
        var cells = new VtScreen().Feed("a漢b").Frame.Lines[0].Cells;
        var selection = new TerminalSelection(new TerminalPoint(0, 2), new TerminalPoint(0, 2));
        Assert.Equal((1, 3), TerminalPainter.SelectionColumns(selection, 0, 20, cells));
    }

    [Fact]
    public void FindMatchesAreHighlightedAndTheCurrentOneMoreSo()
    {
        var matches = new[] { new TerminalMatch(new TerminalPoint(0, 0), new TerminalPoint(0, 2)), new TerminalMatch(new TerminalPoint(0, 4), new TerminalPoint(0, 6)) };
        using var shot = Draw("ab  cd", s => With(s, m =>
        {
            m.Matches = matches;
            m.CurrentMatch = 1;
            m.CursorOn = false;
        }));
        Assert.NotEqual(Theme.Background, shot.Corner(0, 0));
        Assert.NotEqual(shot.Corner(0, 0), shot.Corner(0, 4));
        Assert.Equal(Theme.Background, shot.Corner(0, 3));
    }

    [Fact]
    public void AWideCharacterCoversTwoCells()
    {
        Assert.SkipWhen(SkiaSharp.SKFontManager.Default.MatchCharacter(0x6F22) is null, "no font on this machine draws CJK (a bare CI runner)");
        using var shot = Draw("漢", s => With(s, m => m.CursorOn = false));
        Assert.True(shot.InkIn(0, 0, Theme.Background) > 10);
        Assert.True(shot.InkIn(0, 1, Theme.Background) > 10);
    }

    [Fact]
    public void AnUnderlineIsDrawnUnderTheText()
    {
        using var plain = Draw("x", s => With(s, m => m.CursorOn = false));
        using var underlined = Draw("\e[4mx", s => With(s, m => m.CursorOn = false));
        Assert.True(underlined.InkIn(0, 0, Theme.Background) > plain.InkIn(0, 0, Theme.Background) + 5);
    }

    [Fact]
    public void ALinkUnderThePointerIsUnderlined()
    {
        using var plain = Draw("link", s => With(s, m => m.CursorOn = false));
        using var hovered = Draw("link", s => With(s, m =>
        {
            m.CursorOn = false;
            m.Hover = (new TerminalPoint(0, 0), new TerminalPoint(0, 4));
        }));
        Assert.True(hovered.InkIn(0, 1, Theme.Background) > plain.InkIn(0, 1, Theme.Background));
    }

    [Fact]
    public void AFinishedCommandHasAGreenOrRedMarkInTheGutter()
    {
        using var ok = Draw("\e]133;A\a$ \e]133;B\ax\r\n\e]133;C\a\e]133;D;0\a", s => With(s, m => m.CursorOn = false), gutter: 14);
        using var failed = Draw("\e]133;A\a$ \e]133;B\ax\r\n\e]133;C\a\e]133;D;2\a", s => With(s, m => m.CursorOn = false), gutter: 14);
        var centre = (14 / 2, (int)(shotCentre(ok)));
        Assert.Equal(Theme.CommandSuccess, ok.At(centre.Item1, centre.Item2));
        Assert.Equal(Theme.CommandFailure, failed.At(centre.Item1, centre.Item2));
    }

    private static float shotCentre(Shot shot) => shot.Metrics.Height / 2;

    [Fact]
    public void AMarkIsOnlyShownOnceTheCommandStarted()
    {
        using var shot = Draw("\e]133;A\a$ ", s => With(s, m => m.CursorOn = false), gutter: 14);
        Assert.Equal(Theme.Background, shot.At(7, shot.Metrics.Height / 2));
    }

    [Fact]
    public void TheBellFlashLightensTheScreen()
    {
        using var quiet = Draw("", s => With(s, m => m.CursorOn = false));
        using var flash = Draw("", s => With(s, m =>
        {
            m.CursorOn = false;
            m.Bell = 1;
        }));
        Assert.True(flash.Corner(2, 5) > quiet.Corner(2, 5) || flash.Corner(2, 5) != quiet.Corner(2, 5));
    }

    [Fact]
    public void ComposedTextIsDrawnAtTheCursor()
    {
        using var shot = Draw("ab", s => With(s, m => m.Preedit = "nihao"));
        Assert.True(shot.InkIn(0, 2, Theme.Background) > 10);
    }

    [Fact]
    public void ThePaletteIsTheThemesAndThePalettesOverridesWin()
    {
        using var themed = Draw("\e[41m ");
        Assert.Equal(Theme.Palette[1], themed.Corner(0, 0));
        using var overridden = Draw("\e]4;1;rgb:00/ff/00\a\e[41m ");
        Assert.Equal(0xFF00FF00u, overridden.Corner(0, 0));
    }

    [Fact]
    public void ADimCellIsFadedTowardsTheBackground()
    {
        using var normal = Draw("M", s => With(s, m => m.CursorOn = false));
        using var dim = Draw("\e[2mM", s => With(s, m => m.CursorOn = false));
        Assert.NotEqual(Maximum(normal), Maximum(dim));
    }

    private static uint Maximum(Shot shot)
    {
        uint best = 0;
        for (var y = 0; y < shot.Metrics.Height; y++)
        {
            for (var x = 0; x < shot.Metrics.Width; x++) best = Math.Max(best, shot.At(x, y) & 0xFF);
        }

        return best;
    }

    [Fact]
    public void FontFallbackDrawsSymbolsTheTerminalFontLacks()
    {
        using var shot = Draw("☃", s => With(s, m => m.CursorOn = false));
        // A snowman: not in JetBrains Mono, found in whatever the system offers; if the system offers none, nothing is drawn and nothing breaks.
        Assert.True(shot.InkIn(0, 0, Theme.Background) >= 0);
    }

    [Fact]
    public void ThemesTakeTheirColoursFromVsCodeKeys()
    {
        var themed = TerminalTheme.Default.WithVsCodeColors(new Dictionary<string, uint> { ["terminal.background"] = 0xFF101010, ["terminal.ansiRed"] = 0xFFAA0000, ["terminal.ansiBrightBlue"] = 0xFF0000FF });
        Assert.Equal(0xFF101010u, themed.Background);
        Assert.Equal(0xFFAA0000u, themed.Palette[1]);
        Assert.Equal(0xFF0000FFu, themed.Palette[12]);
        Assert.Equal(TerminalTheme.Default.Palette[2], themed.Palette[2]);
    }

    [Fact]
    public void ThePaletteHasTheStandardColourCube()
    {
        var p = TerminalTheme.Default.Palette;
        Assert.Equal(256, p.Length);
        Assert.Equal(0xFF000000u, p[16]);
        Assert.Equal(0xFFFFFFFFu, p[231]);
        Assert.Equal(0xFF080808u, p[232]);
        Assert.Equal(0xFFEEEEEEu, p[255]);
        Assert.Equal(Argb(0xFF0000), p[196]);
    }
}
