using Microsoft.Extensions.Time.Testing;
using Wicked.Editor.Model;
using static Wicked.Tests.Editor.Marked;

namespace Wicked.Tests.Editor;

public sealed class EditsTests
{
    private static readonly EditOptions O = EditOptions.Default;

    private static string After(string marked, Action<Document> act)
    {
        var doc = Doc(marked);
        act(doc);
        return Show(doc);
    }

    // ── Typing ──

    [Fact]
    public void TypingGoesInAtEveryCaretAndReplacesSelections() =>
        Assert.Equal("ax|b ax|c x|", After("a|b a[zz]c |", d => Edits.Type(d, "x", O)));

    [Theory]
    [InlineData("foo|", "(", "foo(|)")]
    [InlineData("foo(|)", ")", "foo()|")]
    [InlineData("foo|bar", "(", "foo(|bar")]          // no closer before a word
    [InlineData("say |", "\"", "say \"|\"")]
    [InlineData("it|", "'", "it'|")]                   // an apostrophe after a word stays alone
    [InlineData("[abc]", "(", "([abc])")]               // a selection is wrapped
    [InlineData("x = (|)", "{", "x = ({|})")]
    public void PairsCloseSkipAndWrap(string before, string typed, string expected) =>
        Assert.Equal(expected, After(before, d => Edits.Type(d, typed, O)));

    [Fact]
    public void EnterIndentsLikeTheLineAndSplitsABracePair()
    {
        Assert.Equal("    a\n    |b", After("    a|   b", d => Edits.NewLine(d, O)));
        Assert.Equal("if (x) {\n    |", After("if (x) {|", d => Edits.NewLine(d, O)));
        Assert.Equal("f({\n    |\n})", After("f({|})", d => Edits.NewLine(d, O)));
    }

    [Fact]
    public void TabGoesToTheNextStopOrIndentsLines()
    {
        Assert.Equal("ab  |", After("ab|", d => Edits.Tab(d, O)));
        Assert.Equal("[    a\n    b]\n", After("[a\nb]\n", d => Edits.Tab(d, O))); // a selection from column 0 keeps the indentation in it
        Assert.Equal("[a\nb]\n", After("    [a\n  b]\n", d => Edits.OutdentLines(d, O)));
    }

    // ── Deleting ──

    [Theory]
    [InlineData("(|)", "|")]
    [InlineData("ab|c", "a|c")]
    [InlineData("      |x", "    |x")]               // back to the previous tab stop in leading spaces
    [InlineData("a\U0001F600|", "a|")]                // a surrogate pair goes whole
    [InlineData("|abc", "|abc")]
    public void Backspace(string before, string expected) =>
        Assert.Equal(expected, After(before, d => Edits.Backspace(d, O)));

    [Fact]
    public void CaretsThatMeetBecomeOne()
    {
        var doc = Doc("ab|c|d");
        Edits.Backspace(doc, O);
        Assert.Equal("a|d", Show(doc));
        Assert.Single(doc.Selections);
    }

    [Fact]
    public void WordDeletes()
    {
        Assert.Equal("foo |", After("foo bar.baz|", d => { Edits.DeleteWordLeft(d); Edits.DeleteWordLeft(d); Edits.DeleteWordLeft(d); }));
        Assert.Equal("|.baz", After("|foo.baz", d => Edits.DeleteWordRight(d)));
    }

    // ── Undo ──

    [Fact]
    public void ARunOfTypingUndoesAsOneStepAndAPauseStartsAnother()
    {
        var time = new FakeTimeProvider();
        var doc = Doc("|", time);
        foreach (var c in "abc") Edits.Type(doc, c.ToString(), O);
        time.Advance(TimeSpan.FromSeconds(3));
        Edits.Type(doc, "d", O);
        Assert.Equal("abcd|", Show(doc));

        doc.Undo();
        Assert.Equal("abc|", Show(doc));
        doc.Undo();
        Assert.Equal("|", Show(doc));
        doc.Redo();
        Assert.Equal("abc|", Show(doc));
    }

    [Fact]
    public void MovingTheCaretEndsTheTypingGroup()
    {
        var doc = Doc("x|");
        Edits.Type(doc, "a", O);
        Edits.Left(doc, false);
        Edits.Right(doc, false);
        Edits.Type(doc, "b", O);
        doc.Undo();
        Assert.Equal("xa|", Show(doc));
    }

    [Fact]
    public void UndoingBackToTheSavePointIsClean()
    {
        var doc = Doc("|");
        Edits.Type(doc, "a", O);
        doc.MarkSaved();
        Assert.False(doc.IsDirty);
        doc.BreakUndoGroup();
        Edits.Type(doc, "b", O);
        Assert.True(doc.IsDirty);
        doc.Undo();
        Assert.False(doc.IsDirty);
        doc.Undo();
        Assert.True(doc.IsDirty);
        doc.Redo();
        Assert.False(doc.IsDirty);
    }

    // ── Moving ──

    [Fact]
    public void UpAndDownKeepTheColumnThroughShortLines()
    {
        var doc = Doc("abcde|f\nab\nabcdef");
        Edits.Down(doc, false, O);
        Assert.Equal("abcdef\nab|\nabcdef", Show(doc));
        Edits.Down(doc, false, O);
        Assert.Equal("abcdef\nab\nabcde|f", Show(doc));
    }

