using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor.Features;

public sealed class WrapMapTests(ITestOutputHelper output)
{
    private static WrapOptions Cols(int n, WrapIndent indent = WrapIndent.None, int tab = 4) => WrapOptions.AtColumn(n, tab, indent);

    private static WrapMap Of(string text, WrapOptions o, long version = 2) => WrapMap.Compute(Rope.FromString(text), version, o);

    private static string[] Pieces(string line, LineWrap? wrap)
    {
        if (wrap is null) return [line];
        var cuts = new List<int> { 0 };
        cuts.AddRange(wrap.Breaks);
        cuts.Add(line.Length);
        return [.. Enumerable.Range(0, cuts.Count - 1).Select(i => line[cuts[i]..cuts[i + 1]])];
    }

    [Fact]
    public void ShortLinesDoNotWrap()
    {
        var m = Of("abc\ndef\n", Cols(10));
        Assert.Equal(3, m.LineCount);
        Assert.Equal(3, m.VisualLineCount);
        Assert.Null(m.WrapOf(0));
        Assert.Equal(2, m.Version);
    }

    [Fact]
    public void HardBreakWithoutBreakCharacters()
    {
        var m = Of("abcdefghijklmnopqrstuvwxyz", Cols(10));
        Assert.Equal(["abcdefghij", "klmnopqrst", "uvwxyz"], Pieces("abcdefghijklmnopqrstuvwxyz", m.WrapOf(0)));
        Assert.Equal(3, m.VisualLinesOf(0));
    }

    [Fact]
    public void BreaksAfterSpacesAndPunctuation()
    {
        const string line = "hello world foo,bar baz";
        var m = Of(line, Cols(12));
        Assert.Equal(["hello world ", "foo,bar baz"], Pieces(line, m.WrapOf(0)));
        var n = Of("aaa.bbb.ccc.ddd", Cols(8));
        Assert.Equal(["aaa.bbb.", "ccc.ddd"], Pieces("aaa.bbb.ccc.ddd", n.WrapOf(0)));
    }

    [Fact]
    public void BreaksBeforeAnOpeningBracket()
    {
        const string line = "call(argumentsHere)";
        var m = Of(line, Cols(12));
        Assert.Equal(4, m.WrapOf(0)!.Breaks[0]);
    }

    [Fact]
    public void ARunOfSpacesStaysTogether()
    {
        const string line = "ab     cd";
        var m = Of(line, Cols(4));
        var pieces = Pieces(line, m.WrapOf(0));
        Assert.Equal(line, string.Concat(pieces));
        Assert.True(pieces.Length > 1);
    }

    [Fact]
    public void EveryPieceFitsAndTheTextIsPreserved()
    {
        var rng = new Random(7);
        for (var n = 0; n < 300; n++)
        {
            var line = string.Concat(Enumerable.Range(0, rng.Next(1, 120)).Select(_ => "ab ,.)(\tx"[rng.Next(9)]));
            var width = rng.Next(3, 25);
            var o = Cols(width);
            var wrap = WrapMap.Wrap(line, o);
            var pieces = Pieces(line, wrap);
            Assert.Equal(line, string.Concat(pieces));
            if (wrap is not null) Assert.True(wrap.Breaks.SequenceEqual(wrap.Breaks.Order()) && wrap.Breaks.Distinct().Count() == wrap.Breaks.Length);
        }
    }

    [Fact]
    public void TabsCountAsTabStops()
    {
        const string line = "\t\tab";
        // two tabs of 4 = 8 columns, then ab: width 9 fits 'a', not 'b'.
        var m = Of(line, Cols(9));
        Assert.Equal(2, m.VisualLinesOf(0));
        Assert.Equal(1, Of("\tab", Cols(6)).VisualLinesOf(0));
    }

    [Fact]
    public void ContinuationsAreIndentedLikeTheLine()
    {
        const string line = "    one two three four five";
        var same = Of(line, Cols(20, WrapIndent.Same));
        Assert.Equal(4, same.IndentOf(0, 1));
        Assert.Equal(0, same.IndentOf(0, 0));
        var indented = Of(line, Cols(20, WrapIndent.Indent));
        Assert.Equal(8, indented.IndentOf(0, 1));
        var none = Of(line, Cols(20));
        Assert.Equal(0, none.IndentOf(0, 1));
        // the indent shrinks the room: more pieces than with none
        Assert.True(same.VisualLinesOf(0) >= none.VisualLinesOf(0));
        // every continuation fits including its indent
        var pieces = Pieces(line, same.WrapOf(0));
        for (var i = 1; i < pieces.Length; i++) Assert.True(pieces[i].Length + 4 <= 20, pieces[i]);
    }

