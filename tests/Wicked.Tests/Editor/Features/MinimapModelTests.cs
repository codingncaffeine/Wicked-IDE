using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor.Features;

public sealed class MinimapModelTests(ITestOutputHelper output)
{
    private static MinimapRun[] Runs(MinimapModel m, int line) => [.. Enumerable.Range(0, m.RunCount(line)).Select(i => m.Run(line, i))];

    [Fact]
    public void WordsBecomeRunsAndSpacesGaps()
    {
        var m = MinimapModel.Build(Rope.FromString("ab cde  f"), 4, null);
        Assert.Equal([new MinimapRun(0, 2, 1), new MinimapRun(3, 3, 1), new MinimapRun(8, 1, 1)], Runs(m, 0));
        Assert.Equal(4, m.Version);
        Assert.Equal(0, m.Indent(0));
    }

    [Fact]
    public void IndentationIsMeasuredInVisualColumns()
    {
        var m = MinimapModel.Build(Rope.FromString("    a\n\tb\n  \tc\n\n   \n"), 0, null, tabSize: 4);
        Assert.Equal([4, 4, 4, 0, 0, 0], Enumerable.Range(0, 6).Select(m.Indent));
        Assert.Equal(new MinimapRun(4, 1, 1), Runs(m, 0)[0]);
        Assert.Equal(new MinimapRun(4, 1, 1), Runs(m, 1)[0]);
        Assert.Empty(Runs(m, 3));
        Assert.Empty(Runs(m, 4));
    }

    [Fact]
    public void ColoursSplitRunsAndComeFromTokens()
    {
        // "foo(bar)" : foo colour 2, "(" colour 3, bar colour 2, ")" colour 3
        var syntax = FeatureSupport.Synthetic(1, _ => FeatureSupport.Tokens((0, 0, 2), (3, 0, 3), (4, 0, 2), (7, 0, 3)));
        var m = MinimapModel.Build(Rope.FromString("foo(bar)"), 0, syntax);
        Assert.Equal([new MinimapRun(0, 3, 2), new MinimapRun(3, 1, 3), new MinimapRun(4, 3, 2), new MinimapRun(7, 1, 3)], Runs(m, 0));
        Assert.Equal(0xFFFF0000u, m.ColorOf(2));
        Assert.Same(syntax.Colors, m.Colors);
    }

    [Fact]
    public void ASameColourAcrossTokensMergesIntoOneRun()
    {
        var syntax = FeatureSupport.Synthetic(1, _ => FeatureSupport.Tokens((0, 0, 2), (3, 0, 2)));
        var m = MinimapModel.Build(Rope.FromString("foobar"), 0, syntax);
        Assert.Equal([new MinimapRun(0, 6, 2)], Runs(m, 0));
    }

    [Fact]
    public void RunsAreClippedAtMaxColumns()
    {
        var m = MinimapModel.Build(Rope.FromString(new string('x', 500) + "\n" + new string(' ', 130) + "y"), 0, null, maxColumns: 120);
        Assert.Equal([new MinimapRun(0, 120, 1)], Runs(m, 0));
        Assert.Empty(Runs(m, 1));
    }

    [Fact]
    public void ALineWithoutTokensUsesTheDefaultColour()
    {
        var syntax = FeatureSupport.Synthetic(2, i => i == 0 ? FeatureSupport.Tokens((0, 0, 3)) : []) with { };
        var m = MinimapModel.Build(Rope.FromString("ab\ncd"), 0, syntax);
        Assert.Equal(3, Runs(m, 0)[0].ColorId);
        Assert.Equal(1, Runs(m, 1)[0].ColorId);
    }

    [Fact]
    public void RealHighlightingGivesMoreThanOneColour()
    {
        var code = "public class A\n{\n    int x = 42; // note\n}\n";
        var syntax = FeatureSupport.Highlight(code, "a.cs");
        var m = MinimapModel.Build(Rope.FromString(code), 0, syntax);
        Assert.True(Runs(m, 0).Select(r => r.ColorId).Distinct().Count() >= 2);
        Assert.Equal(4, m.Indent(2));
    }

    [Fact]
    public void UpdateMatchesAFreshBuildAfterRandomEdits()
    {
        var rng = new Random(5);
        var doc = new Document(string.Join('\n', Enumerable.Range(0, 30).Select(i => new string(' ', i % 5) + new string((char)('a' + i % 26), rng.Next(0, 20)) + " end")));
        var m = MinimapModel.Build(doc.Text, doc.Version, null);
        DocumentChange? change = null;
        doc.Changed += c => change = c;
        for (var n = 0; n < 200; n++)
        {
            var at = rng.Next(doc.Text.Length + 1);
            var remove = Math.Min(rng.Next(0, 25), doc.Text.Length - at);
            var insert = string.Concat(Enumerable.Range(0, rng.Next(0, 30)).Select(_ => "ab  \n\tz"[rng.Next(7)]));
            if (remove == 0 && insert.Length == 0) continue;
            doc.Apply([new TextChange(at, remove, insert)], [Selection.Caret(0)]);
            m = m.Update(change!, null);
            var fresh = MinimapModel.Build(doc.Text, doc.Version, null);
            Assert.Equal(fresh.LineCount, m.LineCount);
            Assert.Equal(fresh.Version, m.Version);
            for (var line = 0; line < fresh.LineCount; line++)
            {
                Assert.Equal(fresh.Indent(line), m.Indent(line));
                Assert.Equal(Runs(fresh, line), Runs(m, line));
            }
        }
    }

    [Fact]
    public void UpdateLinesRecolours()
    {
        var rope = Rope.FromString("ab\ncd\nef");
        var plain = MinimapModel.Build(rope, 1, null);
        var syntax = FeatureSupport.Synthetic(3, _ => FeatureSupport.Tokens((0, 0, 4)), 2);
        var m = plain.UpdateLines(rope, 2, syntax, 1, 1);
        Assert.Equal(1, Runs(m, 0)[0].ColorId);
        Assert.Equal(4, Runs(m, 1)[0].ColorId);
        Assert.Equal(1, Runs(m, 2)[0].ColorId);
        Assert.Equal(2, m.Version);
        // a different line count falls back to a rebuild
        var other = plain.UpdateLines(Rope.FromString("a\nb"), 3, syntax, 0, 0);
        Assert.Equal(2, other.LineCount);
    }

    [Fact]
    public void HundredThousandLines()
    {
        var rope = Rope.FromString(FeatureSupport.BigSource(100_000));
        var syntax = FeatureSupport.Synthetic(rope.LineCount, i => FeatureSupport.Tokens((0, 0, 2), (8, 0, 3), (16, 0, 4)));
        MinimapModel? m = null;
        var ms = FeatureSupport.TimeMs(() => m = MinimapModel.Build(rope, 1, syntax));
        var doc = new Document(rope);
        DocumentChange? change = null;
        doc.Changed += c => change = c;
        doc.Apply([new TextChange(rope.LineStart(50_000), 0, "x")], [Selection.Caret(0)]);
        var update = FeatureSupport.TimeMs(() => m!.Update(change!, syntax));
        var read = FeatureSupport.TimeMs(() =>
        {
            var sum = 0;
            for (var line = 40_000; line < 41_500; line++)
            {
                for (var i = 0; i < m!.RunCount(line); i++) sum += m.Run(line, i).Length;
            }
        }, 20);
        output.WriteLine($"MinimapModel.Build: {rope.LineCount} lines, {ms:F1} ms; one-line Update {update:F2} ms; read 1500 lines {read * 1000:F1} us");
        Assert.True(ms < 500);
    }
}
