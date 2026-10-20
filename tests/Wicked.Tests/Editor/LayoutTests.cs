using SkiaSharp;
using Wicked.Editor.Layout;
using Wicked.Editor.Model;
using Wicked.Editor.View;

namespace Wicked.Tests.Editor;

/// <summary>One set of fonts for the whole class: loading them reads four font files.</summary>
public sealed class FontsFixture
{
    public EditorFonts Fonts { get; } = EditorFonts.LoadDefault();
}

public sealed class LayoutTests(FontsFixture fixture) : IClassFixture<FontsFixture>
{
    private readonly EditorFonts _fonts = fixture.Fonts;

    private ShapedLine Shape(string text, int tabSize = 4)
    {
        using var shaper = new LineShaper(_fonts);
        return shaper.Shape(text, null, null, 0xFFFFFFFF, tabSize);
    }

    [Fact]
    public void TheFontIsMonospacedAndTheLineHeightWhole()
    {
        Assert.True(_fonts.CharAdvance > 5);
        Assert.Equal(_fonts.CharAdvance, _fonts.SpaceAdvance, precision: 2);
        Assert.Equal(MathF.Round(_fonts.LineHeight), _fonts.LineHeight);
        Assert.InRange(_fonts.Baseline, _fonts.Ascent * 0.8f, _fonts.LineHeight);
    }

    [Fact]
    public void AsciiLandsOnTheGrid()
    {
        var line = Shape("int x = 42;");
        Assert.Equal(12, line.CaretX.Length);
        for (var i = 0; i < line.CaretX.Length; i++) Assert.Equal(i * _fonts.CharAdvance, line.CaretX[i], precision: 1);
        Assert.NotEmpty(line.Runs);
    }

    [Fact]
    public void TabsGoToTheNextStop()
    {
        var line = Shape("a\tb", tabSize: 4);
        Assert.Equal(4 * _fonts.SpaceAdvance, line.XOf(2), precision: 1);
        var full = Shape("\tb", tabSize: 4);
        Assert.Equal(4 * _fonts.SpaceAdvance, full.XOf(1), precision: 1);
    }

    [Fact]
    public void LigaturesKeepEveryCaretPositionInOrder()
    {
        var line = Shape("a => b != c");
        for (var i = 1; i < line.CaretX.Length; i++) Assert.True(line.CaretX[i] > line.CaretX[i - 1], $"caret {i} is not right of caret {i - 1}");
        Assert.Equal(11 * _fonts.CharAdvance, line.Width, precision: 1);
    }

    [Fact]
    public void ClicksFindTheNearestCaretPosition()
    {
        var line = Shape("hello");
        Assert.Equal(0, line.ColumnAt(-5));
        Assert.Equal(2, line.ColumnAt(2.4f * _fonts.CharAdvance));
        Assert.Equal(3, line.ColumnAt(2.6f * _fonts.CharAdvance));
        Assert.Equal(5, line.ColumnAt(99 * _fonts.CharAdvance));
    }

    [Fact]
    public void ASurrogatePairIsOneCaretStop()
    {
        var line = Shape("a\U0001F600b");
        Assert.Equal(5, line.CaretX.Length);
        Assert.True(line.XOf(3) > line.XOf(1));
        // Anywhere across the emoji, a click lands before or after it, never between its two halves.
        for (var x = line.XOf(1); x <= line.XOf(3); x += 0.5f) Assert.NotEqual(2, line.ColumnAt(x));
    }

    [Fact]
    public async Task TheLayoutThreadShapesWhatIsAskedAndTheCacheServesIt()
    {
        using var layout = new LayoutService(_fonts);
        var ready = new SemaphoreSlim(0);
        layout.Ready += () => ready.Release();
        var key = new LineKey("var answer = 42;", null, null, 0xFFFFFFFF, 4, _fonts.Version);
        Assert.False(layout.TryGet(key, out _));
        layout.Request([key]);
        Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(layout.TryGet(key, out var line));
        Assert.Equal("var answer = 42;", line.Text);
    }
}

public sealed class TextViewTests(FontsFixture fixture) : IClassFixture<FontsFixture>
{
    private readonly EditorFonts _fonts = fixture.Fonts;

    private static string Lines(int count) => string.Join('\n', Enumerable.Range(0, count).Select(i => $"line number {i} with some text"));

    [Fact]
    public void APointAndItsOffsetAgree()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(Lines(200));
        var view = new TextView(doc, layout, EditOptions.Default);
        view.Resize(800, 400);
        var offset = doc.Text.Offset(5, 7);
        var x = view.TextLeft + view.XOf(5, 7);
        var y = 5 * view.LineHeight + view.LineHeight / 2;
        Assert.Equal(offset, view.OffsetAt(x, y));
    }

    [Fact]
    public void RevealScrollsTheCaretIntoSight()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(Lines(1000));
        var view = new TextView(doc, layout, EditOptions.Default);
        view.Resize(800, 400);
        view.Reveal(doc.Text.LineStart(500));
        Assert.InRange(500, view.FirstVisibleLine, view.LastVisibleLine);
        view.Reveal(0);
        Assert.Equal(0, view.ScrollY);
    }

    [Fact]
    public void AFrameHasTheLinesInSightTheSelectionsAndTheCarets()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(Lines(100));
        doc.SetSelections([new Selection(doc.Text.Offset(2, 0), doc.Text.Offset(3, 4)), Selection.Caret(doc.Text.Offset(6, 3))]);
        var view = new TextView(doc, layout, EditOptions.Default);
        view.Resize(800, 10 * view.LineHeight);
        var frame = view.BuildFrame();
        Assert.Equal(Enumerable.Range(0, 11), frame.Lines.Select(l => l.Line));
        Assert.Equal(2, frame.Selections.Count);        // the selection's two lines
        Assert.Equal(2, frame.Carets.Count);            // its active end and the lone caret
        Assert.True(frame.Lines.Single(l => l.Line == 6).Current);
        Assert.False(frame.VerticalThumb.IsEmpty);
    }

    [Fact]
    public async Task APaintedFrameShowsTheTextAndTheCaret()
    {
        using var layout = new LayoutService(_fonts);
        var ready = new SemaphoreSlim(0);
        layout.Ready += () => ready.Release();
        var doc = new Document("MMMMMMMM\nsecond line");
        doc.SetSelections([Selection.Caret(4)]);
        var view = new TextView(doc, layout, EditOptions.Default);
        view.Resize(600, 200);
        view.BuildFrame();
        Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var frame = view.BuildFrame();
        Assert.All(frame.Lines, l => Assert.NotNull(l.Shaped));

        using var surface = SKSurface.Create(new SKImageInfo(600, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        EditorPainter.Paint(surface.Canvas, frame);
        using var image = surface.Snapshot();
        using var pixels = image.PeekPixels();

        var theme = frame.Theme;
        Assert.Equal(new SKColor(theme.Background), pixels.GetPixelColor(590, 190));
        // Somewhere across the first line's glyphs the ink differs from the line's own background.
        var lineBackground = new SKColor(theme.CurrentLine);
        var y = (int)(frame.Fonts.Baseline - frame.Fonts.Ascent / 2);
        var inked = Enumerable.Range((int)frame.TextLeft, (int)(8 * frame.Fonts.CharAdvance)).Count(x => pixels.GetPixelColor(x, y) != lineBackground);
        Assert.True(inked > 10, $"only {inked} inked pixels on the first line");
        var caret = frame.Carets.Single();
        Assert.Equal(new SKColor(theme.Caret), pixels.GetPixelColor((int)caret.MidX, (int)caret.MidY));
    }
}
