using System.Collections.Concurrent;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;

namespace Wicked.Tests.Editor;

public sealed class SyntaxTests : IDisposable
{
    private readonly SyntaxService _service = new();

    public void Dispose() => _service.Dispose();

    /// <summary>A document with its highlighting, waited on the way the view waits: by version and progress.</summary>
    private sealed class Highlighted
    {
        private readonly BlockingCollection<SyntaxSnapshot> _snapshots = [];

        public Highlighted(SyntaxService service, string text, string file)
        {
            Doc = new Document(text);
            Session = service.Open(Doc.Text, Doc.Version, Languages.ForFile(file), _snapshots.Add);
            Doc.Changed += Session.Edit;
        }

        public Document Doc { get; }

        public SyntaxSession Session { get; }

        public SyntaxSnapshot Latest { get; private set; } = null!;

        public SyntaxSnapshot Settled(int seconds = 20)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                if (_snapshots.TryTake(out var s, TimeSpan.FromMilliseconds(100))) Latest = s;
                if (Latest is not null && Latest.Version == Doc.Version && Latest.ValidLines == Doc.Text.LineCount) return Latest;
            }

            throw new TimeoutException($"highlighting did not settle: {Latest?.ValidLines} of {Doc.Text.LineCount} lines, version {Latest?.Version} of {Doc.Version}");
        }
    }

    /// <summary>The colour of the token that covers <paramref name="column"/> of <paramref name="line"/>.</summary>
    private static uint ColourAt(SyntaxSnapshot s, int line, int column)
    {
        var tokens = s[line] ?? throw new InvalidOperationException($"line {line} has no tokens");
        var index = 0;
        while (index + 1 < tokens.Count && tokens.Start(index + 1) <= column) index++;
        return s.Colors[tokens.Foreground(index)];
    }

    [Fact]
    public void KnowsLanguagesByFileName()
    {
        Assert.Equal("source.cs", Languages.ForFile("/x/Program.cs")?.Scope);
        Assert.Equal("//", Languages.ForFile("a.cs")?.Options.LineComment);
        Assert.Equal("#", Languages.ForFile("a.py")?.Options.LineComment);
        Assert.Equal("makefile", Languages.ForFile("/x/Makefile")?.Id);
        Assert.Null(Languages.ForFile("notes.unknownext"));
    }

    [Fact]
    public void ALanguagesBracketsAndQuotesBothCloseThemselves()
    {
        var pairs = Languages.ForFile("a.cs")!.Options.Pairs;
        foreach (var open in new[] { "{", "[", "(", "\"" }) Assert.Contains(pairs, p => p.Open == open);
    }

    [Fact]
    public void KeywordsAndNamesGetDifferentColours()
    {
        var h = new Highlighted(_service, "public class Foo\n{\n    int x = 42; // note\n}\n", "a.cs");
        var s = h.Settled();
        Assert.All(Enumerable.Range(0, 4), line => Assert.NotNull(s[line]));
        Assert.NotEqual(ColourAt(s, 0, 0), ColourAt(s, 0, 13));   // "public" against "Foo"
        Assert.NotEqual(ColourAt(s, 2, 12), ColourAt(s, 2, 18));  // "42" against the comment
    }

    [Fact]
    public void TheColoursAreTheThemesOwn()
    {
        // Dark+ draws keywords #569CD6, numbers #B5CEA8 and plain text in its editor foreground #D4D4D4.
        var h = new Highlighted(_service, "public int x = 42;", "a.cs");
        var s = h.Settled();
        Assert.Equal(0xFF569CD6u, ColourAt(s, 0, 0));
        Assert.Equal(0xFFB5CEA8u, ColourAt(s, 0, 16));
        Assert.Equal(0xFFD4D4D4u, ColourAt(s, 0, 6));
    }

    [Fact]
    public void OpeningACommentRecoloursEverythingBelowAndClosingItRestoresIt()
    {
        var code = string.Join('\n', Enumerable.Range(0, 300).Select(i => $"int v{i} = {i};"));
        var h = new Highlighted(_service, code, "a.cs");
        var plain = h.Settled();
        var keyword = ColourAt(plain, 299, 0);

        h.Doc.SetSelections([Selection.Caret(0)]);
        Edits.Type(h.Doc, "/*", EditOptions.Default with { AutoClosePairs = false });
        var commented = h.Settled();
        var comment = ColourAt(commented, 299, 0);
        Assert.NotEqual(keyword, comment);
        Assert.All(new[] { 1, 150, 299 }, line => Assert.Equal(comment, ColourAt(commented, line, 0)));

        h.Doc.Undo();
        var restored = h.Settled();
        Assert.Equal(keyword, ColourAt(restored, 299, 0));
    }

    [Fact]
    public void AnEditDeepInAFileRehighlightsOnlyAFewLines()
    {
        var code = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"    var v{i} = Compute({i}, \"text\"); // line {i}"));
        var h = new Highlighted(_service, code, "a.cs");
        h.Settled(60);
        var before = h.Session.LinesHighlighted;

        h.Doc.SetSelections([Selection.Caret(h.Doc.Text.LineStart(15_000) + 8)]);
        Edits.Type(h.Doc, "x", EditOptions.Default);
        h.Settled();
        Assert.InRange(h.Session.LinesHighlighted - before, 1, 5);
    }

    [Fact]
    public void LinesAddedAndRemovedKeepTheLinesInStep()
    {
        var h = new Highlighted(_service, "a\nb\nc\nd", "a.cs");
        h.Settled();
        h.Doc.SetSelections([Selection.Caret(2)]);
        Edits.Paste(h.Doc, "x\ny\nz\n", false);
        var grown = h.Settled();
        Assert.Equal(h.Doc.Text.LineCount, grown.Lines.Count);

        h.Doc.SetSelections([new Selection(0, h.Doc.Text.LineStart(5))]);
        Edits.Delete(h.Doc);
        var shrunk = h.Settled();
        Assert.Equal(h.Doc.Text.LineCount, shrunk.Lines.Count);
    }

    [Fact]
    public void PlainTextNeedsNoGrammar()
    {
        var h = new Highlighted(_service, "just\nwords", "notes.unknownext");
        var s = h.Settled();
        Assert.Equal(2, s.ValidLines);
        Assert.Null(s[0]);
    }
}

