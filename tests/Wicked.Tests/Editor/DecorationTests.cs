using SkiaSharp;
using Wicked.Editor.Features;
using Wicked.Editor.Layout;
using Wicked.Editor.Model;
using Wicked.Editor.View;

namespace Wicked.Tests.Editor;

/// <summary>Marks providers put on a document: carried through edits, found in sight, drawn where their text is.</summary>
public sealed class DecorationTests(FontsFixture fixture) : IClassFixture<FontsFixture>
{
    private readonly EditorFonts _fonts = fixture.Fonts;

    private static DocumentChange Edit(Document doc, int at, int remove, string insert)
    {
        DocumentChange? seen = null;
        void Take(DocumentChange c) => seen = c;
        doc.Changed += Take;
        doc.Apply([new TextChange(at, remove, insert)], [Selection.Caret(at + insert.Length)]);
        doc.Changed -= Take;
        return seen!;
    }

    [Fact]
    public void ARangeFollowsItsTextAndGrowsWhenTypedInside()
    {
        var doc = new Document("let value = 1;");
        var set = DecorationSet.OfRanges([new RangeDecoration(4, 9, RangeStyle.Error)]); // "value"
        set = set.Map(Edit(doc, 0, 0, "    "));
        Assert.Equal((8, 13), (set.Ranges[0].Start, set.Ranges[0].End));
        set = set.Map(Edit(doc, 10, 0, "XX"));                                               // inside the word
        Assert.Equal((8, 15), (set.Ranges[0].Start, set.Ranges[0].End));
        set = set.Map(Edit(doc, 8, 0, "q"));                                                 // at its start: stays out
        Assert.Equal((9, 16), (set.Ranges[0].Start, set.Ranges[0].End));
        set = set.Map(Edit(doc, 16, 0, "z"));                                                // at its end: joins it
        Assert.Equal((9, 17), (set.Ranges[0].Start, set.Ranges[0].End));
        set = set.Map(Edit(doc, 5, 15, ""));                                                 // swallowed whole
        Assert.Equal((5, 5), (set.Ranges[0].Start, set.Ranges[0].End));
    }

    [Fact]
    public void ALineMarkRidesWithItsLine()
    {
        var doc = new Document("one\ntwo\nthree");
        var set = DecorationSet.OfGutter([new GutterDecoration(DecorationSet.LineKey(doc.Text, 1), GutterGlyph.Breakpoint)]);
        set = set.Map(Edit(doc, 4, 0, "\n"));             // Enter at the start of "two"
        Assert.Equal(2, doc.Text.LineOf(set.Gutter[0].LineStart));
        set = set.Map(Edit(doc, 5, 0, "  "));             // typing at its start keeps it there
        Assert.Equal(2, doc.Text.LineOf(set.Gutter[0].LineStart));
        Assert.Equal(doc.Text.LineStart(2), set.Gutter[0].LineStart);
        set = set.Map(Edit(doc, 4, 1, ""));               // the empty line above removed
        Assert.Equal(1, doc.Text.LineOf(set.Gutter[0].LineStart));
        Assert.Equal(doc.Text.LineStart(1), set.Gutter[0].LineStart);
    }

    [Fact]
    public void RangesInAWindowAreFoundIncludingOnesThatStartBeforeIt()
    {
        var ranges = Enumerable.Range(0, 1000).Select(i => new RangeDecoration(i * 10, i * 10 + 3, RangeStyle.Warning)).Append(new RangeDecoration(5, 5000, RangeStyle.Info)).ToList();
        var set = DecorationSet.OfRanges(ranges);
        var found = set.RangesIn(4002, 4025).ToList();
        Assert.Equal([new RangeDecoration(5, 5000, RangeStyle.Info), new RangeDecoration(4000, 4003, RangeStyle.Warning), new RangeDecoration(4010, 4013, RangeStyle.Warning), new RangeDecoration(4020, 4023, RangeStyle.Warning)], found);
    }

    [Fact]
    public void ASetWorkedOutForAnOlderVersionLandsOnItsTextAfterTheEditsSince()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document("alpha beta gamma");
        var view = new TextView(doc, layout, EditOptions.Default);
        var asked = doc.Version;
        Edit(doc, 0, 0, ">> ");
        Edit(doc, 3, 0, "!");
        Assert.True(view.SetDecorations("diagnostics", DecorationSet.OfRanges([new RangeDecoration(6, 10, RangeStyle.Error)]), asked)); // "beta" then
        var r = view.Decorations["diagnostics"].Ranges.Single();
        Assert.Equal("beta", doc.Text.Substring(r.Start, r.End - r.Start));

        // Too old to carry (the edits are not kept any more) or from the future: refused.
        Assert.False(view.SetDecorations("diagnostics", DecorationSet.Empty, doc.Version + 1));
        for (var i = 0; i < 300; i++) Edit(doc, 0, 0, "x");
        Assert.False(view.SetDecorations("diagnostics", DecorationSet.Empty, asked));

