using Wicked.Core.Lsp;
using Wicked.Editor.Model;
using Wicked.Editor.Text;
using LspRange = Wicked.Core.Lsp.Range;

namespace Wicked.App.Language;

/// <summary>
/// Between the editor's offsets and the protocol's line/character positions. Both count UTF-16 code units (the
/// protocol's default encoding, and a .NET string's), so a column is an index into the line.
/// </summary>
public static class LspText
{
    public static Position PositionOf(Rope text, int offset)
    {
        var (line, column) = text.Position(Math.Clamp(offset, 0, text.Length));
        return new Position(line, column);
    }

    /// <summary>A position's offset, clamped into the text (servers send positions past a line's end, or a line too many).</summary>
    public static int OffsetOf(Rope text, Position position)
    {
        if (position.Line < 0) return 0;
        if (position.Line >= text.LineCount) return text.Length;
        return text.LineStart(position.Line) + Math.Clamp(position.Character, 0, text.LineLength(position.Line));
    }

    public static LspRange RangeOf(Rope text, int start, int end) => new(PositionOf(text, start), PositionOf(text, end));

    public static (int Start, int End) OffsetsOf(Rope text, LspRange range)
    {
        var start = OffsetOf(text, range.Start);
        return (start, Math.Max(start, OffsetOf(text, range.End)));
    }

    /// <summary>
    /// An edit as the protocol's incremental changes. They are applied one after another, each to the text the one before
    /// left; sent from the last to the first, each range is in the coordinates of the text before the edit.
    /// </summary>
    public static IReadOnlyList<TextDocumentContentChangeEvent> ChangesOf(DocumentChange change)
    {
        var result = new List<TextDocumentContentChangeEvent>(change.Changes.Length);
        for (var i = change.Changes.Length - 1; i >= 0; i--)
        {
            var c = change.Changes[i];
            result.Add(new TextDocumentContentChangeEvent(c.Inserted, RangeOf(change.Before, c.Offset, c.End)));
        }

        return result;
    }

    /// <summary>A server's text edits as one change set on <paramref name="text"/> (overlapping edits, which the protocol forbids, are dropped).</summary>
    public static List<TextChange> ChangesFrom(Rope text, IEnumerable<TextEdit> edits)
    {
        var changes = new List<TextChange>();
        var end = -1;
        foreach (var (start, stop, newText) in edits.Select(e =>
                 {
                     var (s, t) = OffsetsOf(text, e.Range);
                     return (s, t, e.NewText.Replace("\r\n", "\n", StringComparison.Ordinal));
                 }).OrderBy(e => e.s).ThenBy(e => e.t))
        {
            if (start < end) continue;
            changes.Add(new TextChange(start, stop - start, newText));
            end = stop;
        }

        return changes;
    }

    /// <summary>The selections after <paramref name="changes"/>, each end carried through them.</summary>
    public static List<Selection> MapSelections(IEnumerable<Selection> selections, IReadOnlyList<TextChange> changes)
    {
        var set = ChangeSet.Normalise(changes);
        return [.. selections.Select(s => s with { Anchor = ChangeSet.Map(s.Anchor, set), Active = ChangeSet.Map(s.Active, set) })];
    }
}
