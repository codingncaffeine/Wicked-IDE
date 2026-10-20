using Wicked.Editor.Model;
using Wicked.Editor.Search;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor;

public sealed class SearchTests
{
    private static IEnumerable<int> Starts(string text, SearchQuery q) => TextSearch.Find(Rope.FromString(text), 0, q, TestContext.Current.CancellationToken).Matches.Select(m => m.Start);

    [Fact]
    public void LiteralsCaseAndWholeWords()
    {
        const string text = "Foo foo food fOO";
        Assert.Equal([0, 4, 8, 13], Starts(text, new SearchQuery("foo")));
        Assert.Equal([4, 8], Starts(text, new SearchQuery("foo", MatchCase: true)));
        Assert.Equal([0, 4, 13], Starts(text, new SearchQuery("foo", WholeWord: true)));
    }

    [Fact]
    public void RegularExpressionsAcrossLinesAndWithGroups()
    {
        var rope = Rope.FromString("let a = 1;\nlet bb = 22;\n");
        var q = new SearchQuery(@"let (\w+) = (\d+)", Regex: true);
        var found = TextSearch.Find(rope, 7, q, TestContext.Current.CancellationToken);
        Assert.Equal(7, found.Version);
        Assert.Equal(2, found.Matches.Length);
        Assert.Equal("var bb: 22", TextSearch.Replacement(rope, found.Matches[1], q, "var $1: $2"));
    }

    [Fact]
    public void ABadPatternSaysWhyAndFindsNothing()
    {
        var found = TextSearch.Find(Rope.FromString("abc"), 0, new SearchQuery("(", Regex: true), TestContext.Current.CancellationToken);
        Assert.NotNull(found.Error);
        Assert.Empty(found.Matches);
    }

    [Fact]
    public void NextAndPreviousWrapAround()
    {
        var matches = TextSearch.Find(Rope.FromString("x . x . x"), 0, new SearchQuery("x"), TestContext.Current.CancellationToken).Matches;
        Assert.Equal(1, TextSearch.NextIndex(matches, 1));
        Assert.Equal(0, TextSearch.NextIndex(matches, 9));
        Assert.Equal(2, TextSearch.PreviousIndex(matches, 0));
        Assert.Equal(1, TextSearch.PreviousIndex(matches, 8));
    }

    [Fact]
    public void ReplaceAllIsOneUndoStep()
    {
        var doc = new Document("a1 a22 a333");
        var q = new SearchQuery(@"a(\d+)", Regex: true);
        var found = TextSearch.Find(doc.Text, doc.Version, q, TestContext.Current.CancellationToken);
        Assert.True(TextSearch.ReplaceAll(doc, found, q, "<$1>"));
        Assert.Equal("<1> <22> <333>", doc.Text.ToString());
        Assert.Equal(doc.Text.Length, doc.Primary.Active);
        doc.Undo();
        Assert.Equal("a1 a22 a333", doc.Text.ToString());
    }

    [Fact]
    public void AStaleResultIsNotReplaced()
    {
        var doc = new Document("aaa");
        var found = TextSearch.Find(doc.Text, doc.Version, new SearchQuery("a"), TestContext.Current.CancellationToken);
        Edits.Type(doc, "b", EditOptions.Default);
        Assert.False(TextSearch.ReplaceAll(doc, found, new SearchQuery("a"), "z"));
    }
}