    [Fact]
    public void ADeepIndentIsDroppedWhenItWouldTakeOverHalfTheWidth()
    {
        var line = new string(' ', 12) + "word word word word";
        var m = Of(line, Cols(20, WrapIndent.Same));
        Assert.Equal(0, m.IndentOf(0, 1));
    }

    [Fact]
    public void ShapedPositionsOverrideTheColumns()
    {
        // 6 characters, proportional: widths 10,10,30,10,10,10 -> boundaries.
        float[] x = [0, 10, 20, 50, 60, 70, 80];
        var wrap = WrapMap.Wrap("abcdef", new WrapOptions { Width = 55 }, x);
        Assert.Equal([3], wrap!.Breaks); // 'c' ends at 50, 'd' at 60 > 55
        Assert.Null(WrapMap.Wrap("abcdef", new WrapOptions { Width = 100 }, x));
    }

    [Fact]
    public void AShapedLineOfTheWrongSizeFallsBackToColumns()
    {
        var wrap = WrapMap.Wrap("abcdef", Cols(3), [0f, 1f]);
        Assert.Equal([3], wrap!.Breaks);
    }

    [Fact]
    public void ThePerLineShapedCallbackIsUsedWhereItAnswers()
    {
        var map = WrapMap.Compute(Rope.FromString("abcdef\nabcdef"), 1, new WrapOptions { Width = 55 }, line => line == 0 ? [0f, 10, 20, 50, 60, 70, 80] : null);
        Assert.Equal([3], map.WrapOf(0)!.Breaks);
        Assert.Null(map.WrapOf(1)); // monospace 1 unit per char: 6 < 55
    }

    [Fact]
    public void NeverSplitsASurrogatePair()
    {
        var line = "ab😀cd😀ef😀gh";
        for (var width = 2; width < 8; width++)
        {
            var wrap = WrapMap.Wrap(line, Cols(width));
            foreach (var b in wrap?.Breaks ?? []) Assert.False(char.IsLowSurrogate(line[b]), $"width {width} break {b}");
        }
    }

    [Fact]
    public void LineToVisualMappingBothWays()
    {
        // lines: 0 -> 3 pieces, 1 -> 1, 2 -> 2, 3 (empty) -> 1
        var m = Of("abcdefghijklmnopqrstuvwxyz\nshort\nabcdefghijklmnop\n", Cols(10));
        Assert.Equal([3, 1, 2, 1], Enumerable.Range(0, 4).Select(m.VisualLinesOf));
        Assert.Equal([0, 3, 4, 6], Enumerable.Range(0, 4).Select(m.FirstVisualLine));
        Assert.Equal(7, m.VisualLineCount);
        var expected = new[] { (0, 0), (0, 1), (0, 2), (1, 0), (2, 0), (2, 1), (3, 0) };
        Assert.Equal(expected, Enumerable.Range(0, 7).Select(m.DocumentLine));
        Assert.Equal((3, 0), m.DocumentLine(99));
        Assert.Equal((0, 0), m.DocumentLine(-4));
    }

    [Fact]
    public void ColumnToSubLine()
    {
        var m = Of("abcdefghijklmnopqrstuvwxyz", Cols(10));
        Assert.Equal(0, m.SubLineOf(0, 0));
        Assert.Equal(0, m.SubLineOf(0, 9));
        Assert.Equal(1, m.SubLineOf(0, 10));
        Assert.Equal(2, m.SubLineOf(0, 25));
        Assert.Equal(2, m.VisualLineOf(0, 20));
        Assert.Equal((10, 20), m.Segment(0, 1, 26));
        Assert.Equal((20, 26), m.Segment(0, 2, 26));
        Assert.Equal((0, 5), Of("hello", Cols(10)).Segment(0, 0, 5));
    }

