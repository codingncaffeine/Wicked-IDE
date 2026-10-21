namespace Wicked.App.Chat;

// The shape a reply's Markdown takes once a worker has parsed it. Immutable, with value equality all the way down, so the
// view can tell on every frame which blocks of a growing reply are still what it already drew and touch only the rest.

public enum MdStyle
{
    Emphasis,
    Strong,
    Strikethrough,
}

public enum MdAlign
{
    Left,
    Center,
    Right,
}

internal static class MdSeq
{
    public static bool Same<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : notnull
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i])) return false;
        }

        return true;
    }

    public static bool SameRow(IReadOnlyList<IReadOnlyList<MdInline>> a, IReadOnlyList<IReadOnlyList<MdInline>> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!Same(a[i], b[i])) return false;
        }

        return true;
    }

    public static int Hash<T>(IReadOnlyList<T> items) where T : notnull => items.Count == 0 ? 0 : HashCode.Combine(items.Count, items[0], items[^1]);
}

public abstract record MdInline;

public sealed record MdText(string Text) : MdInline;

public sealed record MdCode(string Text) : MdInline;

public sealed record MdBreak : MdInline;

public sealed record MdStyled(MdStyle Style, IReadOnlyList<MdInline> Children) : MdInline
{
    public bool Equals(MdStyled? other) => other is not null && Style == other.Style && MdSeq.Same(Children, other.Children);

    public override int GetHashCode() => HashCode.Combine(Style, MdSeq.Hash(Children));
}

public sealed record MdLink(string Url, IReadOnlyList<MdInline> Children) : MdInline
{
    public bool Equals(MdLink? other) => other is not null && Url == other.Url && MdSeq.Same(Children, other.Children);

    public override int GetHashCode() => HashCode.Combine(Url, MdSeq.Hash(Children));
}

public abstract record MdBlock;

public sealed record MdParagraph(IReadOnlyList<MdInline> Inlines) : MdBlock
{
    public bool Equals(MdParagraph? other) => other is not null && MdSeq.Same(Inlines, other.Inlines);

    public override int GetHashCode() => MdSeq.Hash(Inlines);
}

public sealed record MdHeading(int Level, IReadOnlyList<MdInline> Inlines) : MdBlock
{
    public bool Equals(MdHeading? other) => other is not null && Level == other.Level && MdSeq.Same(Inlines, other.Inlines);

    public override int GetHashCode() => HashCode.Combine(Level, MdSeq.Hash(Inlines));
}

/// <summary>A fenced or indented code block. <c>Closed</c> is false while the closing fence has not arrived (the reply is still streaming).</summary>
public sealed record MdCodeBlock(string? Language, IReadOnlyList<string> Lines, bool Closed) : MdBlock
{
    public string Text => string.Join('\n', Lines);

    public bool Equals(MdCodeBlock? other) => other is not null && Language == other.Language && Closed == other.Closed && MdSeq.Same(Lines, other.Lines);

    public override int GetHashCode() => HashCode.Combine(Language, Closed, MdSeq.Hash(Lines));
}

public sealed record MdQuote(IReadOnlyList<MdBlock> Blocks) : MdBlock
{
    public bool Equals(MdQuote? other) => other is not null && MdSeq.Same(Blocks, other.Blocks);

    public override int GetHashCode() => MdSeq.Hash(Blocks);
}

public sealed record MdListItem(IReadOnlyList<MdBlock> Blocks, bool? Checked)
{
    public bool Equals(MdListItem? other) => other is not null && Checked == other.Checked && MdSeq.Same(Blocks, other.Blocks);

    public override int GetHashCode() => HashCode.Combine(Checked, MdSeq.Hash(Blocks));
}

public sealed record MdList(bool Ordered, int Start, IReadOnlyList<MdListItem> Items) : MdBlock
{
    public bool Equals(MdList? other) => other is not null && Ordered == other.Ordered && Start == other.Start && MdSeq.Same(Items, other.Items);

    public override int GetHashCode() => HashCode.Combine(Ordered, Start, MdSeq.Hash(Items));
}

public sealed record MdTable(IReadOnlyList<MdAlign> Alignments, IReadOnlyList<IReadOnlyList<MdInline>> Header, IReadOnlyList<IReadOnlyList<IReadOnlyList<MdInline>>> Rows) : MdBlock
{
    public bool Equals(MdTable? other)
    {
        if (other is null || !MdSeq.Same(Alignments, other.Alignments) || !MdSeq.SameRow(Header, other.Header) || Rows.Count != other.Rows.Count) return false;
        for (var i = 0; i < Rows.Count; i++)
        {
            if (!MdSeq.SameRow(Rows[i], other.Rows[i])) return false;
        }

        return true;
    }

    public override int GetHashCode() => HashCode.Combine(Alignments.Count, Header.Count, Rows.Count);
}

public sealed record MdRule : MdBlock;
