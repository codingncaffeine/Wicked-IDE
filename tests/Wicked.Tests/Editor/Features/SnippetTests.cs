using System.Collections.Immutable;
using Microsoft.Extensions.Time.Testing;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Tests.Editor.Features;

public sealed class SnippetTests
{
    private static ExpandedSnippet X(string body, SnippetContext? ctx = null, string indent = "", string unit = "\t") => Snippets.Expand(body, ctx, indent, unit);

    private static string Flat(string body) => string.Concat(Snippets.Parse(body).Nodes.OfType<SnippetText>().Select(x => x.Text));

    private static (int, int)[] R(ExpandedSnippet e, int stop) => [.. e.Stops.Single(s => s.Index == stop).Ranges.Select(r => (r.Start, r.End))];

    // ── Parsing ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PlainTextIsOneNode()
    {
        var s = Snippets.Parse("hello world");
        Assert.Equal([new SnippetText("hello world")], s.Nodes);
        Assert.False(s.HasTabStops);
    }

    [Fact]
    public void ParsesTabStopsInBothForms()
    {
        var s = Snippets.Parse("a $1 b ${2} c $0");
        Assert.Equal(
            [new SnippetText("a "), new SnippetTabStop(1), new SnippetText(" b "), new SnippetTabStop(2), new SnippetText(" c "), new SnippetTabStop(0)],
            s.Nodes);
        Assert.True(s.HasTabStops);
    }

    [Fact]
    public void ParsesPlaceholdersAndNesting()
    {
        var s = Snippets.Parse("${1:foo ${2:bar}}");
        var p = Assert.IsType<SnippetPlaceholder>(Assert.Single(s.Nodes));
        Assert.Equal(1, p.Index);
        Assert.Equal(2, p.Children.Length);
        var inner = Assert.IsType<SnippetPlaceholder>(p.Children[1]);
        Assert.Equal(2, inner.Index);
        Assert.Equal("bar", Assert.IsType<SnippetText>(Assert.Single(inner.Children)).Text);
    }

    [Fact]
    public void ParsesChoices()
    {
        var s = Snippets.Parse("${1|one,two,three|}");
        var c = Assert.IsType<SnippetChoice>(Assert.Single(s.Nodes));
        Assert.Equal(["one", "two", "three"], c.Options);
    }

    [Fact]
    public void ChoiceEscapes()
    {
        var s = Snippets.Parse(@"${1|a\,b,c\|d,e|}");
        var c = Assert.IsType<SnippetChoice>(Assert.Single(s.Nodes));
        Assert.Equal(["a,b", "c|d", "e"], c.Options);
    }

    [Fact]
    public void ParsesVariablesInAllForms()
    {
        var s = Snippets.Parse("$TM_FILENAME ${CURRENT_YEAR} ${TM_SELECTED_TEXT:none}");
        Assert.Equal("TM_FILENAME", ((SnippetVariable)s.Nodes[0]).Name);
        Assert.Equal("CURRENT_YEAR", ((SnippetVariable)s.Nodes[2]).Name);
        var v = (SnippetVariable)s.Nodes[4];
        Assert.Equal([new SnippetText("none")], v.Default);
    }

    [Fact]
    public void EscapesBecomeLiterals()
    {
        var s = Snippets.Parse(@"cost: \$5 \} \\ \x");
        Assert.Equal([new SnippetText(@"cost: $5 } \ \x")], s.Nodes);
    }

    [Fact]
    public void BadSyntaxStaysText()
    {
        Assert.Equal([new SnippetText("a $ b")], Snippets.Parse("a $ b").Nodes);
        Assert.Equal("${1:oops", Flat("${1:oops"));
        Assert.Equal("${", Flat("${"));
        Assert.Equal("$", Flat("$"));
        Assert.Equal("${1|a,b}", Flat("${1|a,b}"));
        Assert.Empty(Snippets.Parse("").Nodes);
    }

