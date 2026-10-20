using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor.Features;

public sealed class FoldingTests(ITestOutputHelper output)
{
    private static FoldingRanges Of(string text, FoldingMarkers? markers = null, int tab = 4) =>
        FoldingRanges.Compute(Rope.FromString(text), 5, tab, markers);

    private static FoldRange[] All(FoldingRanges r) => [.. Enumerable.Range(0, r.Count).Select(i => r[i])];

    [Fact]
    public void AHeaderFoldsUpToTheLastDeeperLine()
    {
        var r = Of("a\n    b\n    c\nd\n");
        Assert.Equal([new FoldRange(0, 2)], All(r));
        Assert.Equal(5, r.Version);
    }

    [Fact]
    public void AClosingBraceStaysVisible()
    {
        var r = Of("void M()\n{\n    x;\n    y;\n}\n");
        Assert.Equal([new FoldRange(1, 3)], All(r));
    }

    [Fact]
    public void RangesNest()
    {
        var r = Of("a\n    b\n        c\n        d\n    e\nf\n");
        Assert.Equal([new FoldRange(0, 4), new FoldRange(1, 3)], All(r));
        Assert.Equal(-1, r.Parent(0));
        Assert.Equal(0, r.Parent(1));
        Assert.Equal(1, r.Depth(1));
    }

    [Fact]
    public void BlankLinesDoNotEndOrExtendARange()
    {
        Assert.Equal([new FoldRange(0, 3)], All(Of("a\n    b\n\n    c\n\n\nd\n")));
        Assert.Equal([new FoldRange(0, 1)], All(Of("a\n    b\n\n\nc\n")));
    }

    [Fact]
    public void AHeaderWithNoDeeperLineDoesNotFold()
    {
        Assert.Empty(All(Of("a\nb\nc\n")));
        Assert.Empty(All(Of("")));
    }

    [Fact]
    public void ARangeRunsToTheEndOfTheFile()
    {
        Assert.Equal([new FoldRange(0, 2)], All(Of("a\n  b\n  c")));
    }

    [Fact]
    public void TabsAndTabSize()
    {
        Assert.Equal([new FoldRange(0, 1)], All(Of("a\n\tb\n")));
        Assert.Equal([new FoldRange(0, 2), new FoldRange(1, 2)], All(Of("a\n  b\n    c\n", tab: 2)));
    }

    [Fact]
    public void SiblingsAtTheSameDepthAreSeparateRanges()
    {
        Assert.Equal([new FoldRange(0, 1), new FoldRange(2, 4)], All(Of("a\n  1\nb\n  2\n  3\n")));
    }

    [Fact]
    public void RegionMarkersFoldIncludingTheirEndLine()
    {
        var r = Of("#region R\nint a;\nint b;\n#endregion\nint c;\n", FoldingMarkers.Default);
        Assert.Equal([new FoldRange(0, 3)], All(r));
    }

    [Fact]
    public void CommentFormsOfRegionMarkers()
    {
        foreach (var (s, e) in new[] { ("// #region x", "// #endregion"), ("//region x", "//endregion"), ("# region x", "# endregion") })
        {
            var r = Of($"{s}\nfoo\nbar\n{e}\nend\n", FoldingMarkers.Default);
            Assert.Equal([new FoldRange(0, 3)], All(r));
        }
    }

    [Fact]
    public void RegionsNestWithIndentation()
    {
        const string code = "class A\n{\n    #region r\n    void M()\n    {\n        x;\n    }\n    #endregion\n}\n";
        var r = Of(code, FoldingMarkers.Default);
        // class body 1..7, region 2..7, method body 4..5.
        Assert.Equal([new FoldRange(1, 7), new FoldRange(2, 7), new FoldRange(4, 5)], All(r));
    }

    [Fact]
    public void RangesNeverCross()
    {
        var r = Of("#region\nx\n  y\n#endregion\n    z\n", FoldingMarkers.Default);
        Assert.Equal(All(r).OrderBy(x => x.StartLine), All(r));
        for (var i = 0; i < r.Count; i++)
        {
            for (var j = i + 1; j < r.Count; j++)
            {
                Assert.True(r.StartLine(j) > r.EndLine(i) || r.EndLine(j) <= r.EndLine(i));
            }
        }
    }

    [Fact]
    public void MarkersComeFromTheLanguageConfiguration()
    {
        var cs = FoldingMarkers.For(Languages.ForFile("a.cs"));
        Assert.Matches(cs.Start, "  #region foo");
        Assert.DoesNotMatch(cs.Start, "// #region foo");
        var js = FoldingMarkers.For(Languages.ForFile("a.js"));
        Assert.Matches(js.Start, "// #region foo");
        Assert.Matches(js.End, "//endregion");
        Assert.Same(FoldingMarkers.Default, FoldingMarkers.For(null));
    }

