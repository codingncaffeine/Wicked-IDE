using Wicked.App.Debug;
using Wicked.Core.Debug;
using Wicked.Editor.Model;

namespace Wicked.Tests.Debug;

/// <summary>What the debugger's views are made of, without a window: breakpoints through edits, the console's lines, the variables tree.</summary>
public sealed class DebugModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Breakpoints through edits ────────────────────────────────────────────────────────────────

    /// <summary>A document with a breakpoint on each of the given lines (1-based); edits apply to it and the breakpoints are carried through each.</summary>
    private sealed class Edited
    {
        private readonly BreakpointStore _store = new();

        public Edited(string text, params int[] lines)
        {
            Document = new Document(text);
            foreach (var line in lines) _store.Add("/work/a.cs", line);
            Document.Changed += change => _store.Relocate("/work/a.cs", DebugController.MoveBreakpoints(_store.ForFile("/work/a.cs"), change));
        }

        public Document Document { get; }

        public int[] Lines => [.. _store.ForFile("/work/a.cs").Select(b => b.Line)];

        public void Replace(int start, int length, string text) => Document.Write([new TextChange(start, length, text)], [Selection.Caret(start)]);

        public int StartOf(int line) => Document.Text.LineStart(line - 1);
    }

    private const string Text = "one\ntwo\nthree\nfour\nfive";

    [Fact]
    public void AnEditAboveCarriesTheBreakpointsDownAndADeletionAboveBringsThemBack()
    {
        var e = new Edited(Text, 3, 5);

        e.Replace(0, 0, "zero\n");
        Assert.Equal([4, 6], e.Lines);

        e.Replace(0, 9, ""); // "zero\none\n" is gone
        Assert.Equal([2, 4], e.Lines);
    }

    [Fact]
    public void TypingInsideOrAfterABreakpointsLineLeavesItWhereItIs()
    {
        var e = new Edited(Text, 3);

        e.Replace(e.StartOf(3) + 2, 0, "XX");
        e.Replace(e.StartOf(3) + 7, 0, " tail");
        e.Replace(e.StartOf(4), 0, "new line\n");

        Assert.Equal([3], e.Lines);
        Assert.Equal("thXXree tail", e.Document.Text.Line(2));
    }

    [Fact]
    public void PressingEnterAtTheStartOfABreakpointsLineCarriesItWithItsCode()
    {
        var e = new Edited(Text, 3);

        e.Replace(e.StartOf(3), 0, "\n");

        Assert.Equal([4], e.Lines);
        Assert.Equal("three", e.Document.Text.Line(3));
    }

    [Fact]
    public void DeletingABreakpointsOwnLineMovesItOntoTheLineThatTookItsPlaceAndJoiningLinesMovesItUp()
    {
        var e = new Edited(Text, 3);
        e.Replace(e.StartOf(3), "three\n".Length, "");
        Assert.Equal([3], e.Lines); // "four" now stands where "three" was

        var j = new Edited(Text, 3);
        j.Replace(j.StartOf(3) - 1, 1, ""); // the line break before "three" removed: "twothree"
        Assert.Equal([2], j.Lines);
    }

    [Fact]
    public void UndoPutsABreakpointBackWhereItWas()
    {
        var e = new Edited(Text, 4);

        e.Replace(0, 4, ""); // "one\n" deleted
        Assert.Equal([3], e.Lines);
        e.Document.Undo();

        Assert.Equal([4], e.Lines);
    }

    [Fact]
    public void ReplacingARangeThatHoldsTwoBreakpointsKeepsOneOfThem()
    {
        var e = new Edited(Text, 2, 3);

        e.Replace(e.StartOf(2), "two\nthree\n".Length, "merged\n");

        Assert.Single(e.Lines);
        Assert.Equal(2, e.Lines[0]);
    }

    // ── The console ──────────────────────────────────────────────────────────────────────────────

    private static DebugConsoleViewModel Console(Func<string, Task>? evaluate = null) => new(a => a(), evaluate);

    [Fact]
    public void TextWithoutANewlineIsCompletedByTheNextPieceOfTheSameKindAndOtherKindsStartNewLines()
    {
        var console = Console();

        console.Write("stdout", "Hel");
        console.Flush();
        console.Write("stdout", "lo, wor");
        console.Write("stdout", "ld\nsecond\n");
        console.Write("stderr", "oops");
        console.Write("stdout", "third\n");
        console.Flush();

        Assert.Equal(["Hello, world", "second", "oops", "third"], console.Lines.Select(l => l.Text));
        Assert.Equal(["stdout", "stdout", "stderr", "stdout"], console.Lines.Select(l => l.Category));
        Assert.True(console.Lines[2].IsError);
    }

    [Fact]
    public void ManyLinesKeepTheNewestAndAnEmptyWriteIsNothing()
    {
        var console = Console();

        console.Write("stdout", "");
        for (var i = 0; i < DebugConsoleViewModel.LineLimit + 1500; i++) console.Write("stdout", $"line {i}\n");
        console.Flush();

        Assert.True(console.Lines.Count <= DebugConsoleViewModel.LineLimit + 500);
        Assert.Equal($"line {DebugConsoleViewModel.LineLimit + 1499}", console.Lines[^1].Text);
        Assert.DoesNotContain(console.Lines, l => l.Text == "line 0");
    }

    [Fact]
    public async Task SubmittingEchoesTheExpressionEvaluatesItAndUpAndDownWalkTheHistory()
    {
        var evaluated = new List<string>();
        var console = Console(text =>
        {
            evaluated.Add(text);
            return Task.CompletedTask;
        });

        foreach (var text in new[] { "first", "second", "second", "third" })
        {
            console.Input = text;
            await console.SubmitAsync();
        }

        Assert.Equal(["first", "second", "second", "third"], evaluated);
        Assert.Equal("", console.Input);
        Assert.Equal(["first", "second", "third"], console.Lines.Where(l => l.IsInput).Select(l => l.Text).Distinct());
        console.Recall(-1);
        Assert.Equal("third", console.Input);
        console.Recall(-1);
        Assert.Equal("second", console.Input);
        console.Recall(-1);
        console.Recall(-1);
        Assert.Equal("first", console.Input);
        console.Recall(1);
        console.Recall(1);
        console.Recall(1);
        Assert.Equal("", console.Input);
    }

    [Fact]
    public async Task ABlankBoxIsNotSubmitted()
    {
        var calls = 0;
        var console = Console(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });
        console.Input = "   ";

        await console.SubmitAsync();

        Assert.Equal(0, calls);
        Assert.Empty(console.Lines);
    }

    // ── The variables tree ───────────────────────────────────────────────────────────────────────

    private static DebugNode Scope(string name, int reference) => new(DebugNodeKind.Scope, name) { Reference = reference };

    private static DebugNode Var(string name, string value, int reference = 0) => new(DebugNodeKind.Variable, name) { Value = value, Reference = reference };

    [Fact]
    public async Task OpeningARowFetchesItsChildrenOnceAndClosingTakesThemOut()
    {
        var loads = new List<string>();
        var tree = new DebugTree(node =>
        {
            loads.Add(node.Name);
            return Task.FromResult(node.Name == "Locals" ? new List<DebugNode> { Var("a", "1"), Var("obj", "{}", 5) } : [Var("x", "9")]);
        });
        await tree.SetRootsAsync([Scope("Locals", 1)]);
        var locals = tree.Rows[0];

        await tree.OpenAsync(locals);
        await tree.OpenAsync(tree.Rows.Single(r => r.Name == "obj"));
        Assert.Equal(["Locals", "a", "obj", "x"], tree.Rows.Select(r => r.Name));
        Assert.Equal([0, 1, 1, 2], tree.Rows.Select(r => r.Depth));

        tree.Close(locals);
        Assert.Equal(["Locals"], tree.Rows.Select(r => r.Name));
        await tree.OpenAsync(locals);
        Assert.Equal(["Locals", "obj"], loads.Where(l => l != "a").Take(2));
        Assert.Equal(2, loads.Count); // the second opening used what was fetched
    }

    [Fact]
    public async Task RowsThatWereOpenStayOpenAfterTheNextStopAndChangedValuesAreMarked()
    {
        var value = "1";
        var tree = new DebugTree(node => Task.FromResult(node.Name == "Locals" ? new List<DebugNode> { Var("n", value), Var("obj", "{}", 5), Var("same", "k") } : [Var("inner", value)]));
        await tree.SetRootsAsync([Scope("Locals", 1)], openFirstScope: true);
        await tree.OpenAsync(tree.Rows.Single(r => r.Name == "obj"));
        Assert.Equal(["Locals", "n", "obj", "inner", "same"], tree.Rows.Select(r => r.Name));

        value = "2";
        await tree.SetRootsAsync([Scope("Locals", 1)], openFirstScope: true);

        Assert.Equal(["Locals", "n", "obj", "inner", "same"], tree.Rows.Select(r => r.Name));
        Assert.True(tree.Rows.Single(r => r.Name == "n").Changed);
        Assert.True(tree.Rows.Single(r => r.Name == "inner").Changed);
        Assert.False(tree.Rows.Single(r => r.Name == "same").Changed);
    }

    [Fact]
    public async Task AValueThatWasNotThereBeforeIsNotMarkedAndAFailedFetchShowsTheAdaptersWords()
    {
        var tree = new DebugTree(node => throw new DapException("cannot read memory"));
        await tree.SetRootsAsync([new DebugNode(DebugNodeKind.Watch, "x") { Value = "5" }]);
        await tree.SetRootsAsync([new DebugNode(DebugNodeKind.Watch, "x") { Value = "5" }]);
        Assert.False(tree.Rows[0].Changed);

        var parent = Var("p", "{}", 3);
        await tree.SetRootsAsync([parent]);
        await tree.OpenAsync(parent);

        Assert.Equal(DebugNodeKind.Message, tree.Rows[1].Kind);
        Assert.Equal("cannot read memory", tree.Rows[1].Name);
    }

    [Fact]
    public async Task AnExpensiveScopeStaysClosedUntilAsked()
    {
        var loaded = false;
        var tree = new DebugTree(_ =>
        {
            loaded = true;
            return Task.FromResult<List<DebugNode>>([]);
        });
        var globals = Scope("Globals", 2);
        globals.Expensive = true;

        await tree.SetRootsAsync([globals], openFirstScope: true);

        Assert.False(loaded);
        Assert.False(globals.IsExpanded);
        await tree.OpenAsync(globals);
        Assert.True(loaded);
    }
}