    [Fact]
    public void ParsesTransforms()
    {
        var s = Snippets.Parse("${1/(.*)/${1:/upcase}/}");
        var t = Assert.IsType<SnippetTabStop>(Assert.Single(s.Nodes));
        Assert.Equal("HELLO", t.Transform!.Apply("hello"));
    }

    [Fact]
    public void RandomGarbageNeverThrows()
    {
        var rng = new Random(1);
        const string alphabet = "${}:|/\\,1a_0 A\n\t+?-";
        for (var i = 0; i < 3000; i++)
        {
            var body = string.Concat(Enumerable.Range(0, rng.Next(1, 24)).Select(_ => alphabet[rng.Next(alphabet.Length)]));
            var expanded = X(body);
            Assert.NotNull(expanded.Text);
            foreach (var stop in expanded.Stops)
            {
                foreach (var r in stop.Ranges) Assert.True(r.Start >= 0 && r.Start <= r.End && r.End <= expanded.Text.Length, body);
            }
        }
    }

    // ── Expanding ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ExpandsTextAndStops()
    {
        var e = X("for (${1:i} = 0; $1 < ${2:n}; $1++) {\n\t$0\n}");
        Assert.Equal("for (i = 0; i < n; i++) {\n\t\n}", e.Text);
        Assert.Equal([1, 2, 0], e.Stops.Select(s => s.Index));
        Assert.Equal([(5, 6), (12, 13), (19, 20)], R(e, 1));
        Assert.Equal([(16, 17)], R(e, 2));
        var tab = e.Text.IndexOf('\t', StringComparison.Ordinal) + 1;
        Assert.Equal([(tab, tab)], R(e, 0));
    }

    [Fact]
    public void MirrorsFollowThePrimary()
    {
        var e = X("${1:name} = $1;");
        Assert.Equal("name = name;", e.Text);
        var stop = e.Stops[0];
        Assert.Equal(2, stop.Ranges.Length);
        Assert.True(stop.Ranges[0].IsPrimary);
        Assert.False(stop.Ranges[1].IsPrimary);
        Assert.Equal(stop.Ranges[0], stop.Primary);
    }

    [Fact]
    public void AMirrorBeforeThePlaceholderStillCopiesIt()
    {
        var e = X("$1 and ${1:thing}");
        Assert.Equal("thing and thing", e.Text);
        Assert.False(e.Stops[0].Ranges[0].IsPrimary);
        Assert.Equal(e.Stops[0].Ranges[1], e.Stops[0].Primary);
    }

    [Fact]
    public void NestedPlaceholders()
    {
        var e = X("${1:foo ${2:bar}}!");
        Assert.Equal("foo bar!", e.Text);
        Assert.Equal([(0, 7)], R(e, 1));
        Assert.Equal([(4, 7)], R(e, 2));
    }

    [Fact]
    public void StopsAreOrderedWithTheFinalOneLast()
    {
        var e = X("$0 $3 $1 $2");
        Assert.Equal([1, 2, 3, 0], e.Stops.Select(s => s.Index));
    }

    [Fact]
    public void AFinalStopIsAddedAtTheEnd()
    {
        var e = X("a ${1:b} c");
        Assert.Equal([1, 0], e.Stops.Select(s => s.Index));
        Assert.Equal([(5, 5)], R(e, 0));
    }

    [Fact]
    public void NoStopsMeansNoStops()
    {
        Assert.False(X("just text").HasStops);
        Assert.False(X("$TM_FILENAME", new SnippetContext { FilePath = "/a/b.cs" }).HasStops);
    }

    [Fact]
    public void ChoicesInsertTheFirstAndKeepTheList()
    {
        var e = X("${1|a,b,c|} x");
        Assert.Equal("a x", e.Text);
        Assert.Equal(["a", "b", "c"], e.Stops[0].Choices);
    }

    [Fact]
    public void IndentsNewLinesAndConvertsLeadingTabs()
    {
        var e = X("if (x) {\n\t${1:body}\n}", indent: "    ", unit: "  ");
        Assert.Equal("if (x) {\n      body\n    }", e.Text);
        Assert.Equal([(15, 19)], R(e, 1));
        // tabs not at the line start stay
        Assert.Equal("a\tb", X("a\tb").Text);
        // a tab unit
        Assert.Equal("x\n\t\t\ty", X("x\n\t\ty", indent: "\t", unit: "\t").Text);
    }