    [Fact]
    public void ColumnsCountTabStops()
    {
        // "\tx": x is at visual column 4; the caret below it lands after "abcd".
        var doc = Doc("\t|x\nabcdefgh");
        Edits.Down(doc, false, O);
        Assert.Equal("\tx\nabcd|efgh", Show(doc));
    }

    [Fact]
    public void SmartHomeAndEnd()
    {
        var doc = Doc("    fo|o");
        Edits.Home(doc, false);
        Assert.Equal("    |foo", Show(doc));
        Edits.Home(doc, false);
        Assert.Equal("|    foo", Show(doc));
        Edits.End(doc, true);
        Assert.Equal("[    foo]", Show(doc));
    }

    [Fact]
    public void WordMoves()
    {
        var doc = Doc("|foo.bar  baz");
        Edits.WordRight(doc, false);
        Assert.Equal("foo|.bar  baz", Show(doc));
        Edits.WordRight(doc, false);
        Assert.Equal("foo.|bar  baz", Show(doc));
        Edits.WordRight(doc, false);
        Edits.WordRight(doc, false);
        Assert.Equal("foo.bar  baz|", Show(doc));
        Edits.WordLeft(doc, true);
        Assert.Equal("foo.bar  ]baz[", Show(doc));
    }

    // ── More carets ──

    [Fact]
    public void CtrlDSelectsTheWordThenEachNextOccurrence()
    {
        var doc = Doc("f|oo bar foo baz foo");
        Edits.AddNextOccurrence(doc);
        Assert.Equal("[foo] bar foo baz foo", Show(doc));
        Edits.AddNextOccurrence(doc);
        Edits.AddNextOccurrence(doc);
        Assert.Equal("[foo] bar [foo] baz [foo]", Show(doc));
        Assert.False(Edits.AddNextOccurrence(doc));
        Edits.Type(doc, "qux", O);
        Assert.Equal("qux| bar qux| baz qux|", Show(doc));
    }

    [Fact]
    public void SelectAllOccurrences()
    {
        var doc = Doc("a1 b a1 c a|1");
        Edits.SelectAllOccurrences(doc);
        Assert.Equal("[a1] b [a1] c [a1]", Show(doc));
    }

    [Fact]
    public void CaretsAboveAndBelow()
    {
        var doc = Doc("abc\nab|c\nabc");
        Edits.AddCaretVertically(doc, 1, O);
        Edits.AddCaretVertically(doc, -1, O);
        Assert.Equal("ab|c\nab|c\nab|c", Show(doc));
    }

    // ── Lines ──

    [Fact]
    public void MovingLinesCarriesTheSelection()
    {
        Assert.Equal("b|b\naa\ncc", After("aa\nb|b\ncc", d => Edits.MoveLines(d, -1)));
        Assert.Equal("cc\n[aa\nbb]", After("[aa\nbb]\ncc", d => Edits.MoveLines(d, 1)));
        Assert.Equal("a|a\nbb", After("a|a\nbb", d => Edits.MoveLines(d, -1)));
    }

    [Fact]
    public void CopyingLinesDownSelectsTheCopy() =>
        Assert.Equal("ab\na|b\ncd", After("a|b\ncd", d => Edits.CopyLines(d, 1)));

    [Fact]
    public void DeletingLinesKeepsTheColumn()
    {
        Assert.Equal("one\nth|ree", After("one\ntw|o\nthree", d => Edits.DeleteLines(d, O)));
        Assert.Equal("one\ntwo|", After("one\ntwo\nthr|ee", d => Edits.DeleteLines(d, O)));
    }

    [Fact]
    public void LineCommentsGoAtTheShallowestIndentAndComeOff()
    {
        var doc = Doc("[    a\n        b]");
        Edits.ToggleLineComment(doc, O);
        Assert.Equal("[    // a\n    //     b]", Show(doc));
        Edits.ToggleLineComment(doc, O);
        Assert.Equal("[    a\n        b]", Show(doc));
    }

    // ── Clipboard ──

    [Fact]
    public void CopyWithNoSelectionTakesTheLineAndPastesAboveTheCaretsLine()
    {
        var doc = Doc("one\nt|wo");
        var (text, whole) = Edits.CopyText(doc);
        Assert.Equal(("two\n", true), (text, whole));
        Edits.Paste(doc, text, whole);
        Assert.Equal("one\ntwo\nt|wo", Show(doc));
    }

    [Fact]
    public void PasteSpreadsLinesAcrossAsManyCarets() =>
        Assert.Equal("a1| a2| a3|", After("a| a| a|", d => Edits.Paste(d, "1\n2\n3", false)));

    [Fact]
    public void AnAutoSaveTrimLeavesTheCaretsBlanks() =>
        Assert.Equal("a\nb  |\nc", After("a  \nb  |\nc\t", d => Edits.TrimTrailingWhitespace(d, keepAtCarets: true)));

    [Fact]
    public void ASaveTheUserAsksForTrimsEveryLine() =>
        Assert.Equal("a\nb|\nc", After("a  \nb  |\nc\t", d => Edits.TrimTrailingWhitespace(d)));

    [Fact]
    public void SavingWritesTheDocumentsLineBreaks()
    {
        var doc = new Document("a\r\nb\r\nc");
        Assert.Equal("a\nb\nc", doc.Text.ToString());
        Assert.Equal("a\r\nb\r\nc", Edits.ForSaving(doc));
    }
}
