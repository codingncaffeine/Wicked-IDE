using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor.Features;

public sealed class BracketIndexTests(ITestOutputHelper output)
{
    private static BracketIndex Plain(string text, long version = 1) => BracketIndex.Build(Rope.FromString(text), version, null);

    [Fact]
    public void TokenTypeAccessorReadsTextMateTypes()
    {
        var tokens = new Wicked.Editor.Syntax.LineTokens(FeatureSupport.Tokens((0, 0, 1), (3, 1, 2), (6, 2, 3), (9, 3, 4)));
        Assert.Equal([0, 1, 2, 3], Enumerable.Range(0, 4).Select(tokens.TokenType));
        Assert.Equal([1, 2, 3, 4], Enumerable.Range(0, 4).Select(tokens.Foreground));
    }

    [Fact]
    public void PairsNestedBracketsWithDepth()
    {
        var b = Plain("a(b[c]{d})e");
        Assert.Equal(6, b.Count);
        Assert.Equal([0, 1, 1, 1, 1, 0], Enumerable.Range(0, 6).Select(b.Depth));
        Assert.Equal(5, b.Partner(0));
        Assert.Equal(0, b.Partner(5));
        Assert.Equal(2, b.Partner(1));
        Assert.Equal(4, b.Partner(3));
        Assert.Equal(1, b.Version);
    }

    [Fact]
    public void DepthGrowsWithNesting()
    {
        var b = Plain("(((x)))");
        Assert.Equal([0, 1, 2, 2, 1, 0], Enumerable.Range(0, 6).Select(b.Depth));
    }

    [Fact]
    public void UnmatchedBracketsHaveNoPartner()
    {
        var b = Plain("(]");
        Assert.False(b.IsMatched(0));
        Assert.False(b.IsMatched(1));
        var c = Plain(")(");
        Assert.False(c.IsMatched(0));
        Assert.False(c.IsMatched(1));
    }

    [Fact]
    public void ACloserMatchingAnOuterOpenerLeavesTheInnerOnesUnmatched()
    {
        var b = Plain("( [ )");
        Assert.Equal(2, b.Partner(0));
        Assert.False(b.IsMatched(1));
    }

    [Fact]
    public void MatchAtPrefersTheBracketBeforeTheCaret()
    {
        var b = Plain("(a)(b)");
        Assert.Equal(new BracketMatch(0, 2), b.MatchAt(1)); // caret at 1 is after '('
        Assert.Equal(new BracketMatch(0, 2), b.MatchAt(0));  // before '('
        Assert.Equal(new BracketMatch(0, 2), b.MatchAt(3));  // after ')': the one before wins over '(' after
        Assert.Equal(new BracketMatch(3, 5), b.MatchAt(4));
        Assert.Null(Plain("abc").MatchAt(1));
        Assert.Null(Plain("(a").MatchAt(0));
    }

    [Fact]
    public void EnclosingFindsTheInnermostPair()
    {
        var b = Plain("a(b[c]d)e");
        Assert.Equal(new BracketMatch(3, 5), b.Enclosing(4));
        Assert.Equal(new BracketMatch(1, 7), b.Enclosing(6));
        Assert.Equal(new BracketMatch(1, 7), b.Enclosing(2));
        Assert.Null(b.Enclosing(0));
        Assert.Null(b.Enclosing(9));
    }

    [Fact]
    public void FirstAtOrAfterIsABinarySearch()
    {
        var b = Plain("x(y)z");
        Assert.Equal(0, b.FirstAtOrAfter(0));
        Assert.Equal(0, b.FirstAtOrAfter(1));
        Assert.Equal(1, b.FirstAtOrAfter(2));
        Assert.Equal(2, b.FirstAtOrAfter(4));
        Assert.Equal(-1, b.IndexAt(0));
        Assert.Equal(1, b.IndexAt(3));
    }

    [Fact]
    public void QuotePairsAreNotBrackets()
    {
        var b = BracketIndex.Build(Rope.FromString("\"(\"'['"), 0, null, EditOptions.Default.Pairs);
        Assert.Equal(2, b.Count);
    }

    [Fact]
    public void CustomPairsAreHonoured()
    {
        var b = BracketIndex.Build(Rope.FromString("<a>(b)"), 0, null, [new BracketPair("<", ">")]);
        Assert.Equal(2, b.Count);
        Assert.Equal(1, b.Partner(0));
    }

    [Fact]
    public void BracketsInCommentsAndStringsAreSkippedWithRealHighlighting()
    {
        const string code = "class A\n{\n    void M() { var s = \"}\"; /* ) */ }\n    // (\n}\n";
        var syntax = FeatureSupport.Highlight(code, "a.cs");
        var b = BracketIndex.Build(Rope.FromString(code), 0, syntax, Wicked.Editor.Syntax.Languages.ForFile("a.cs")!.Options.Pairs);
        // { ( ) { } } : six real brackets; the quoted }, the commented ), and the commented ( are ignored.
        Assert.Equal(["{", "(", ")", "{", "}", "}"], Enumerable.Range(0, b.Count).Select(i => b.Char(i).ToString()));
        Assert.All(Enumerable.Range(0, b.Count), i => Assert.True(b.IsMatched(i)));
    }

    [Fact]
    public void SyntheticTokenTypesSkipBrackets()
    {
        var syntax = FeatureSupport.Synthetic(1, _ => FeatureSupport.Tokens((0, 0, 1), (2, 2, 2), (5, 0, 1)));
        // "( (x) )": the string token covers columns 2..4 "(x)"
        var b = BracketIndex.Build(Rope.FromString("( (x) )"), 0, syntax);
        Assert.Equal(2, b.Count);
    }

    [Fact]
    public void LinesBeyondTheValidOnesCountAsCode()
    {
        var syntax = FeatureSupport.Synthetic(2, _ => FeatureSupport.Tokens((0, 2, 1))) with { ValidLines = 1 };
        var b = BracketIndex.Build(Rope.FromString("(\n)"), 0, syntax);
        Assert.Equal(1, b.Count);
    }

    [Fact]
    public void WorksAcrossLines()
    {
        var b = Plain("f(\n  {\n  }\n)\n");
        Assert.Equal(4, b.Count);
        Assert.Equal(3, b.Partner(0));
        Assert.Equal(new BracketMatch(1, 11), b.MatchAt(2));
    }

    [Fact]
    public void EmptyAndEmptyIndex()
    {
        Assert.Equal(0, Plain("").Count);
        Assert.Null(BracketIndex.Empty.MatchAt(0));
        Assert.Equal(0, BracketIndex.Empty.FirstAtOrAfter(5));
    }

    [Fact]
    public void HundredThousandLinesBuildQuickly()
    {
        var rope = Rope.FromString(FeatureSupport.BigSource(100_000));
        var syntax = FeatureSupport.Synthetic(rope.LineCount, _ => FeatureSupport.Tokens((0, 0, 1)));
        BracketIndex? built = null;
        var ms = FeatureSupport.TimeMs(() => built = BracketIndex.Build(rope, 1, syntax));
        output.WriteLine($"BracketIndex.Build: {rope.LineCount} lines, {built!.Count} brackets, {ms:F1} ms");
        Assert.True(built.Count > 100_000);
        Assert.True(ms < 500, $"{ms} ms");
    }
}