    [Fact]
    public void EditsReWrapOnlyTheTouchedLines()
    {
        var doc = new Document("short\nabcdefghijklmnopqrstuvwxyz\nlast\n");
        var map = WrapMap.Compute(doc.Text, doc.Version, Cols(10));
        DocumentChange? change = null;
        doc.Changed += c => change = c;

        // shorten the long line
        doc.Apply([new TextChange(doc.Text.LineStart(1) + 5, 21, "")], [Selection.Caret(0)]);
        var next = map.Update(change!);
        Assert.Equal(doc.Version, next.Version);
        Assert.Equal(1, next.VisualLinesOf(1));
        Assert.Equal(4, next.VisualLineCount);
        Assert.Same(map.WrapOf(0), next.WrapOf(0));

        // add lines above: later lines move, wraps follow
        var tall = next;
        doc.Apply([new TextChange(0, 0, "x\ny\nz\n")], [Selection.Caret(0)]);
        tall = tall.Update(change!);
        Assert.Equal(WrapMap.Compute(doc.Text, doc.Version, Cols(10)).VisualLineCount, tall.VisualLineCount);
    }

    [Fact]
    public void UpdateMatchesAFreshComputeAfterRandomEdits()
    {
        var rng = new Random(11);
        var doc = new Document(string.Join('\n', Enumerable.Range(0, 40).Select(i => new string((char)('a' + i % 26), rng.Next(0, 60)))));
        var o = Cols(12);
        var map = WrapMap.Compute(doc.Text, doc.Version, o);
        DocumentChange? change = null;
        doc.Changed += c => change = c;
        for (var n = 0; n < 150; n++)
        {
            var at = rng.Next(doc.Text.Length + 1);
            var remove = Math.Min(rng.Next(0, 30), doc.Text.Length - at);
            var insert = string.Concat(Enumerable.Range(0, rng.Next(0, 40)).Select(_ => "ab \n,"[rng.Next(5)]));
            if (remove == 0 && insert.Length == 0) continue;
            doc.Apply([new TextChange(at, remove, insert)], [Selection.Caret(0)]);
            map = map.Update(change!);
            var fresh = WrapMap.Compute(doc.Text, doc.Version, o);
            Assert.Equal(fresh.LineCount, map.LineCount);
            for (var line = 0; line < fresh.LineCount; line++)
            {
                Assert.Equal(fresh.VisualLinesOf(line), map.VisualLinesOf(line));
                Assert.Equal(fresh.WrapOf(line)?.Breaks ?? [], map.WrapOf(line)?.Breaks ?? []);
            }

            Assert.Equal(fresh.VisualLineCount, map.VisualLineCount);
        }
    }

    [Fact]
    public void FenwickRoundTripOnEveryVisualLine()
    {
        var rng = new Random(3);
        var text = string.Join('\n', Enumerable.Range(0, 500).Select(_ => new string('x', rng.Next(0, 55))));
        var m = Of(text, Cols(8));
        for (var v = 0; v < m.VisualLineCount; v++)
        {
            var (line, sub) = m.DocumentLine(v);
            Assert.Equal(v, m.FirstVisualLine(line) + sub);
            Assert.True(sub < m.VisualLinesOf(line));
        }
    }

    [Fact]
    public void HundredThousandLines()
    {
        var rope = Rope.FromString(FeatureSupport.BigSource(100_000));
        WrapMap? m = null;
        var ms = FeatureSupport.TimeMs(() => m = WrapMap.Compute(rope, 1, Cols(30, WrapIndent.Same)));
        var sum = 0;
        var lookups = FeatureSupport.TimeMs(() =>
        {
            for (var v = 0; v < 100_000; v++) sum += m!.DocumentLine(v * 3 % m.VisualLineCount).Line + m.FirstVisualLine(v);
        });
        var doc = new Document(rope);
        DocumentChange? change = null;
        doc.Changed += c => change = c;
        doc.Apply([new TextChange(rope.LineStart(50_000), 0, "x")], [Selection.Caret(0)]);
        var update = FeatureSupport.TimeMs(() => m!.Update(change!));
        output.WriteLine($"WrapMap.Compute: {rope.LineCount} lines -> {m!.VisualLineCount} visual, {ms:F1} ms; 100000 mapped lookups {lookups:F2} ms ({sum}); one-line Update {update:F2} ms");
        Assert.True(ms < 500);
    }
}
