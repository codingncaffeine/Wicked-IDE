using Wicked.Editor.Features;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor.Features;

public sealed class IndentGuidesTests(ITestOutputHelper output)
{
    private static IndentGuides Of(string text, int tab = 4) => IndentGuides.Compute(Rope.FromString(text), 3, tab);

    private static int[] Counts(IndentGuides g) => [.. Enumerable.Range(0, g.LineCount).Select(g.GuideCount)];

    [Fact]
    public void CountsOneGuideForEachIndentUnitStartedInside()
    {
        var g = Of("a\n    b\n        c\n      d\n  e\n");
        Assert.Equal([0, 1, 2, 2, 1, 0], Counts(g));
        Assert.Equal(3, g.Version);
    }

    [Fact]
    public void TabsCountAsTabStops()
    {
        var g = Of("a\n\tb\n\t\tc\n \td\n");
        Assert.Equal([0, 1, 2, 1, 0], Counts(g));
    }

    [Fact]
    public void TabSizeChangesTheCount()
    {
        Assert.Equal([0, 2, 0], Counts(Of("a\n    b\n", 2)));
        Assert.Equal([0, 1, 0], Counts(Of("a\n    b\n", 8)));
    }

    [Fact]
    public void ABlankLineInsideABlockKeepsTheGuides()
    {
        var g = Of("{\n    a\n\n    b\n}\n");
        Assert.Equal([0, 1, 1, 1, 0, 0], Counts(g));
    }

    [Fact]
    public void ABlankLineBetweenBlocksTakesTheSmallerNeighbour()
    {
        var g = Of("f\n        deep\n\n    shallow\n");
        Assert.Equal(1, g.GuideCount(2));
        var h = Of("x\n\n        deep\n");
        Assert.Equal(0, h.GuideCount(1));
    }

    [Fact]
    public void WhitespaceOnlyLinesAreBlank()
    {
        var g = Of("a\n    b\n      \n    c\n");
        Assert.Equal(1, g.GuideCount(2));
    }

    [Fact]
    public void ActiveGuideIsTheCaretsBlock()
    {
        var g = Of("class A\n{\n    void M()\n    {\n        x;\n        y;\n    }\n    int z;\n}\n");
        // caret in "x;" line 4: level 2, lines 4..5
        Assert.Equal(new ActiveGuide(2, 4, 5), g.ActiveFor(4));
        // caret on the line "void M()" (line 2, count 1; next line count 1): level 1, lines 2..7
        Assert.Equal(new ActiveGuide(1, 2, 7), g.ActiveFor(2));
        // caret on "{" at line 3 (count 1, next deeper): the block that follows, level 2, lines 4..5
        Assert.Equal(new ActiveGuide(2, 4, 5), g.ActiveFor(3));
        // caret on the header "{" of the class (line 1, count 0, next deeper): level 1 over the class body
        Assert.Equal(new ActiveGuide(1, 2, 7), g.ActiveFor(1));
        // caret on the closing "}" of the class: none
        Assert.Equal(ActiveGuide.None, g.ActiveFor(8));
        Assert.Equal(ActiveGuide.None, g.ActiveFor(99));
    }

    [Fact]
    public void CollectMarksOnlyTheActiveBlocksGuide()
    {
        var g = Of("{\n    a\n        b\n    c\n}\n");
        var guides = g.GuidesFor(0, 4, 2);
        Assert.Equal(
            [new IndentGuide(1, 1, false), new IndentGuide(2, 1, false), new IndentGuide(2, 2, true), new IndentGuide(3, 1, false)],
            guides);
    }

    [Fact]
    public void CollectClampsTheRange()
    {
        var g = Of("a\n    b\n");
        Assert.Single(g.GuidesFor(-5, 50, 1));
        Assert.Empty(g.GuidesFor(5, 9, 0));
    }

    [Fact]
    public void EmptyDocument()
    {
        var g = Of("");
        Assert.Equal(1, g.LineCount);
        Assert.Equal(ActiveGuide.None, g.ActiveFor(0));
    }

    [Fact]
    public void HundredThousandLines()
    {
        var rope = Rope.FromString(FeatureSupport.BigSource(100_000));
        IndentGuides? g = null;
        var ms = FeatureSupport.TimeMs(() => g = IndentGuides.Compute(rope, 1, 4));
        var active = FeatureSupport.TimeMs(() => g!.ActiveFor(50_000), 20);
        var list = new List<Wicked.Editor.Features.IndentGuide>(200);
        var visible = FeatureSupport.TimeMs(() => { list.Clear(); g!.Collect(50_000, 50_060, g.ActiveFor(50_005), list); }, 20);
        output.WriteLine($"IndentGuides.Compute: {rope.LineCount} lines, {ms:F1} ms; ActiveFor {active * 1000:F1} us; visible page {visible * 1000:F1} us");
        Assert.True(ms < 500);
    }
}
