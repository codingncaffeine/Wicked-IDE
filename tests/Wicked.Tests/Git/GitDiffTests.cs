// The diff functions take an optional token for the app's own cancellation; these tests run them to completion on purpose.
#pragma warning disable xUnit1051
using System.Diagnostics;
using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitDiffTests
{
    private static string[] L(string text) => text.Length == 0 ? [] : text.Split('\n');

    /// <summary>Applies a diff's blocks to the original: the result must be the modified text.</summary>
    private static string[] Rebuild(string[] a, string[] b, DiffResult diff)
    {
        var result = new List<string>();
        var at = 0;
        foreach (var block in diff.Blocks)
        {
            result.AddRange(a[at..block.OrigStart]);
            result.AddRange(b[block.ModStart..block.ModEnd]);
            at = block.OrigEnd;
        }

        result.AddRange(a[at..]);
        return [.. result];
    }

    private static int Lcs(string[] a, string[] b)
    {
        var t = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
        {
            for (var j = b.Length - 1; j >= 0; j--) t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
        }

        return t[0, 0];
    }

    [Fact]
    public void BlocksAreTheLinesChangedInOrder()
    {
        var diff = DiffEngine.Diff(L("a\nb\nc\nd\ne"), L("a\nB\nc\nd\nx\ny\ne"));
        Assert.Equal([new LineBlock(1, 1, 1, 1), new LineBlock(4, 0, 4, 2)], diff.Blocks);
        Assert.False(diff.TimedOut);
        Assert.True(DiffEngine.Diff(L("same\ntext"), L("same\ntext")).IsIdentical);
        Assert.Equal([new LineBlock(0, 2, 0, 0)], DiffEngine.Diff(L("x\ny"), []).Blocks);
        Assert.Equal([new LineBlock(0, 0, 0, 2)], DiffEngine.Diff([], L("x\ny")).Blocks);
    }

    [Fact]
    public void ARandomDiffRebuildsTheTextAndIsMinimal()
    {
        var random = new Random(20261020);
        for (var round = 0; round < 300; round++)
        {
            var alphabet = random.Next(2, 8);
            var a = Enumerable.Range(0, random.Next(0, 60)).Select(_ => $"l{random.Next(alphabet)}").ToArray();
            var b = Enumerable.Range(0, random.Next(0, 60)).Select(_ => $"l{random.Next(alphabet)}").ToArray();
            var diff = DiffEngine.Diff(a, b);
            Assert.Equal(b, Rebuild(a, b, diff));
            var common = Lcs(a, b);
            Assert.Equal(a.Length - common, diff.Blocks.Sum(x => x.OrigCount));
            Assert.Equal(b.Length - common, diff.Blocks.Sum(x => x.ModCount));
        }
    }

    [Fact]
    public void TheTimeCapStopsAHugeDiffAndStillAnswersCorrectly()
    {
        // Two unrelated 200,000-line files: the exact answer takes far longer than the cap.
        var random = new Random(7);
        var a = Enumerable.Range(0, 200_000).Select(_ => $"a{random.Next(1_000_000)}").ToArray();
        var b = Enumerable.Range(0, 200_000).Select(_ => $"b{random.Next(1_000_000)}").ToArray();
        var clock = Stopwatch.StartNew();
        var diff = DiffEngine.Diff(a, b, TimeSpan.FromMilliseconds(200));
        clock.Stop();

        Assert.True(diff.TimedOut);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the cap of 200 ms took {clock.Elapsed}");
        Assert.Equal(b, Rebuild(a, b, diff));
    }

    [Fact]
    public void ACancelledDiffStopsAtOnce()
    {
        var random = new Random(8);
        var a = Enumerable.Range(0, 100_000).Select(_ => $"a{random.Next(1_000_000)}").ToArray();
        var b = Enumerable.Range(0, 100_000).Select(_ => $"b{random.Next(1_000_000)}").ToArray();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(50);
        Assert.Throws<OperationCanceledException>(() => DiffEngine.Diff(a, b, TimeSpan.FromMinutes(5), cts.Token));
    }

    [Fact]
    public void GutterMarksSayAddedModifiedAndDeleted()
    {
        var original = L("one\ntwo\nthree\nfour\nfive\nsix");
        var working = L("one\nTWO\nthree\nnew a\nnew b\nfive");
        // two->TWO modified; four replaced by two new lines (one modified, one added); six deleted at the end.
        var marks = GutterMarks.Compute(original, working);
        Assert.Equal(
            [
                new LineChange(1, LineChangeKind.Modified),
                new LineChange(3, LineChangeKind.Modified),
                new LineChange(4, LineChangeKind.Added),
                new LineChange(5, LineChangeKind.DeletedBelow),
            ],
            marks);

        Assert.Equal([new LineChange(2, LineChangeKind.DeletedAbove)], GutterMarks.Compute(L("a\nb\nc\nd"), L("a\nb\nd")));
        Assert.Equal([new LineChange(0, LineChangeKind.Added), new LineChange(1, LineChangeKind.Added)], GutterMarks.Compute([], L("x\ny")));
        Assert.Empty(GutterMarks.Compute(L("same"), L("same")));
    }

    [Fact]
    public void CrlfAndLfTextsCompareEqual()
    {
        Assert.Empty(GutterMarks.Compute(TextLines.Split("a\r\nb\r\n"), TextLines.Split("a\nb\n")));
        Assert.Equal(["a", "b", ""], TextLines.Split("a\r\nb\n"));
    }

    [Fact]
    public void SideBySideAlignsLinesWithFillersAndMapsLinesToRows()
    {
        var left = L("keep 1\nold only\nchanged line here\nkeep 2");
        var right = L("keep 1\nchanged line there\nnew only\nkeep 2");
        var diff = SideBySideDiff.Build(left, right);

        Assert.Equal(
            [
                new AlignedRow(0, 0, AlignedRowKind.Unchanged),
                new AlignedRow(1, null, AlignedRowKind.Removed),
                new AlignedRow(2, 1, AlignedRowKind.Modified),
                new AlignedRow(null, 2, AlignedRowKind.Added),
                new AlignedRow(3, 3, AlignedRowKind.Unchanged),
            ],
            diff.Rows);
        Assert.Equal([0, 1, 2, 4], diff.LeftRowOfLine);
        Assert.Equal([0, 2, 3, 4], diff.RightRowOfLine);
        Assert.Equal([1], diff.ChangeRows);

        var inline = Assert.Single(diff.Inline);
        Assert.Equal((2, 1), (inline.LeftLine, inline.RightLine));
        Assert.Equal("changed line [here]", InlineDiff.Mark(left[2], inline.Left));
        Assert.Equal("changed line [there]", InlineDiff.Mark(right[1], inline.Right));
    }

    [Fact]
    public void EveryLineOfBothTextsHasExactlyOneRow()
    {
        var random = new Random(99);
        for (var round = 0; round < 100; round++)
        {
            var a = Enumerable.Range(0, random.Next(0, 40)).Select(_ => $"line {random.Next(6)} text").ToArray();
            var b = Enumerable.Range(0, random.Next(0, 40)).Select(_ => $"line {random.Next(6)} text").ToArray();
            var diff = SideBySideDiff.Build(a, b);
            Assert.Equal(Enumerable.Range(0, a.Length), diff.Rows.Where(r => r.Left is not null).Select(r => r.Left!.Value));
            Assert.Equal(Enumerable.Range(0, b.Length), diff.Rows.Where(r => r.Right is not null).Select(r => r.Right!.Value));
            for (var i = 0; i < a.Length; i++) Assert.Equal(i, diff.Rows[diff.LeftRowOfLine[i]].Left);
            for (var i = 0; i < b.Length; i++) Assert.Equal(i, diff.Rows[diff.RightRowOfLine[i]].Right);
        }
    }

    [Fact]
    public void InlineChangesMarkWholeWordsAndMergeNeighbours()
    {
        static (string, string) Marked(string a, string b)
        {
            var c = InlineDiff.Between(a, b, 0, 0)!;
            return (InlineDiff.Mark(a, c.Left), InlineDiff.Mark(b, c.Right));
        }

        Assert.Equal(("var [count] = [10];", "var [total] = [12];"), Marked("var count = 10;", "var total = 12;"));
        Assert.Equal(("call(a, b)", "call(a, b[, c])"), Marked("call(a, b)", "call(a, b, c)"));
        Assert.Equal(("[old words here]", "[new phrase]"), Marked("old words here", "new phrase"));
        Assert.Null(InlineDiff.Between("same", "same", 0, 0));
    }

    [Fact]
    public void WhitespaceOnlyChangesAreStillChanges()
    {
        var c = InlineDiff.Between("a b", "a  b", 3, 4)!;
        Assert.Equal((3, 4), (c.LeftLine, c.RightLine));
        Assert.NotEmpty(c.Right);
    }
}
