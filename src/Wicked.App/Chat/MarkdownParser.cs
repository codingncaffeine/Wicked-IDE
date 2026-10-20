using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Wicked.App.Chat;

/// <summary>
/// Markdown to <see cref="MdBlock"/>s, with Markdig. Pure functions of their input and safe on any thread: the chat runs
/// them on a worker, never on the UI thread.
/// </summary>
public static class MarkdownParser
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    public static IReadOnlyList<MdBlock> Parse(string text)
    {
        var document = Markdown.Parse(text, Pipeline);
        var blocks = new List<MdBlock>();
        foreach (var block in document) Add(blocks, block);
        return blocks;
    }

    /// <summary>The top-level blocks with where each starts in <paramref name="text"/>, for the streaming parser.</summary>
    internal static List<(int Start, List<MdBlock> Blocks)> ParseWithStarts(string text)
    {
        var document = Markdown.Parse(text, Pipeline);
        var result = new List<(int, List<MdBlock>)>();
        foreach (var block in document)
        {
            var converted = new List<MdBlock>();
            Add(converted, block);
            result.Add((block.Span.Start, converted));
        }

        return result;
    }

    private static void Add(List<MdBlock> into, Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                into.Add(new MdHeading(heading.Level, Inlines(heading.Inline)));
                break;
            case ParagraphBlock paragraph:
                into.Add(new MdParagraph(Inlines(paragraph.Inline)));
                break;
            case FencedCodeBlock fenced:
                into.Add(new MdCodeBlock(Language(fenced.Info), Lines(fenced), fenced.ClosingFencedCharCount > 0));
                break;
            case CodeBlock code:
                into.Add(new MdCodeBlock(null, Lines(code), true));
                break;
            case QuoteBlock quote:
                into.Add(new MdQuote(Children(quote)));
                break;
            case ListBlock list:
                into.Add(ConvertList(list));
                break;
            case Table table:
                into.Add(ConvertTable(table));
                break;
            case ThematicBreakBlock:
                into.Add(new MdRule());
                break;
            case HtmlBlock html:
                // Raw HTML is shown as the text it is, never interpreted.
                into.Add(new MdParagraph([new MdText(string.Join('\n', Lines(html)))]));
                break;
            case LinkReferenceDefinitionGroup:
                break;
            case ContainerBlock container:
                foreach (var child in container) Add(into, child);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                into.Add(new MdParagraph(Inlines(leaf.Inline)));
                break;
        }
    }

    private static List<MdBlock> Children(ContainerBlock container)
    {
        var blocks = new List<MdBlock>();
        foreach (var child in container) Add(blocks, child);
        return blocks;
    }

    private static string? Language(string? info)
    {
        if (string.IsNullOrWhiteSpace(info)) return null;
        var word = info.Trim().Split(' ', 2)[0].Trim('{', '}', '.');
        return word.Length == 0 ? null : word;
    }

    private static List<string> Lines(LeafBlock block)
    {
        var lines = new List<string>(block.Lines.Count);
        for (var i = 0; i < block.Lines.Count; i++) lines.Add(block.Lines.Lines[i].Slice.ToString().TrimEnd('\r'));
        // A trailing empty line is the fence's own newline, not code.
        while (lines.Count > 0 && lines[^1].Length == 0 && block is not FencedCodeBlock) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static MdList ConvertList(ListBlock list)
    {
        var items = new List<MdListItem>();
        foreach (var child in list)
        {
            if (child is not ListItemBlock item) continue;
            bool? isChecked = null;
            var blocks = Children(item);
            if (item.FirstOrDefault() is ParagraphBlock { Inline: { } inline } && FindTask(inline) is { } task) isChecked = task.Checked;
            items.Add(new MdListItem(blocks, isChecked));
        }

        var start = int.TryParse(list.OrderedStart, out var s) ? s : 1;
        return new MdList(list.IsOrdered, start, items);
    }

    private static TaskList? FindTask(ContainerInline inline) => inline.FirstChild as TaskList;

    private static MdTable ConvertTable(Table table)
    {
        var alignments = table.ColumnDefinitions.Select(c => c.Alignment switch
        {
            TableColumnAlign.Center => MdAlign.Center,
            TableColumnAlign.Right => MdAlign.Right,
            _ => MdAlign.Left,
        }).ToList();
        IReadOnlyList<IReadOnlyList<MdInline>> header = [];
        var rows = new List<IReadOnlyList<IReadOnlyList<MdInline>>>();
        foreach (var child in table)
        {
            if (child is not TableRow row) continue;
            var cells = new List<IReadOnlyList<MdInline>>();
            foreach (var cell in row)
            {
                if (cell is not TableCell tableCell) continue;
                var inlines = new List<MdInline>();
                foreach (var part in tableCell)
                {
                    if (part is LeafBlock { Inline: { } inline }) inlines.AddRange(Inlines(inline));
                }

                cells.Add(inlines);
            }

            if (row.IsHeader) header = cells;
            else rows.Add(cells);
        }

        return new MdTable(alignments, header, rows);
    }

    private static List<MdInline> Inlines(ContainerInline? container)
    {
        var result = new List<MdInline>();
        if (container is null) return result;
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    result.Add(new MdText(literal.Content.ToString()));
                    break;
                case CodeInline code:
                    result.Add(new MdCode(code.Content));
                    break;
                case LineBreakInline lineBreak:
                    result.Add(lineBreak.IsHard ? new MdBreak() : new MdText(" "));
                    break;
                case EmphasisInline emphasis:
                    var style = emphasis.DelimiterChar == '~' ? MdStyle.Strikethrough : emphasis.DelimiterCount >= 2 ? MdStyle.Strong : MdStyle.Emphasis;
                    result.Add(new MdStyled(style, Inlines(emphasis)));
                    break;
                case LinkInline { IsImage: true } image:
                    result.Add(new MdText("[" + (PlainText(image) is { Length: > 0 } alt ? alt : "image") + "]"));
                    break;
                case LinkInline link:
                    result.Add(new MdLink(link.Url ?? "", Inlines(link)));
                    break;
                case AutolinkInline auto:
                    result.Add(new MdLink(auto.Url, [new MdText(auto.Url)]));
                    break;
                case HtmlEntityInline entity:
                    result.Add(new MdText(entity.Transcoded.ToString()));
                    break;
                case HtmlInline html:
                    result.Add(new MdText(html.Tag));
                    break;
                case TaskList:
                    break;
                case ContainerInline nested:
                    result.AddRange(Inlines(nested));
                    break;
            }
        }

        return result;
    }

    private static string PlainText(ContainerInline container)
    {
        var text = new System.Text.StringBuilder();
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            if (inline is LiteralInline literal) text.Append(literal.Content.ToString());
            else if (inline is ContainerInline nested) text.Append(PlainText(nested));
        }

        return text.ToString();
    }
}

