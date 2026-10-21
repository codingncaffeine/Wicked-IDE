using Wicked.Editor.Features;
using Wicked.Editor.Model;
using static Wicked.Tests.Editor.Marked;

namespace Wicked.Tests.Editor;

/// <summary>The line and text tools: duplicate, insert a line, select lines, case, sort, reverse, join, carets at line ends, brackets.</summary>
public sealed class EditToolsTests
{
    private static string After(string marked, Func<Document, bool> act)
    {
        var doc = Doc(marked);
        act(doc);
        return Show(doc);
    }

    [Theory]
    [InlineData("a[bc]d", "abc[bc]d")]               // the copy follows the selection and is selected
    [InlineData("one|\ntwo", "one\none|\ntwo")]      // an empty selection copies its line below
    [InlineData("[a]-[b]", "a[a]-b[b]")]             // every selection
    public void DuplicateSelectionCopiesWhatIsSelected(string before, string expected) =>
        Assert.Equal(expected, After(before, Edits.DuplicateSelection));

    [Fact]
    public void InsertLineBelowAndAboveKeepTheIndentAndDoNotSplitTheLine()
    {
        Assert.Equal("    ab\n    |\nc", After("    a|b\nc", d => Edits.InsertLine(d, above: false)));
        Assert.Equal("x\n    |\n    ab", After("x\n    a|b", d => Edits.InsertLine(d, above: true)));
        Assert.Equal("a\n|\nb\n|", After("a|\nb|", d => Edits.InsertLine(d, above: false)));
    }

    [Fact]
    public void ExpandLineSelectionTakesTheLineThenTheNext()
    {
        var doc = Doc("one\nt|wo\nthree");
        Edits.SelectLines(doc);
        Assert.Equal("one\n[two\n]three", Show(doc));
        Edits.SelectLines(doc);
        Assert.Equal("one\n[two\nthree]", Show(doc));
    }

    [Theory]
    [InlineData("say [Hello World]", "upper", "say [HELLO WORLD]")]
    [InlineData("say [Hello World]", "lower", "say [hello world]")]
    [InlineData("[the QUICK fox-jumps]", "title", "[The Quick Fox-Jumps]")]
    [InlineData("a wo|rd here", "upper", "a WO|RD here")]          // an empty caret takes its word
    public void CaseChangesTheSelectionOrTheWord(string before, string how, string expected) =>
        Assert.Equal(expected, After(before, d => Edits.TransformCase(d, how switch
        {
            "upper" => t => t.ToUpperInvariant(),
            "lower" => t => t.ToLowerInvariant(),
            _ => Edits.TitleCase,
        })));

    [Fact]
    public void SortingAndReversingTakeTheSelectedLinesOrTheWholeFile()
    {
        Assert.Equal("x\n[apple\nbanana\ncherry]\na", After("x\n[cherry\napple\nbanana]\na", d => Edits.ReorderLines(d, Edits.SortAscending)));
        Assert.Equal("c|\nb\na", After("b|\na\nc", d => Edits.ReorderLines(d, Edits.SortDescending)));
        Assert.Equal("3|\n2\n1", After("1|\n2\n3", d => Edits.ReorderLines(d, Edits.Reverse)));
        // Already in order: nothing changes and nothing is added to undo.
        var doc = Doc("a|\nb");
        Assert.False(Edits.ReorderLines(doc, Edits.SortAscending));
    }

    [Fact]
    public void JoinLinesJoinsWithOneSpaceAndDropsTheIndent()
    {
        // A caret joins its line with the next and sits at the join; a selection joins its lines, the caret at the last join.
        Assert.Equal("if (x) { |return;\n}", After("if (x) {|\n    return;\n}", Edits.JoinLines));
        Assert.Equal("a b |c\nd", After("[a\n  b\n  c]\nd", Edits.JoinLines));
        var single = Doc("a|");
        Assert.False(Edits.JoinLines(single)); // the last line has nothing to join
        Assert.Equal("a|", Show(single));
    }

    [Fact]
    public void CaretsGoToTheEndOfEveryLineSelected()
    {
        Assert.Equal("one|\ntwo|\nth|ree", After("o[ne\ntwo\nth]ree", Edits.CaretsAtLineEnds));
        Assert.Equal("one|\ntwo|\nthree", After("[one\ntwo\n]three", Edits.CaretsAtLineEnds));
    }

    [Fact]
    public void ExpandSelectionGrowsByWordStringBracketsLineAndFile()
    {
        var doc = Doc("x\n    call(fo|o, \"bar baz\");\ny");
        var steps = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var brackets = BracketIndex.Build(doc.Text, doc.Version, null);
            if (SmartSelect.Expand(doc.Text, brackets, doc.Primary.Start, doc.Primary.End) is not { } r) break;
            doc.SetSelections([new Selection(r.Start, r.End)]);
            steps.Add(Show(doc));
        }

        Assert.Equal(
        [
            "x\n    call([foo], \"bar baz\");\ny",
            "x\n    call([foo, \"bar baz\"]);\ny",
            "x\n    call[(foo, \"bar baz\")];\ny",
            "x\n    [call(foo, \"bar baz\");]\ny",
            "x\n[    call(foo, \"bar baz\");\n]y",
            "[x\n    call(foo, \"bar baz\");\ny]",
        ], steps);

        // Inside a string: its contents, then the string with its quotes.
        var inString = Doc("say(\"bar b|az\")");
        var b = BracketIndex.Build(inString.Text, inString.Version, null);
        var word = SmartSelect.Expand(inString.Text, b, inString.Primary.Start, inString.Primary.End)!.Value;
        var contents = SmartSelect.Expand(inString.Text, b, word.Start, word.End)!.Value;
        var quoted = SmartSelect.Expand(inString.Text, b, contents.Start, contents.End)!.Value;
        Assert.Equal("bar baz", inString.Text.Substring(contents.Start, contents.End - contents.Start));
        Assert.Equal("\"bar baz\"", inString.Text.Substring(quoted.Start, quoted.End - quoted.Start));
    }

    [Theory]
    [InlineData("f(|a, b) {}", "f(a, b|) {}")]      // beside an opener: before its closer
    [InlineData("f(a, b|) {}", "f(|a, b) {}")]      // beside a closer: after its opener, so a second jump comes back
    [InlineData("{ x = |1; }", "{ x = 1; |}")]      // inside a pair: before its closer
    [InlineData("no |brackets", "no |brackets")]
    public void JumpToBracketGoesToThePartner(string before, string expected)
    {
        var doc = Doc(before);
        var brackets = BracketIndex.Build(doc.Text, doc.Version, null);
        var to = brackets.JumpTarget(doc.Primary.Active);
        if (to is { } t) doc.SetSelections([Selection.Caret(t)]);
        Assert.Equal(expected, Show(doc));
    }
}
