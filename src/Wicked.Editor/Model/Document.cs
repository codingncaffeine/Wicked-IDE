using System.Collections.Immutable;
using Wicked.Editor.Text;

namespace Wicked.Editor.Model;

/// <summary>How an edit groups with the one before it for undo.</summary>
public enum EditKind
{
    /// <summary>Its own undo step.</summary>
    Other,

    /// <summary>Typed characters: a run of them undoes as one step.</summary>
    Typing,

    /// <summary>Backspace or Delete: a run of them undoes as one step.</summary>
    Deleting,
}

/// <summary>
/// An open text: the current <see cref="Rope"/>, its selections, and the undo history. It belongs to the UI thread and
/// does only microsecond work there: an edit is a rope path copy, and everyone who needs more (highlighting, layout,
/// search) is handed the immutable <see cref="DocumentChange"/> and works on their own thread.
/// </summary>
public sealed class Document
{
    /// <summary>A run of typing stops grouping after this pause, as in every mainstream editor.</summary>
    public static readonly TimeSpan GroupPause = TimeSpan.FromSeconds(1.5);

    private readonly Stack<UndoEntry> _undo = new();
    private readonly Stack<UndoEntry> _redo = new();
    private readonly TimeProvider _time;
    private long _nextState = 1;
    private long _state;
    private long _savedState;

    public Document(Rope text, string lineBreak = "\n", TimeProvider? time = null)
    {
        Text = text;
        LineBreak = lineBreak;
        _time = time ?? TimeProvider.System;
        Selections = [Selection.Caret(0)];
    }

    public Document(string text)
        : this(TextLoading.Normalise(text).Text, TextLoading.Normalise(text).LineBreak)
    {
    }

    public Rope Text { get; private set; }

    /// <summary>Counts every change to the text; a worker's result for an older version is stale.</summary>
    public long Version { get; private set; }

    /// <summary>The line break a save writes ("\n", "\r\n" or "\r"); the text itself always holds '\n'.</summary>
    public string LineBreak { get; set; }

    /// <summary>The selections, sorted by position and never overlapping. The first is the primary one.</summary>
    public ImmutableArray<Selection> Selections { get; private set; }

    /// <summary>Which of <see cref="Selections"/> the user made last (it is the one the view keeps in sight).</summary>
    public int PrimaryIndex { get; private set; }

    public Selection Primary => Selections[PrimaryIndex];

    /// <summary>True when the text differs from what was last saved (undoing back to it makes it clean again).</summary>
    public bool IsDirty => _state != _savedState;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised on the UI thread after every change to the text.</summary>
    public event Action<DocumentChange>? Changed;

    /// <summary>Raised after the selections change, by an edit or on their own.</summary>
    public event Action? SelectionsChanged;

    /// <summary>Raised when <see cref="IsDirty"/> may have changed.</summary>
    public event Action? DirtyChanged;

    public void MarkSaved()
    {
        _savedState = _state;
        DirtyChanged?.Invoke();
    }

    /// <summary>Sets the selections: sorted, overlapping ones merged; <paramref name="primary"/> indexes the list as given.</summary>
    public void SetSelections(IReadOnlyList<Selection> selections, int primary = -1)
    {
        if (selections.Count == 0) throw new ArgumentException("at least one selection is needed", nameof(selections));
        var length = Text.Length;
        var clamped = selections.Select(s => s with { Anchor = Math.Clamp(s.Anchor, 0, length), Active = Math.Clamp(s.Active, 0, length) }).ToList();
        var primarySelection = clamped[primary < 0 ? clamped.Count - 1 : primary];
        var merged = Merge(clamped);
        Selections = merged;
        var index = merged.IndexOf(primarySelection);
        PrimaryIndex = index >= 0 ? index : FindContaining(merged, primarySelection.Active);
        SelectionsChanged?.Invoke();
    }