    [Fact]
    public void VariablesResolveFromTheContext()
    {
        // Paths come out the platform's way, as VS Code gives them.
        var root = OperatingSystem.IsWindows() ? @"C:\work\proj" : "/work/proj";
        var file = Path.Combine(root, "src", "Hello.cs");
        var ctx = new SnippetContext
        {
            FilePath = file,
            WorkspaceFolder = root,
            SelectedText = "sel",
            LineIndex = 4,
            Clipboard = "clip",
            LineComment = "//",
            BlockCommentStart = "/*",
            BlockCommentEnd = "*/",
            CursorIndex = 1,
        };
        Assert.Equal($"Hello.cs Hello {Path.Combine(root, "src")} {Path.Combine("src", "Hello.cs")} {file} proj", X("$TM_FILENAME $TM_FILENAME_BASE $TM_DIRECTORY $RELATIVE_FILEPATH $TM_FILEPATH $WORKSPACE_NAME", ctx).Text);
        Assert.Equal("sel clip 4 5 1 2 // /* */", X("$TM_SELECTED_TEXT $CLIPBOARD $TM_LINE_INDEX $TM_LINE_NUMBER $CURSOR_INDEX $CURSOR_NUMBER $LINE_COMMENT $BLOCK_COMMENT_START $BLOCK_COMMENT_END", ctx).Text);
        Assert.Equal(root, X("$WORKSPACE_FOLDER", ctx).Text);
    }