    [Fact]
    public void LookupsByLine()
    {
        var r = Of("a\n  b\n    c\n  d\n");
        Assert.Equal(0, r.IndexStartingAt(0));
        Assert.Equal(-1, r.IndexStartingAt(3));
        Assert.Equal(1, r.FirstStartingAtOrAfter(1));
        Assert.Equal(r.Count, r.FirstStartingAtOrAfter(9));
        Assert.Equal(1, r.InnermostContaining(2));
        Assert.Equal(0, r.InnermostContaining(3));
        Assert.Equal(-1, FoldingRanges.Empty.InnermostContaining(0));
    }

    // ── FoldState ────────────────────────────────────────────────────────────────────────────────

    private const string Sample = "a\n  b1\n  b2\nc\nd\n  e1\n  e2\n  e3\nf\n"; // lines 0..8, + an empty line 9

    [Fact]
    public void NoFoldsMapLinesToThemselves()
    {
        var s = FoldState.None(10);
        Assert.Equal(7, s.VisualLine(7));
        Assert.Equal(7, s.DocumentLine(7));
        Assert.Equal(10, s.VisualLineCount);
        Assert.False(s.IsHidden(3));
    }

    [Fact]
    public void CollapsingHidesLinesAndShiftsTheRest()
    {
        var r = Of(Sample);
        var s = FoldState.None(10).Collapse(r[0]).Collapse(r[1]);
        // hidden: 1,2 and 5,6,7
        Assert.Equal([0, 3, 4, 8, 9], Enumerable.Range(0, s.VisualLineCount).Select(s.DocumentLine));
        Assert.Equal([0, 0, 0, 1, 2, 2, 2, 2, 3, 4], Enumerable.Range(0, 10).Select(s.VisualLine));
        Assert.Equal([false, true, true, false, false, true, true, true, false, false], Enumerable.Range(0, 10).Select(s.IsHidden));
        Assert.Equal(5, s.VisualLineCount);
        Assert.Equal(5, s.HiddenLines);
    }

    [Fact]
    public void RoundTripHoldsForEveryVisualLine()
    {
        var s = FoldState.None(10).CollapseAll(Of(Sample));
        for (var v = 0; v < s.VisualLineCount; v++) Assert.Equal(v, s.VisualLine(s.DocumentLine(v)));
    }

    [Fact]
    public void NestedCollapsedRangesShowAsTheOuter()
    {
        var r = Of("a\n  b\n    c\n    d\n  e\nf\n");
        var s = FoldState.None(7).Collapse(r[1]).Collapse(r[0]);
        Assert.Equal(3, s.VisualLineCount);
        Assert.Equal(2, s.Collapsed.Length);
        Assert.Equal(5, s.DocumentLine(1));
    }

    [Fact]
    public void ExpandRevealAndToggle()
    {
        var r = Of(Sample);
        var s = FoldState.None(10).CollapseAll(r);
        Assert.Equal(2, s.Collapsed.Length);
        Assert.Single(s.Expand(0).Collapsed);
        Assert.Single(s.Reveal(6).Collapsed);
        Assert.Equal(2, s.Reveal(3).Collapsed.Length);
        Assert.False(s.Toggle(r, 0).IsHidden(1));
        Assert.True(FoldState.None(10).Toggle(r, 0).IsHidden(1));
        Assert.Same(s, s.Toggle(r, 3)); // not a header
        Assert.False(s.ExpandAll().HasCollapsed);
    }

    [Fact]
    public void FoldAtCollapsesTheInnermostThenTheOuter()
    {
        var r = Of("a\n  b\n    c\n    d\n  e\nf\n");
        var s = FoldState.None(7).FoldAt(r, 2);
        Assert.Equal(new FoldRange(1, 3), s.Collapsed.Single());
        var t = s.FoldAt(r, 2);
        Assert.Equal(2, t.Collapsed.Length);
        Assert.Single(t.UnfoldAt(0).Collapsed);
    }

    [Fact]
    public void CollapseLevelPicksOneDepth()
    {
        var r = Of("a\n  b\n    c\n    d\n  e\nf\n  g\n");
        Assert.Equal([new FoldRange(0, 4), new FoldRange(5, 6)], FoldState.None(8).CollapseLevel(r, 1).Collapsed.ToArray());
        Assert.Equal([new FoldRange(1, 3)], FoldState.None(8).CollapseLevel(r, 2).Collapsed.ToArray());
    }

    private static (Document Doc, FoldState State, List<DocumentChange> Changes) Fold(string text, params int[] headers)
    {
        var doc = new Document(text);
        var ranges = FoldingRanges.Compute(doc.Text, doc.Version, 2, null);
        var state = FoldState.None(doc.Text.LineCount);
        foreach (var h in headers) state = state.Collapse(ranges[ranges.IndexStartingAt(h)]);
        var changes = new List<DocumentChange>();
        doc.Changed += changes.Add;
        return (doc, state, changes);
    }