public sealed class ViewportSyntaxTests : IDisposable
{
    private readonly SyntaxService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public void LinesInSightFarAheadAreHighlightedBeforeTheInOrderPassGetsThere()
    {
        var code = string.Join('\n', Enumerable.Range(0, 60_000).Select(i => $"    var v{i} = Compute({i}, \"text\"); // line {i}"));
        var doc = new Wicked.Editor.Model.Document(code);
        SyntaxSnapshot? seen = null;
        using var found = new ManualResetEventSlim();
        SyntaxSession? session = null;
        session = _service.Open(doc.Text, doc.Version, Languages.ForFile("a.cs"), s =>
        {
            if (s[50_010] is not null && s.ValidLines < 50_000)
            {
                seen = s;
                found.Set();
            }
        });
        session.SetViewport(50_000, 50_040);

        Assert.True(found.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the lines in sight were not highlighted ahead of the in-order pass");
        Assert.NotNull(seen![50_040]);
        session.Close();
    }
}

public sealed class SyntaxSharingTests : IDisposable
{
    private readonly SyntaxService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public void ABigDocumentDoesNotHoldBackASmallOneOpenedAfterIt()
    {
        var big = new Wicked.Editor.Model.Document(string.Join('\n', Enumerable.Range(0, 80_000).Select(i => $"    var v{i} = Compute({i}, \"text\");")));
        var bigSession = _service.Open(big.Text, big.Version, Languages.ForFile("a.cs"), _ => { });
        using var smallDone = new ManualResetEventSlim();
        var small = new Wicked.Editor.Model.Document("class A { }");
        var smallSession = _service.Open(small.Text, small.Version, Languages.ForFile("b.cs"), s =>
        {
            if (s.ValidLines == 1) smallDone.Set();
        });

        // The big one needs about ten seconds; the small one must not wait for it.
        Assert.True(smallDone.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken), "the small document waited behind the big one");
        bigSession.Close();
        smallSession.Close();
    }
}
