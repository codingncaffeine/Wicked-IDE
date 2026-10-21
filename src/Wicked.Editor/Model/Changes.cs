using System.Collections.Immutable;
using Wicked.Editor.Text;

namespace Wicked.Editor.Model;

/// <summary>One replacement, in the coordinates of the text before the change set it belongs to.</summary>
public readonly record struct TextChange(int Offset, int RemovedLength, string Inserted)
{
    public int End => Offset + RemovedLength;

    public int Delta => Inserted.Length - RemovedLength;
}

/// <summary>
/// One selection: the anchor stays where the selection began, the active end is the caret. <see cref="Column"/> is the
/// visual column a vertical move aims for, kept while moving up and down through shorter lines (-1 when not set).
/// <see cref="Upstream"/> is the caret's affinity where a line wraps: an active end on a break is drawn at the end of the row
/// above (End, or a click past a row's end, puts it there) instead of the start of the row below. Every other move makes a
/// new selection, which clears it.
/// </summary>
public readonly record struct Selection(int Anchor, int Active, int Column = -1, bool Upstream = false)
{
    public static Selection Caret(int offset, int column = -1, bool upstream = false) => new(offset, offset, column, upstream);

    public int Start => Math.Min(Anchor, Active);

    public int End => Math.Max(Anchor, Active);

    public bool IsEmpty => Anchor == Active;

    public int Length => End - Start;

    public bool Reversed => Active < Anchor;
}

/// <summary>Helpers for change sets: sorted, non-overlapping replacements applied together.</summary>
public static class ChangeSet
{
    /// <summary>Sorts and checks a change set; overlapping changes are a programming error.</summary>
    public static ImmutableArray<TextChange> Normalise(IEnumerable<TextChange> changes)
    {
        var sorted = changes.Where(c => c.RemovedLength > 0 || c.Inserted.Length > 0).OrderBy(c => c.Offset).ThenBy(c => c.RemovedLength).ToImmutableArray();
        for (var i = 1; i < sorted.Length; i++)
        {
            if (sorted[i].Offset < sorted[i - 1].End) throw new ArgumentException($"changes overlap at {sorted[i].Offset}");
        }

        return sorted;
    }

    /// <summary>Applies a normalised change set; the last change first, so every offset stays in the old text's coordinates.</summary>
    public static Rope Apply(Rope text, ImmutableArray<TextChange> changes)
    {
        for (var i = changes.Length - 1; i >= 0; i--)
        {
            var c = changes[i];
            text = text.Replace(c.Offset, c.RemovedLength, c.Inserted);
        }

        return text;
    }

    /// <summary>The change set that undoes <paramref name="changes"/>, in the coordinates of the text after them.</summary>
    public static ImmutableArray<TextChange> Invert(Rope before, ImmutableArray<TextChange> changes)
    {
        var inverse = ImmutableArray.CreateBuilder<TextChange>(changes.Length);
        var shift = 0;
        foreach (var c in changes)
        {
            inverse.Add(new TextChange(c.Offset + shift, c.Inserted.Length, before.Substring(c.Offset, c.RemovedLength)));
            shift += c.Delta;
        }

        return inverse.MoveToImmutable();
    }

    /// <summary>
    /// Where <paramref name="position"/> lands after <paramref name="changes"/>. A position inside a replaced range lands
    /// at the replacement's start; one at an insertion point moves past the insertion when <paramref name="stickAfter"/>.
    /// </summary>
    public static int Map(int position, ImmutableArray<TextChange> changes, bool stickAfter = true)
    {
        var shift = 0;
        foreach (var c in changes)
        {
            if (position < c.Offset || (position == c.Offset && !stickAfter && c.RemovedLength == 0)) break;
            if (position < c.End) return c.Offset + shift;
            if (position == c.Offset && c.RemovedLength == 0)
            {
                shift += c.Delta;
                break;
            }

            shift += c.Delta;
        }

        return position + shift;
    }
}

/// <summary>What one edit did, for everyone who keeps something in step with the text (the syntax worker, the view).</summary>
/// <param name="Before">The text before.</param>
/// <param name="After">The text after.</param>
/// <param name="Changes">The replacements, in <paramref name="Before"/>'s coordinates.</param>
/// <param name="Version">The document's version after the edit.</param>
public sealed record DocumentChange(Rope Before, Rope After, ImmutableArray<TextChange> Changes, long Version);