    [Fact]
    public void TimeVariablesUseTheClock()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 3, 7, 9, 5, 2, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var ctx = new SnippetContext { Time = time };
        Assert.Equal("2026 26 03 March Mar 07 Saturday Sat 09 05 02", X("$CURRENT_YEAR $CURRENT_YEAR_SHORT $CURRENT_MONTH $CURRENT_MONTH_NAME $CURRENT_MONTH_NAME_SHORT $CURRENT_DATE $CURRENT_DAY_NAME $CURRENT_DAY_NAME_SHORT $CURRENT_HOUR $CURRENT_MINUTE $CURRENT_SECOND", ctx).Text);
        Assert.Equal(time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), X("$CURRENT_SECONDS_UNIX", ctx).Text);
        Assert.Equal("+00:00", X("$CURRENT_TIMEZONE_OFFSET", ctx).Text);
    }

    [Fact]
    public void RandomAndUuidHaveTheRightShape()
    {
        var ctx = new SnippetContext { RandomSeed = 3 };
        Assert.Matches("^[0-9]{6}$", X("$RANDOM", ctx).Text);
        Assert.Matches("^[0-9a-f]{6}$", X("$RANDOM_HEX", ctx).Text);
        Assert.True(Guid.TryParse(X("$UUID", ctx).Text, out _));
        Assert.Equal(X("$RANDOM", new SnippetContext { RandomSeed = 3 }).Text, X("$RANDOM", new SnippetContext { RandomSeed = 3 }).Text);
    }

    [Fact]
    public void AVariableWithNoValueUsesItsDefaultThenNothing()
    {
        Assert.Equal("fallback", X("${TM_SELECTED_TEXT:fallback}").Text);
        Assert.Equal("[]", X("[$TM_SELECTED_TEXT]").Text);
        Assert.Equal("chosen", X("${TM_SELECTED_TEXT:fallback}", new SnippetContext { SelectedText = "chosen" }).Text);
    }

    [Fact]
    public void AnUnknownVariableBecomesAPlaceholderOfItsName()
    {
        var e = X("${1:a} $UNKNOWN_THING");
        Assert.Equal("a UNKNOWN_THING", e.Text);
        Assert.Equal([1, 2, 0], e.Stops.Select(s => s.Index));
        Assert.Equal([(2, 15)], R(e, 2));
    }

    [Fact]
    public void VariableTransforms()
    {
        var ctx = new SnippetContext { FilePath = "/x/my-cool_file.test.cs" };
        Assert.Equal("MyCoolFileTest", X("${TM_FILENAME_BASE/(.*)/${1:/pascalcase}/}", ctx).Text);
        Assert.Equal("myCoolFileTest", X("${TM_FILENAME_BASE/(.*)/${1:/camelcase}/}", ctx).Text);
        Assert.Equal("MY-COOL_FILE.TEST", X("${TM_FILENAME_BASE/(.*)/${1:/upcase}/}", ctx).Text);
        Assert.Equal("my_cool_file_test", X("${TM_FILENAME_BASE/[-.]/_/g}", ctx).Text);
        Assert.Equal("My-cool_file.test", X("${TM_FILENAME_BASE/(.*)/${1:/capitalize}/}", ctx).Text);
    }

    [Fact]
    public void ConditionalFormats()
    {
        var ctx = new SnippetContext { SelectedText = "yes" };
        Assert.Equal("Y", X("${TM_SELECTED_TEXT/(yes)|(no)/${1:+Y}${2:+N}/}", ctx).Text);
        Assert.Equal("is yes", X("${TM_SELECTED_TEXT/(yes)/${1:?is yes:is no}/}", ctx).Text);
        Assert.Equal("is no", X("${TM_SELECTED_TEXT/(zzz)?.*/${1:?is yes:is no}/}", ctx).Text);
        Assert.Equal("dflt", X("${TM_SELECTED_TEXT/(zzz)?.*/${1:-dflt}/}", ctx).Text);
    }

    [Fact]
    public void ATransformedMirrorOfAPlaceholder()
    {
        var e = X("${1:hello} ${1/(.*)/${1:/upcase}/}");
        Assert.Equal("hello HELLO", e.Text);
        Assert.NotNull(e.Stops[0].Ranges[1].Transform);
    }

    // ── Sessions ─────────────────────────────────────────────────────────────────────────────────

    private static (Document Doc, SnippetSession Session, List<DocumentChange> Changes) Start(string body, string before = "", string after = "")
    {
        var expanded = X(body);
        var doc = new Document(before + expanded.Text + after);
        var session = new SnippetSession(expanded, before.Length);
        var changes = new List<DocumentChange>();
        doc.Changed += changes.Add;
        return (doc, session, changes);
    }

    private static ImmutableArray<TextChange> Type(Document doc, SnippetSession session, List<DocumentChange> changes, int at, int removed, string text)
    {
        changes.Clear();
        doc.Apply([new TextChange(at, removed, text)], [Selection.Caret(0)]);
        return session.OnChange(changes.Single());
    }

    [Fact]
    public void TabsThroughTheStopsInOrder()
    {
        var (doc, session, _) = Start("a ${1:one} ${2:two} $0!", before: "XX");
        Assert.True(session.IsActive);
        Assert.Equal(1, session.CurrentStop);
        var (sel, primary) = session.CurrentSelections();
        Assert.Equal([new Selection(4, 7)], sel);
        Assert.Equal(0, primary);
        Assert.True(session.MoveNext());
        Assert.Equal(2, session.CurrentStop);
        Assert.Equal([new Selection(8, 11)], session.CurrentSelections().Selections);
        Assert.True(session.MovePrevious());
        Assert.Equal(1, session.CurrentStop);
        Assert.False(session.MovePrevious());
        Assert.True(session.MoveNext());
        Assert.True(session.MoveNext()); // lands on $0
        Assert.Equal(0, session.CurrentStop);
        Assert.True(session.IsAtFinalStop);
        Assert.False(session.IsActive);
        Assert.Equal([new Selection(12, 12)], session.CurrentSelections().Selections);
        Assert.Equal("XXa one two !", doc.Text.ToString());
    }

    [Fact]
    public void AGroupOfOnlyAFinalStopIsNotASession()
    {
        var expanded = X("text $0");
        Assert.False(new SnippetSession(expanded, 0).IsActive);
        Assert.False(new SnippetSession(X("plain"), 0).IsActive);
    }

    [Fact]
    public void TypingInTheCurrentStopGrowsIt()
    {
        var (doc, session, changes) = Start("f(${1:a}, ${2:b})", before: ">");
        // replace the selected "a" with "arg1"
        Type(doc, session, changes, 3, 1, "arg1");
        Assert.Equal([new Selection(3, 7)], session.CurrentSelections().Selections);
        // type more at its end
        Type(doc, session, changes, 7, 0, "X");
        Assert.Equal([new Selection(3, 8)], session.CurrentSelections().Selections);
        Assert.True(session.MoveNext());
        Assert.Equal([new Selection(10, 11)], session.CurrentSelections().Selections);
        Assert.Equal(">f(arg1X, b)", doc.Text.ToString());
    }

    [Fact]
    public void TypingAtAnEmptyStopFillsIt()
    {
        var (doc, session, changes) = Start("<$1></$2>");
        Type(doc, session, changes, 1, 0, "div");
        Assert.Equal([new Selection(1, 4)], session.CurrentSelections().Selections);
        Assert.True(session.MoveNext());
        Assert.Equal([new Selection(7, 7)], session.CurrentSelections().Selections);
    }

    [Fact]
    public void AdjacentEmptyStopsDoNotSwallowEachOthersText()
    {
        var (doc, session, changes) = Start("$1$2");
        Type(doc, session, changes, 0, 0, "x");
        Assert.True(session.MoveNext());
        Assert.Equal([new Selection(1, 1)], session.CurrentSelections().Selections);
        Assert.Equal("x", doc.Text.ToString());
    }

    [Fact]
    public void MirrorsAreUpdatedAsYouType()
    {
        var (doc, session, changes) = Start("${1:x} = $1; // $1");
        var mirror = Type(doc, session, changes, 0, 1, "count");
        Assert.Equal(2, mirror.Length);
        changes.Clear();
        doc.Apply(mirror, [Selection.Caret(0)]);
        Assert.Empty(session.OnChange(changes.Single()));
        Assert.Equal("count = count; // count", doc.Text.ToString());
        Assert.Equal([new Selection(0, 5)], session.CurrentSelections().Selections.Take(1).ToArray());
        // type again
        var again = Type(doc, session, changes, 5, 0, "er");
        changes.Clear();
        doc.Apply(again, [Selection.Caret(0)]);
        session.OnChange(changes.Single());
        Assert.Equal("counter = counter; // counter", doc.Text.ToString());
    }

    [Fact]
    public void MirrorTransformsAreAppliedToTheMirror()
    {
        var (doc, session, changes) = Start("${1:a} ${1/(.*)/${1:/upcase}/}");
        var mirror = Type(doc, session, changes, 0, 1, "abc");
        changes.Clear();
        doc.Apply(mirror, [Selection.Caret(0)]);
        session.OnChange(changes.Single());
        Assert.Equal("abc ABC", doc.Text.ToString());
    }

    [Fact]
    public void EditsBeforeTheSnippetMoveIt()
    {
        var (doc, session, changes) = Start("${1:a}-${2:b}", before: "start ");
        Type(doc, session, changes, 0, 0, "ZZZ");
        Assert.True(session.IsActive);
        Assert.Equal([new Selection(9, 10)], session.CurrentSelections().Selections);
        Assert.Equal(9, session.Start);
    }

    [Fact]
    public void EditsAfterTheSnippetLeaveItAlone()
    {
        var (doc, session, changes) = Start("${1:a}-${2:b}", after: " tail");
        Type(doc, session, changes, doc.Text.Length, 0, "!!!");
        Assert.True(session.IsActive);
        Assert.Equal([new Selection(0, 1)], session.CurrentSelections().Selections);
    }

    [Fact]
    public void AnEditCrossingTheBoundaryEndsTheSession()
    {
        var (doc, session, changes) = Start("${1:a}-${2:b}", before: "start ");
        Type(doc, session, changes, 4, 4, "");
        Assert.False(session.IsActive);
        Assert.Empty(session.OnChange(changes.Single()));
    }

    [Fact]
    public void ContainsAndCancel()
    {
        var (_, session, _) = Start("${1:a}-${2:b}", before: "ab");
        Assert.True(session.Contains(2));
        Assert.True(session.Contains(5));
        Assert.False(session.Contains(1));
        Assert.False(session.Contains(6));
        session.Cancel();
        Assert.False(session.IsActive);
        Assert.False(session.MoveNext());
    }

    [Fact]
    public void ChoicesAreExposedAtTheirStop()
    {
        var (_, session, _) = Start("${1|public,private|} ${2:x}");
        Assert.Equal(["public", "private"], session.CurrentChoices);
        session.MoveNext();
        Assert.Empty(session.CurrentChoices);
    }

    [Fact]
    public void MirroredStopsSelectEveryOccurrence()
    {
        var (_, session, _) = Start("${1:v} + $1");
        var (sel, primary) = session.CurrentSelections();
        Assert.Equal([new Selection(0, 1), new Selection(4, 5)], sel);
        Assert.Equal(0, primary);
    }

    // ── Files ────────────────────────────────────────────────────────────────────────────────────

    private const string File = """
        {
            // a comment, as VS Code snippet files allow
            "For loop": {
                "prefix": ["for", "fori"],
                "body": ["for (int ${1:i} = 0; $1 < ${2:n}; $1++)", "{", "\t$0", "}"],
                "description": "A for loop",
                "scope": "csharp, javascript",
            },
            "Log": { "prefix": "log", "body": "Console.WriteLine($1);" },
            "Broken": { "prefix": "b" },
            "NotAnObject": 5,
        }
        """;

    [Fact]
    public void LoadsVsCodeSnippetFiles()
    {
        var defs = Snippets.LoadJson(File);
        Assert.Equal(2, defs.Length);
        var f = defs[0];
        Assert.Equal("For loop", f.Name);
        Assert.Equal(["for", "fori"], f.Prefixes);
        Assert.Equal("for (int ${1:i} = 0; $1 < ${2:n}; $1++)\n{\n\t$0\n}", f.Body);
        Assert.Equal("A for loop", f.Description);
        Assert.Equal(["csharp", "javascript"], f.Scopes);
        Assert.Equal(["log"], defs[1].Prefixes);
        Assert.Empty(defs[1].Scopes);
        Assert.Equal("Console.WriteLine($1);", defs[1].Body);
        Assert.Equal(2, f.Parse().Nodes.OfType<SnippetPlaceholder>().Count());
    }

    [Fact]
    public void NotJsonGivesNoSnippets()
    {
        Assert.Empty(Snippets.LoadJson("this is not json"));
        Assert.Empty(Snippets.LoadJson("[1,2]"));
        Assert.Empty(Snippets.LoadJson(""));
    }

    [Fact]
    public void FindsByPrefixAndScope()
    {
        var defs = Snippets.LoadJson(File);
        Assert.Equal(["for", "fori"], Snippets.Find(defs, "for", "csharp").Select(m => m.Prefix));
        Assert.Equal(["fori"], Snippets.Find(defs, "fori", "javascript").Select(m => m.Prefix));
        Assert.Empty(Snippets.Find(defs, "for", "python"));
        Assert.Empty(Snippets.Find(defs, "for", null));
        Assert.Equal(["log"], Snippets.Find(defs, "LO", "python").Select(m => m.Prefix));
        Assert.Equal(2, Snippets.Find(defs, "", "csharp").Count() - 1);
    }

    [Fact]
    public void AFileSnippetExpandsEndToEnd()
    {
        var def = Snippets.LoadJson(File)[0];
        var e = Snippets.Expand(def.Body, null, "    ", "    ");
        Assert.Equal("for (int i = 0; i < n; i++)\n    {\n        \n    }", e.Text);
        Assert.Equal([1, 2, 0], e.Stops.Select(s => s.Index));
        Assert.Equal(3, e.Stops[0].Ranges.Length);
    }
}