/// <summary>
/// Parses a growing reply. The blocks before the last cannot change when more text arrives, so they are parsed once and
/// kept (the same objects every time); only the text from the last block's start is parsed again. One per message, used
/// from one worker at a time.
/// </summary>
public sealed class StreamingMarkdown
{
    private readonly List<MdBlock> _done = [];
    private string _text = "";
    private int _doneEnd;

    /// <summary>The blocks for <paramref name="full"/>, which is the whole text so far.</summary>
    public IReadOnlyList<MdBlock> Update(string full)
    {
        // Text that is not an extension of what was parsed (an edit, a new message) starts over.
        if (_doneEnd > full.Length || !full.AsSpan(0, _doneEnd).SequenceEqual(_text.AsSpan(0, _doneEnd)))
        {
            _done.Clear();
            _doneEnd = 0;
        }

        _text = full;
        var tail = _doneEnd == 0 ? full : full[_doneEnd..];
        var parsed = MarkdownParser.ParseWithStarts(tail);
        for (var i = 0; i < parsed.Count - 1; i++) _done.AddRange(parsed[i].Blocks);
        if (parsed.Count > 1) _doneEnd += parsed[^1].Start;

        var result = new List<MdBlock>(_done.Count + 2);
        result.AddRange(_done);
        if (parsed.Count > 0) result.AddRange(parsed[^1].Blocks);
        return result;
    }
}