        // Kept marks still follow later edits.
        Edit(doc, 0, 0, "yy");
        r = view.Decorations["diagnostics"].Ranges.Single();
        Assert.Equal("beta", doc.Text.Substring(r.Start, r.End - r.Start));
    }

    [Fact]
    public async Task AnErrorIsDrawnAsAWaveUnderItsTextAndATickInTheScrollbar()
    {
        using var layout = new LayoutService(_fonts);
        var ready = new SemaphoreSlim(0);
        layout.Ready += () => ready.Release();
        var doc = new Document("first line\nsecond broken line\nthird");
        doc.SetSelections([Selection.Caret(0)]);
        var view = new TextView(doc, layout, EditOptions.Default) { ShowMinimap = false };
        view.Resize(600, 200);
        view.BuildFrame();
        Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var broken = doc.Text.LineStart(1) + "second ".Length;
        view.SetDecorations("diagnostics", DecorationSet.OfRanges([new RangeDecoration(broken, broken + "broken".Length, RangeStyle.Error)]), doc.Version);
        var frame = view.BuildFrame();
        var (rect, style) = frame.RangeMarks.Single();
        Assert.Equal(RangeStyle.Error, style);
        Assert.Equal(frame.TextLeft + view.XOf(1, 7), rect.Left, 0.5f);
        Assert.Equal(view.LineTop(1), rect.Top, 0.5f);
        Assert.Single(frame.Overview);

        using var surface = SKSurface.Create(new SKImageInfo(600, 200, SKColorType.Bgra8888, SKAlphaType.Premul));
        EditorPainter.Paint(surface.Canvas, frame);
        using var image = surface.Snapshot();
        using var pixels = image.PeekPixels();
        bool Reddish(SKColor c) => c.Red > 150 && c.Green < 120 && c.Blue < 120;
        int RedIn(float left, float right, float top, float bottom)
        {
            var n = 0;
            for (var x = (int)left; x < (int)right; x++)
            {
                for (var y = (int)top; y < (int)bottom; y++)
                {
                    if (Reddish(pixels.GetPixelColor(x, y))) n++;
                }
            }

            return n;
        }

        Assert.True(RedIn(rect.Left, rect.Right, rect.Bottom - 5, rect.Bottom) > 10, "no wave under the word");
        Assert.Equal(0, RedIn(frame.TextLeft, 590, view.LineTop(0), view.LineTop(1)));          // the line above is clean
        Assert.True(RedIn(590, 598, 0, 200) > 0, "no tick in the scrollbar");
    }

    [Fact]
    public void GutterGlyphsAndLineFillsAreOnTheirRows()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(string.Join('\n', Enumerable.Range(0, 50).Select(i => $"line {i}")));
        var view = new TextView(doc, layout, EditOptions.Default) { ShowMinimap = false, ShowGlyphMargin = true };
        view.Resize(600, 300);
        var plainGutter = new TextView(doc, layout, EditOptions.Default).GutterWidth;
        Assert.Equal(plainGutter + TextView.GlyphMarginWidth, view.GutterWidth);
        var key = (int line) => DecorationSet.LineKey(doc.Text, line);
        view.SetDecorations("debug", new DecorationSet([], [new LineDecoration(key(3), LineStyle.Stopped)], [new GutterDecoration(key(3), GutterGlyph.Stopped), new GutterDecoration(key(5), GutterGlyph.Breakpoint)], []), doc.Version);
        view.SetDecorations("git", DecorationSet.OfGutter([new GutterDecoration(key(5), GutterGlyph.Modified), new GutterDecoration(key(40), GutterGlyph.Added)]), doc.Version);
        var frame = view.BuildFrame();
        Assert.Equal([(view.LineTop(3), LineStyle.Stopped)], frame.LineFills);
        Assert.Contains((view.LineTop(3), GutterGlyph.Stopped), frame.GutterMarks);
        Assert.Contains((view.LineTop(5), GutterGlyph.Breakpoint), frame.GutterMarks);
        Assert.Contains((view.LineTop(5), GutterGlyph.Modified), frame.GutterMarks);
        Assert.DoesNotContain(frame.GutterMarks, g => g.Glyph == GutterGlyph.Added);              // line 40 is out of sight
        Assert.Equal(3, frame.Overview.Count);                                                      // ...but on the ruler, with the others
    }

    [Fact]
    public async Task SemanticColoursAreDrawnKeptThroughEditsElsewhereAndDroppedFromAnEditedLine()
    {
        using var layout = new LayoutService(_fonts);
        var ready = new SemaphoreSlim(0);
        layout.Ready += () => ready.Release();
        var doc = new Document("alpha beta\ngamma delta");
        var view = new TextView(doc, layout, EditOptions.Default) { ShowMinimap = false };
        view.Resize(600, 200);
        const uint Pink = 0xFFFF33AA;
        var lines = new Dictionary<int, SemanticLine>
        {
            [0] = new("alpha beta", [6], [10], [Pink]),
            [1] = new("gamma delta", [0], [5], [Pink]),
        };
        view.Semantic = new SemanticColors(doc.Version, doc.Text, lines);

        async Task<IReadOnlyList<FrameLine>> Frame()
        {
            view.BuildFrame();
            for (var i = 0; i < 20 && view.BuildFrame().Lines.Any(l => l.Shaped is null); i++) await ready.WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            return view.BuildFrame().Lines;
        }

        bool PinkOn(IReadOnlyList<FrameLine> frame, string text) => frame.Single(l => l.Text == text).Shaped!.Runs.Any(r => r.Color == Pink);
        var frame = await Frame();
        Assert.True(PinkOn(frame, "alpha beta"));
        Assert.True(PinkOn(frame, "gamma delta"));

        // A new line above both: they move down and keep their colours.
        doc.Apply([new TextChange(0, 0, "top\n")], [Selection.Caret(4)]);
        frame = await Frame();
        Assert.True(PinkOn(frame, "alpha beta"));
        Assert.True(PinkOn(frame, "gamma delta"));
        Assert.False(PinkOn(frame, "top"));

        // An edited line shows the grammar's colours until fresh tokens come.
        doc.Apply([new TextChange(doc.Text.LineStart(2), 0, "x")], [Selection.Caret(0)]);
        frame = await Frame();
        Assert.False(PinkOn(frame, "xgamma delta"));
        Assert.True(PinkOn(frame, "alpha beta"));
    }
}
