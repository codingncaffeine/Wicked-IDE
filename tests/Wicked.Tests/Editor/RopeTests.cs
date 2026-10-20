using System.Text;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor;

public sealed class RopeTests
{
    /// <summary>Every line's start, the slow and obvious way, to check the rope against.</summary>
    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }

        return starts;
    }

    private static void AssertSame(string expected, Rope rope)
    {
        rope.Validate();
        Assert.Equal(expected.Length, rope.Length);
        Assert.Equal(expected, rope.ToString());
        var starts = LineStarts(expected);
        Assert.Equal(starts.Count, rope.LineCount);
        for (var line = 0; line < starts.Count; line++) Assert.Equal(starts[line], rope.LineStart(line));
        for (var offset = 0; offset <= expected.Length; offset += Math.Max(1, expected.Length / 97))
        {
            var line = starts.FindLastIndex(s => s <= offset);
            Assert.Equal(line, rope.LineOf(offset));
        }

        Assert.Equal(starts.Count - 1, rope.LineOf(expected.Length));
    }

    private static string RandomText(Random random, int length)
    {
        const string alphabet = "abcdefghij klmnop\n\n{}();\té中";
        var builder = new StringBuilder(length);
        for (var i = 0; i < length; i++)
        {
            // Now and then an emoji: a surrogate pair the rope must never split.
            if (random.Next(200) == 0 && i + 1 < length)
            {
                builder.Append("\U0001F600");
                i++;
                continue;
            }

            builder.Append(alphabet[random.Next(alphabet.Length)]);
        }

        return builder.ToString();
    }

    [Fact]
    public void EmptyTextHasOneLine()
    {
        Assert.Equal(0, Rope.Empty.Length);
        Assert.Equal(1, Rope.Empty.LineCount);
        Assert.Equal(0, Rope.Empty.LineStart(0));
        Assert.Equal(0, Rope.Empty.LineOf(0));
        Assert.Equal("", Rope.Empty.Line(0));
    }

    [Fact]
    public void LinesAndPositions()
    {
        var rope = Rope.FromString("one\ntwo\n\nfour");
        Assert.Equal(4, rope.LineCount);
        Assert.Equal("two", rope.Line(1));
        Assert.Equal("", rope.Line(2));
        Assert.Equal("four", rope.Line(3));
        Assert.Equal(3, rope.LineLength(0));
        Assert.Equal((1, 2), rope.Position(6));
        Assert.Equal((0, 3), rope.Position(3)); // the break belongs to the line it ends
        Assert.Equal(6, rope.Offset(1, 2));
        Assert.Equal(7, rope.Offset(1, 99)); // the column is clamped to the line
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ThousandsOfRandomEditsMatchAPlainString(int seed)
    {
        var random = new Random(seed);
        var expected = RandomText(random, 20_000);
        var rope = Rope.FromString(expected);
        AssertSame(expected, rope);

        for (var step = 0; step < 3000; step++)
        {
            var at = random.Next(expected.Length + 1);
            if (char.IsLowSurrogate(expected.ElementAtOrDefault(at))) at--;
            switch (random.Next(10))
            {
                case < 5:
                    var inserted = RandomText(random, random.Next(1, random.Next(10) == 0 ? 3000 : 12));
                    expected = expected.Insert(at, inserted);
                    rope = rope.Insert(at, inserted);
                    break;
                case < 9:
                    var length = Math.Min(expected.Length - at, random.Next(random.Next(20) == 0 ? 4000 : 15));
                    if (at + length < expected.Length && char.IsLowSurrogate(expected[at + length])) length++;
                    expected = expected.Remove(at, length);
                    rope = rope.Remove(at, length);
                    break;
                default:
                    var by = RandomText(random, random.Next(30));
                    var replaced = Math.Min(expected.Length - at, random.Next(30));
                    if (at + replaced < expected.Length && char.IsLowSurrogate(expected[at + replaced])) replaced++;
                    expected = expected.Remove(at, replaced).Insert(at, by);
                    rope = rope.Replace(at, replaced, by);
                    break;
            }

            if (step % 100 == 0) AssertSame(expected, rope);
            else rope.Validate();
        }

        AssertSame(expected, rope);
    }

    [Fact]
    public void OldVersionsStayValidAfterEdits()
    {
        var first = Rope.FromString(string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"line {i}")));
        var second = first.Insert(100, "inserted\n").Remove(2000, 500);
        Assert.Equal(5000, first.LineCount);
        Assert.Equal("line 4999", first.Line(4999));
        Assert.NotEqual(first.ToString(), second.ToString());
    }

    [Fact]
    public void AHugeInsertStaysBalanced()
    {
        var big = new string('x', 3_000_000) + "\n" + new string('y', 10);
        var rope = Rope.FromString("start\nend").Insert(6, big);
        rope.Validate();
        Assert.Equal(3, rope.LineCount);
        Assert.InRange(rope.Height, 1, 6);
        Assert.Equal("yyyyyyyyyyend", rope.Line(2));
    }

    [Fact]
    public void ChunksAndSubstringsCoverTheRange()
    {
        var text = RandomText(new Random(7), 10_000);
        var rope = Rope.FromString(text);
        Assert.Equal(text.Substring(1234, 5678), rope.Substring(1234, 5678));
        Assert.Equal(text.Substring(1234, 5678), string.Concat(rope.Chunks(1234, 5678).Select(c => c.ToString())));
        Assert.Equal(text[4321], rope[4321]);
    }

    [Fact]
    public void AReaderNeverSplitsASurrogatePair()
    {
        // 767 'a' then an emoji: the pair straddles the reader's first block.
        var text = new string('a', Rope.MaxLeaf * 3 / 4 - 1) + "\U0001F600" + "tail\nend";
        var rope = Rope.FromReader(new StringReader(text));
        AssertSame(text, rope);
        Assert.All(rope.Chunks(0, rope.Length), c => Assert.False(char.IsHighSurrogate(c.Span[^1]) && c.Span.Length > 0 && c.Length < rope.Length));
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\n", "\r\n", "a\nb\nc\n")]
    [InlineData("a\nb\nc\r\n", "\n", "a\nb\nc\n")]
    [InlineData("a\rb\rc", "\r", "a\nb\nc")]
    [InlineData("no breaks", "\n", "no breaks")]
    public void LoadingNormalisesBreaksAndRemembersTheDominantOne(string file, string style, string kept)
    {
        var (text, lineBreak) = TextLoading.Normalise(file);
        Assert.Equal(kept, text.ToString());
        Assert.Equal(style, lineBreak);
    }
}
