using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Tests.Editor.Features;

public sealed class WhitespaceMarksTests(ITestOutputHelper output)
{
    private static int[] Cols(string line, WhitespaceMode mode, params (int, int)[] selected)
    {
        var list = new List<WhitespaceMark>();
        var n = WhitespaceMarks.Collect(line, mode, selected, list);
        Assert.Equal(n, list.Count);
        return [.. list.Select(m => m.Column)];
    }

    [Fact]
    public void NoneMarksNothing() => Assert.Empty(Cols("  a b \t", WhitespaceMode.None));

    [Fact]
    public void AllMarksEverySpaceAndTab()
    {
        Assert.Equal([0, 1, 3, 5], Cols("  a b ", WhitespaceMode.All));
        var list = new List<WhitespaceMark>();
        WhitespaceMarks.Collect("\t x", WhitespaceMode.All, [], list);
        Assert.Equal([new WhitespaceMark(0, true), new WhitespaceMark(1, false)], list);
    }

    [Fact]
    public void BoundarySkipsOnlyASingleSpaceBetweenWords()
    {
        Assert.Equal([], Cols("a b c", WhitespaceMode.Boundary));
        Assert.Equal([1, 2], Cols("a  b", WhitespaceMode.Boundary));
        Assert.Equal([1], Cols("a\tb", WhitespaceMode.Boundary));
        Assert.Equal([0], Cols(" a", WhitespaceMode.Boundary));      // leading single space
        Assert.Equal([1], Cols("a ", WhitespaceMode.Boundary));      // trailing single space
        Assert.Equal([0, 1, 2, 3], Cols("    a", WhitespaceMode.Boundary));
        Assert.Equal([0], Cols(" ", WhitespaceMode.Boundary));
    }

    [Fact]
    public void TrailingMarksOnlyTheEndOfTheLine()
    {
        Assert.Equal([3, 4], Cols("a b  ", WhitespaceMode.Trailing));
        Assert.Equal([1], Cols("a\t", WhitespaceMode.Trailing));
        Assert.Empty(Cols("a b", WhitespaceMode.Trailing));
        Assert.Equal([0, 1, 2], Cols("   ", WhitespaceMode.Trailing));
    }

    [Fact]
    public void SelectionMarksWhitespaceInsideTheSelectedRanges()
    {
        Assert.Equal([1, 3], Cols("a b c d", WhitespaceMode.Selection, (0, 4)));
        Assert.Equal([3], Cols("a b c d", WhitespaceMode.Selection, (2, 4)));
        Assert.Equal([1, 5], Cols("a b c d", WhitespaceMode.Selection, (0, 2), (4, 7)));
        Assert.Empty(Cols("a b c d", WhitespaceMode.Selection));
        Assert.Equal([1, 2, 3], Cols("a    b", WhitespaceMode.Selection, (0, 4)));
    }

    [Fact]
    public void SelectedRangesForALine()
    {
        var into = new List<(int Start, int End)>();
        // a document where this line spans offsets 10..20
        WhitespaceMarks.SelectedRanges([new Selection(5, 14), new Selection(18, 30), new Selection(12, 12), new Selection(0, 3)], 10, 10, into);
        Assert.Equal([(0, 4), (8, 10)], into);
        WhitespaceMarks.SelectedRanges([new Selection(20, 25)], 10, 10, into);
        Assert.Empty(into);
    }

    [Fact]
    public void EmptyAndWhitespaceFreeLines()
    {
        Assert.Empty(Cols("", WhitespaceMode.All));
        Assert.Empty(Cols("abc", WhitespaceMode.All));
    }

    [Fact]
    public void IgnoresOtherWhitespace()
    {
        Assert.Empty(Cols("a b", WhitespaceMode.All));
    }

    [Fact]
    public void ReusingTheListAppends()
    {
        var list = new List<WhitespaceMark>();
        WhitespaceMarks.Collect("a ", WhitespaceMode.All, [], list);
        WhitespaceMarks.Collect("b ", WhitespaceMode.All, [], list);
        Assert.Equal(2, list.Count);
    }

    [Fact]
    public void ManyLinesAreFast()
    {
        var lines = Enumerable.Range(0, 100_000).Select(i => "    var x = foo(a,  b);  ").ToArray();
        var list = new List<WhitespaceMark>(64);
        var ms = FeatureSupport.TimeMs(() =>
        {
            foreach (var l in lines)
            {
                list.Clear();
                WhitespaceMarks.Collect(l, WhitespaceMode.Boundary, [], list);
            }
        });
        output.WriteLine($"WhitespaceMarks.Collect: 100000 lines (boundary) {ms:F1} ms");
        Assert.True(ms < 1000);
    }
}
