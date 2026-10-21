using Wicked.App.Chat;
using Wicked.Editor.Syntax;

namespace Wicked.Tests.Chat;

/// <summary>The reply's Markdown as blocks, whole and as it grows, and the code highlighter behind the code blocks.</summary>
public sealed class MarkdownTests
{
    [Fact]
    public void ParsesTheBlocksAReplyUses()
    {
        var blocks = MarkdownParser.Parse("""
            # Title

            Some *emphasis*, **strong**, `code` and a [link](https://example.test/a).

            > quoted

            1. one
            2. two

            - [x] done
            - [ ] open

            | a | b |
            |:--|--:|
            | 1 | 2 |

            ```csharp
            var x = 1;
            ```

            ---
            """);
        Assert.Collection(
            blocks,
            b => Assert.Equal(1, Assert.IsType<MdHeading>(b).Level),
            b =>
            {
                var inlines = Assert.IsType<MdParagraph>(b).Inlines;
                Assert.Contains(inlines, i => i is MdStyled { Style: MdStyle.Emphasis });
                Assert.Contains(inlines, i => i is MdStyled { Style: MdStyle.Strong });
                Assert.Contains(inlines, i => i is MdCode { Text: "code" });
                Assert.Contains(inlines, i => i is MdLink { Url: "https://example.test/a" });
            },
            b => Assert.IsType<MdParagraph>(Assert.Single(Assert.IsType<MdQuote>(b).Blocks)),
            b =>
            {
                var list = Assert.IsType<MdList>(b);
                Assert.True(list.Ordered);
                Assert.Equal(2, list.Items.Count);
            },
            b =>
            {
                var list = Assert.IsType<MdList>(b);
                Assert.Equal([true, false], list.Items.Select(i => i.Checked));
            },
            b =>
            {
                var table = Assert.IsType<MdTable>(b);
                Assert.Equal([MdAlign.Left, MdAlign.Right], table.Alignments);
                Assert.Equal(2, table.Header.Count);
                Assert.Single(table.Rows);
            },
            b =>
            {
                var code = Assert.IsType<MdCodeBlock>(b);
                Assert.Equal("csharp", code.Language);
                Assert.Equal("var x = 1;", code.Text);
                Assert.True(code.Closed);
            },
            b => Assert.IsType<MdRule>(b));
    }

    [Fact]
    public void RawHtmlIsShownAsTextNeverInterpreted()
    {
        var blocks = MarkdownParser.Parse("<script>alert(1)</script>");
        var text = Assert.IsType<MdText>(Assert.Single(Assert.IsType<MdParagraph>(Assert.Single(blocks)).Inlines));
        Assert.Contains("<script>", text.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AGrowingReplyReparsesOnlyItsLastBlock()
    {
        const string Full = "First paragraph.\n\nSecond paragraph with more words.\n\n```python\nprint(1)\nprint(2)\n```\n\nDone.";
        var streaming = new StreamingMarkdown();
        IReadOnlyList<MdBlock> previous = [];
        var reparsedEarlier = 0;
        for (var length = 1; length <= Full.Length; length++)
        {
            var blocks = streaming.Update(Full[..length]);
            // Every block but the last is the very object an earlier update returned.
            for (var i = 0; i < Math.Min(previous.Count, blocks.Count) - 1; i++)
            {
                if (!ReferenceEquals(previous[i], blocks[i])) reparsedEarlier++;
            }

            previous = blocks;
        }

        Assert.Equal(0, reparsedEarlier);
        // And the result is what a whole parse gives.
        Assert.Equal(MarkdownParser.Parse(Full).Count, previous.Count);
        Assert.Equal(4, previous.Count);
        Assert.True(((MdCodeBlock)previous[2]).Closed);
    }

    [Fact]
    public void AnUnclosedFenceIsOpenUntilTheClosingFenceArrives()
    {
        var streaming = new StreamingMarkdown();
        var open = Assert.IsType<MdCodeBlock>(Assert.Single(streaming.Update("```js\nlet a = 1;\nlet b")));
        Assert.False(open.Closed);
        Assert.Equal(["let a = 1;", "let b"], open.Lines);
        var closed = Assert.IsType<MdCodeBlock>(Assert.Single(streaming.Update("```js\nlet a = 1;\nlet b = 2;\n```")));
        Assert.True(closed.Closed);
    }

    [Fact]
    public void TextThatIsNotAnExtensionStartsOver()
    {
        var streaming = new StreamingMarkdown();
        streaming.Update("One.\n\nTwo.\n\nThree.");
        var blocks = streaming.Update("Other.");
        Assert.Equal("Other.", ((MdText)Assert.IsType<MdParagraph>(Assert.Single(blocks)).Inlines[0]).Text);
    }

    [Fact]
    public void CodeIsHighlightedOffTheCallersThreadAndGrowsFromTheFirstChangedLine()
    {
        using var highlighter = new CodeHighlighter();
        var results = new List<IReadOnlyList<HighlightedLine>>();
        var threads = new HashSet<int>();
        using var arrived = new SemaphoreSlim(0);
        var session = highlighter.Open("csharp", lines =>
        {
            lock (results)
            {
                results.Add(lines);
                threads.Add(Environment.CurrentManagedThreadId);
            }

            arrived.Release();
        });
        session.Update(["int x = 1; // note", "string s = \"a\";"]);
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken), "no highlighting arrived");
        IReadOnlyList<HighlightedLine> first;
        lock (results) first = results[^1];
        Assert.Equal(2, first.Count);
        Assert.NotEmpty(first[0].Spans);
        Assert.True(first[0].Spans.Select(s => s.Color).Distinct().Count() > 1, "one colour: the grammar did nothing");
        Assert.DoesNotContain(Environment.CurrentManagedThreadId, threads);

        session.Update(["int x = 1; // note", "string s = \"a\";", "return x;"]);
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        lock (results) Assert.Equal(3, results[^1].Count);
        session.Close();
    }

    [Fact]
    public void ACodeFenceNamesALanguageByIdAliasOrExtension()
    {
        Assert.Equal("csharp", Languages.ForName("csharp")?.Id);
        Assert.Equal("csharp", Languages.ForName("cs")?.Id);
        Assert.Equal("python", Languages.ForName("py")?.Id);
        Assert.Equal("shellscript", Languages.ForName("bash")?.Id);
        Assert.Null(Languages.ForName("not-a-language"));
        Assert.Null(Languages.ForName(null));
    }
}
