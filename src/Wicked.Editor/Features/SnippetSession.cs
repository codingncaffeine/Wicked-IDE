using System.Collections.Immutable;
using Wicked.Editor.Model;

namespace Wicked.Editor.Features;

/// <summary>
/// An expanded snippet living in a document: which tab stop is current, where every stop is now, and how to keep
/// mirrors in step. UI-thread state, cheap by construction (a few ranges): the view creates it when it inserts a snippet,
/// feeds it every <see cref="DocumentChange"/> and asks it for the selections to show.
/// <para>
/// Ranges follow the text through <see cref="OnChange"/>. The current stop's ranges grow with what is typed at their
/// edges; the others keep their place. A change that crosses the snippet's boundary ends the session; one entirely
/// before or after it only moves it. When the current stop's text changed, OnChange returns the edits that bring its
/// mirrors up to date (through their transforms): the caller applies them to the document as one more change and feeds
/// that change back in, after which the mirrors agree and no further edits come.
/// </para>
/// <para>
/// Landing on the final stop (<c>$0</c>) ends the session, as in VS Code: the selections of that stop are still
/// readable, but later changes are ignored.
/// </para>
/// </summary>
public sealed class SnippetSession
{
    private sealed class Live(SnippetStop stop, int baseOffset)
    {
        public int Index { get; } = stop.Index;

        public ImmutableArray<string> Choices { get; } = stop.Choices;

        public List<SnippetRange> Source { get; } = [.. stop.Ranges];

        public int[] Starts { get; } = [.. stop.Ranges.Select(r => r.Start + baseOffset)];

        public int[] Ends { get; } = [.. stop.Ranges.Select(r => r.End + baseOffset)];
    }

    private readonly List<Live> _stops;
    private int _current;

    public SnippetSession(ExpandedSnippet snippet, int insertOffset)
    {
        Start = insertOffset;
        End = insertOffset + snippet.Text.Length;
        _stops = [.. snippet.Stops.Select(s => new Live(s, insertOffset))];
        IsActive = _stops.Count > 0;
        if (IsActive && _stops[0].Index == 0) IsActive = false; // only a final stop: nothing to tab through
    }

    /// <summary>False once the session ended: the last stop was reached, the user edited outside it, or it was cancelled.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The document offsets the snippet's text occupies now.</summary>
    public int Start { get; private set; }

    public int End { get; private set; }

    /// <summary>The number of the current tab stop (0 for the final one).</summary>
    public int CurrentStop => _stops.Count == 0 ? -1 : _stops[_current].Index;

    public bool IsAtFinalStop => _stops.Count > 0 && _stops[_current].Index == 0;

    /// <summary>The current stop's choices, empty unless it is a choice stop.</summary>
    public ImmutableArray<string> CurrentChoices => _stops.Count == 0 ? [] : _stops[_current].Choices;

    /// <summary>The selections to show at the current stop (a caret where the stop is empty), and which one is the primary.</summary>
    public (ImmutableArray<Selection> Selections, int Primary) CurrentSelections()
    {
        if (_stops.Count == 0) return ([], 0);
        var stop = _stops[_current];
        var selections = ImmutableArray.CreateBuilder<Selection>(stop.Starts.Length);
        var primary = 0;
        for (var i = 0; i < stop.Starts.Length; i++)
        {
            selections.Add(new Selection(stop.Starts[i], stop.Ends[i]));
            if (stop.Source[i].IsPrimary) primary = i;
        }

        return (selections.MoveToImmutable(), primary);
    }

    /// <summary>Whether <paramref name="offset"/> is inside the snippet (a caret leaving it should end the session).</summary>
    public bool Contains(int offset) => offset >= Start && offset <= End;

    /// <summary>Tab: the next stop. Returns false and ends the session when there is none; landing on the final stop ends it too, returning true.</summary>
    public bool MoveNext()
    {
        if (!IsActive) return false;
        if (_current + 1 >= _stops.Count)
        {
            IsActive = false;
            return false;
        }

        _current++;
        if (_stops[_current].Index == 0) IsActive = false;
        return true;
    }

    /// <summary>Shift+Tab: the previous stop; false at the first.</summary>
    public bool MovePrevious()
    {
        if (!IsActive || _current == 0) return false;
        _current--;
        return true;
    }

    public void Cancel() => IsActive = false;

    /// <summary>Moves the snippet's ranges through a change and returns the edits that update the current stop's mirrors (in the new text's coordinates).</summary>
    public ImmutableArray<TextChange> OnChange(DocumentChange change)
    {
        if (!IsActive) return [];

        var touchedActive = false;
        var active = _stops[_current];
        foreach (var c in change.Changes)
        {
            var inside = c.Offset >= Start && c.End <= End;
            var outside = c.End < Start || c.Offset > End;
            if (!inside && !outside)
            {
                IsActive = false;
                return [];
            }

            if (inside)
            {
                for (var i = 0; i < active.Starts.Length; i++)
                {
                    if (c.Offset >= active.Starts[i] && c.End <= active.Ends[i]) touchedActive = true;
                }
            }
        }

        var newStart = ChangeSet.Map(Start, change.Changes, stickAfter: false);
        var newEnd = ChangeSet.Map(End, change.Changes, stickAfter: true);
        foreach (var stop in _stops)
        {
            var isActive = ReferenceEquals(stop, active);
            for (var i = 0; i < stop.Starts.Length; i++)
            {
                var s = stop.Starts[i];
                var e = stop.Ends[i];
                // The current stop grows with what is typed at its edges; a non-empty range that holds the change grows too.
                var grows = isActive || (e > s && change.Changes.Any(c => c.Offset >= s && c.End <= e));
                stop.Starts[i] = ChangeSet.Map(s, change.Changes, stickAfter: !grows);
                stop.Ends[i] = ChangeSet.Map(e, change.Changes, stickAfter: true);
            }
        }

        Start = newStart;
        End = Math.Max(newStart, newEnd);
        return touchedActive ? MirrorEdits(change, active) : [];
    }

    private static ImmutableArray<TextChange> MirrorEdits(DocumentChange change, Live active)
    {
        var primaryIndex = active.Source.FindIndex(r => r.IsPrimary);
        if (primaryIndex < 0 || active.Source.Count < 2) return [];
        var text = change.After.Substring(active.Starts[primaryIndex], active.Ends[primaryIndex] - active.Starts[primaryIndex]);
        var edits = ImmutableArray.CreateBuilder<TextChange>();
        for (var i = 0; i < active.Starts.Length; i++)
        {
            if (i == primaryIndex) continue;
            var s = active.Starts[i];
            var e = active.Ends[i];
            // A range nested in or around the primary is not a mirror to overwrite.
            if (s < active.Ends[primaryIndex] && e > active.Starts[primaryIndex]) continue;
            var expected = active.Source[i].Transform?.Apply(text) ?? text;
            if (change.After.Substring(s, e - s) != expected) edits.Add(new TextChange(s, e - s, expected));
        }

        return [.. edits.OrderBy(e => e.Offset)];
    }
}