    private static FoldState After(FoldState state, Document doc, List<DocumentChange> changes, params TextChange[] edits)
    {
        changes.Clear();
        doc.Apply(edits, [Selection.Caret(0)]);
        return state.Map(changes.Single());
    }

    [Fact]
    public void EditsAboveShiftTheFold()
    {
        var (doc, s, changes) = Fold("x\ny\na\n  b\n  c\nd\n", 2);
        s = After(s, doc, changes, new TextChange(0, 0, "new1\nnew2\n"));
        Assert.Equal(new FoldRange(4, 6), s.Collapsed.Single());
        Assert.Equal(doc.Text.LineCount, s.LineCount);
    }

    [Fact]
    public void ATypedLineInsideTheFoldGrowsIt()
    {
        var (doc, s, changes) = Fold("a\n  b\n  c\nd\n", 0);
        s = After(s, doc, changes, new TextChange(doc.Text.LineStart(2), 0, "  mid\n"));
        Assert.Equal(new FoldRange(0, 3), s.Collapsed.Single());
    }

    [Fact]
    public void EnterAtTheEndOfTheHeaderKeepsTheFoldOnTheHeader()
    {
        var (doc, s, changes) = Fold("a\n  b\n  c\nd\n", 0);
        s = After(s, doc, changes, new TextChange(1, 0, "\n"));
        Assert.Equal(0, s.Collapsed.Single().StartLine);
    }

    [Fact]
    public void EnterAtTheStartOfTheHeaderPushesItDown()
    {
        var (doc, s, changes) = Fold("a\n  b\n  c\nd\n", 0);
        s = After(s, doc, changes, new TextChange(0, 0, "\n"));
        Assert.Equal(new FoldRange(1, 3), s.Collapsed.Single());
    }

    [Fact]
    public void DeletingTheHeaderDropsTheFold()
    {
        var (doc, s, changes) = Fold("x\na\n  b\n  c\nd\n", 1);
        s = After(s, doc, changes, new TextChange(0, 3, ""));
        Assert.False(s.HasCollapsed);
    }

    [Fact]
    public void DeletingTheWholeBodyDropsTheFold()
    {
        var (doc, s, changes) = Fold("a\n  b\n  c\nd\n", 0);
        s = After(s, doc, changes, new TextChange(1, 8, ""));
        Assert.False(s.HasCollapsed);
    }

    [Fact]
    public void RebindKeepsFoldsThatStillStartARange()
    {
        var (doc, s, changes) = Fold("a\n  b\n  c\nd\n  e\n  f\n", 0, 3);
        s = After(s, doc, changes, new TextChange(doc.Text.LineStart(3), 0, "  more\n"));
        var fresh = FoldingRanges.Compute(doc.Text, doc.Version, 2, null);
        var rebound = s.Rebind(fresh);
        Assert.Equal(2, rebound.Collapsed.Length);
        Assert.Equal([new FoldRange(0, 3), new FoldRange(4, 6)], rebound.Collapsed.ToArray());
        // a fold whose header lost its deeper lines is dropped
        var flat = FoldingRanges.Compute(Rope.FromString("a\nb\nc\n"), 9, 2, null);
        Assert.False(s.Rebind(flat).HasCollapsed);
    }

    [Fact]
    public void FoldsSurviveManySequentialEdits()
    {
        var (doc, s, changes) = Fold("a\n  b\n  c\nd\ne\n", 0);
        for (var i = 0; i < 20; i++) s = After(s, doc, changes, new TextChange(doc.Text.Length, 0, "z\n"));
        Assert.Equal(new FoldRange(0, 2), s.Collapsed.Single());
        Assert.Equal(doc.Text.LineCount - 2, s.VisualLineCount);
    }

    [Fact]
    public void HundredThousandLines()
    {
        var rope = Rope.FromString(FeatureSupport.BigSource(100_000));
        FoldingRanges? r = null;
        var markers = FoldingMarkers.For(Languages.ForFile("a.cs"));
        var ms = FeatureSupport.TimeMs(() => r = FoldingRanges.Compute(rope, 1, 4, markers));
        var all = FoldState.None(rope.LineCount).CollapseAll(r!);
        var sum = 0;
        var lookups = FeatureSupport.TimeMs(() =>
        {
            for (var v = 0; v < 100_000; v++) sum += all.DocumentLine(v % all.VisualLineCount);
        });
        output.WriteLine($"FoldingRanges.Compute: {rope.LineCount} lines, {r!.Count} ranges, {ms:F1} ms; FoldState {all.Collapsed.Length} collapsed, 100000 DocumentLine lookups {lookups:F2} ms ({sum})");
        Assert.True(ms < 500);
    }
}
