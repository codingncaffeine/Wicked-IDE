namespace Wicked.App.Chat;

// The shape a reply's Markdown takes once a worker has parsed it. Immutable, so a parsed block can be handed to the UI
// thread and shared between frames: the view recognises a block it has already drawn by reference.

public enum MdStyle
{
    Emphasis,
    Strong,
    Strikethrough,
}

public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdCode(string Text) : MdInline;

public sealed record MdBreak : MdInline;

public sealed record MdStyled(MdStyle Style, IReadOnlyList<MdInline> Children) : MdInline;

public sealed record MdLink(string Url, IReadOnlyList<MdInline> Children) : MdInline;

public abstract record MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock;

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock;

/// <summary>A fenced or indented code block. <c>Closed</c> is false while the closing fence has not arrived (the reply is still streaming).</summary>
public sealed record MdCodeBlock(string? Language, IReadOnlyList<string> Lines, bool Closed) : MdBlock
{
    public string Text => string.Join('\n', Lines);
}

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock;

public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked);

public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock;

public enum MdAlign
{
    Left,
    Center,
    Right,
}

public sealed record MdTable(IReadOnlyList<MdAlign> Alignments, IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock;

public sealed record MdRule : MdBlock;