    /// <summary>
    /// Applies a change set and moves to <paramref name="selectionsAfter"/> (<paramref name="primary"/> indexes it; -1 for
    /// its last). Returns false for an empty change set.
    /// </summary>
    public bool Apply(IEnumerable<TextChange> changes, IReadOnlyList<Selection> selectionsAfter, EditKind kind = EditKind.Other, int primary = -1)
    {
        var set = ChangeSet.Normalise(changes);
        if (set.IsEmpty)
        {
            SetSelections(selectionsAfter, primary);
            return false;
        }

        var before = Text;
        var selectionsBefore = Selections;
        var primaryBefore = PrimaryIndex;
        var inverse = ChangeSet.Invert(before, set);
        var stateBefore = _state;
        Commit(set);
        _state = _nextState++;
        SetSelections(selectionsAfter, primary);

        var now = _time.GetUtcNow();
        var step = new Step(set, inverse);
        _redo.Clear();
        if (kind != EditKind.Other && _undo.TryPeek(out var last) && last.Kind == kind && now - last.Time < GroupPause
            && last.SelectionsAfter.SequenceEqual(selectionsBefore) && !set.Any(c => c.Inserted.Contains('\n')))
        {
            last.Steps.Add(step);
            last.SelectionsAfter = Selections;
            last.PrimaryAfter = PrimaryIndex;
            last.Time = now;
            last.State = _state;
        }
        else
        {
            _undo.Push(new UndoEntry(kind, selectionsBefore, primaryBefore, Selections, PrimaryIndex, now, [step], _state, stateBefore));
        }

        DirtyChanged?.Invoke();
        return true;
    }

    /// <summary>Replaces the whole text (a reload from disk); the undo history is kept and this is one more step.</summary>
    public void ReplaceAll(Rope text)
    {
        if (ReferenceEquals(text, Text)) return;
        Apply([new TextChange(0, Text.Length, text.ToString())], [Selection.Caret(Math.Min(Primary.Active, text.Length))]);
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var entry)) return false;
        for (var i = entry.Steps.Count - 1; i >= 0; i--) Commit(entry.Steps[i].Inverse);
        _state = entry.StateBefore;
        _redo.Push(entry);
        SetSelections(entry.SelectionsBefore, entry.PrimaryBefore);
        DirtyChanged?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var entry)) return false;
        foreach (var step in entry.Steps) Commit(step.Forward);
        _state = entry.State;
        _undo.Push(entry);
        SetSelections(entry.SelectionsAfter, entry.PrimaryAfter);
        DirtyChanged?.Invoke();
        return true;
    }

    /// <summary>Ends the current undo group, so the next edit starts a step of its own (a caret moved by the user does this).</summary>
    public void BreakUndoGroup()
    {
        if (_undo.TryPeek(out var last)) last.Time = DateTimeOffset.MinValue;
    }

    private void Commit(ImmutableArray<TextChange> set)
    {
        var before = Text;
        Text = ChangeSet.Apply(before, set);
        Version++;
        Changed?.Invoke(new DocumentChange(before, Text, set, Version));
    }

    private static ImmutableArray<Selection> Merge(List<Selection> selections)
    {
        var sorted = selections.OrderBy(s => s.Start).ThenBy(s => s.End).ToList();
        var result = ImmutableArray.CreateBuilder<Selection>(sorted.Count);
        foreach (var s in sorted)
        {
            if (result.Count > 0)
            {
                var last = result[^1];
                // Overlapping selections merge, and so does a caret touching another selection or caret; two selections
                // that only touch stay apart (VS Code's rule).
                if (s.Start < last.End || (s.Start == last.End && (s.IsEmpty || last.IsEmpty)))
                {
                    var start = Math.Min(last.Start, s.Start);
                    var end = Math.Max(last.End, s.End);
                    result[^1] = last.Reversed ? new Selection(end, start, last.Column) : new Selection(start, end, last.Column);
                    continue;
                }
            }

            result.Add(s);
        }

        return result.ToImmutable();
    }

    private static int FindContaining(ImmutableArray<Selection> selections, int offset)
    {
        for (var i = 0; i < selections.Length; i++)
        {
            if (selections[i].Start <= offset && offset <= selections[i].End) return i;
        }

        return selections.Length - 1;
    }

    private sealed record Step(ImmutableArray<TextChange> Forward, ImmutableArray<TextChange> Inverse);

    private sealed class UndoEntry(EditKind kind, ImmutableArray<Selection> selectionsBefore, int primaryBefore, ImmutableArray<Selection> selectionsAfter, int primaryAfter, DateTimeOffset time, List<Step> steps, long state, long stateBefore)
    {
        public EditKind Kind { get; } = kind;

        public ImmutableArray<Selection> SelectionsBefore { get; } = selectionsBefore;

        public int PrimaryBefore { get; } = primaryBefore;

        public ImmutableArray<Selection> SelectionsAfter { get; set; } = selectionsAfter;

        public int PrimaryAfter { get; set; } = primaryAfter;

        public DateTimeOffset Time { get; set; } = time;

        public List<Step> Steps { get; } = steps;

        /// <summary>The state id with this entry applied.</summary>
        public long State { get; set; } = state;

        /// <summary>The state id before it, which undoing returns to (so undo back to the save point is clean).</summary>
        public long StateBefore { get; } = stateBefore;
    }
}
