using System.Collections.Immutable;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>How a range of text is marked.</summary>
public enum RangeStyle
{
    /// <summary>A wavy underline in the error colour (a diagnostic of severity 1).</summary>
    Error,

    Warning,

    Info,

    /// <summary>Three dots under the start, as VS Code marks a hint.</summary>
    Hint,

    /// <summary>Drawn faded: unused code a server tagged <c>Unnecessary</c>.</summary>
    Unnecessary,

    /// <summary>Struck through: a <c>Deprecated</c> tag.</summary>
    Deprecated,

    /// <summary>A background behind other uses of the symbol at the caret (read), and where it is written.</summary>
    Highlight,

    HighlightWrite,

    /// <summary>Characters added or removed inside a changed line of a diff.</summary>
    Inserted,

    Removed,

    /// <summary>A plain underline: a link under the pointer with Ctrl held.</summary>
    Link,
}

/// <summary>A whole line's background.</summary>
public enum LineStyle
{
    Inserted,
    Removed,

    /// <summary>The sides of a merge conflict, and its common ancestor.</summary>
    ConflictCurrent,

    ConflictIncoming,

    ConflictBase,

    /// <summary>The line a debugger stopped on.</summary>
    Stopped,
}

/// <summary>A mark in the gutter beside a line.</summary>
public enum GutterGlyph
{
    /// <summary>Source control: a bar for added and changed lines, a wedge where lines were deleted.</summary>
    Added,

    Modified,

    Deleted,

    Breakpoint,

    BreakpointConditional,

    Logpoint,

    BreakpointDisabled,

    /// <summary>A breakpoint the debugger could not bind (hollow).</summary>
    BreakpointUnverified,

    /// <summary>The debugger's current line (an arrow; over a breakpoint when both).</summary>
    Stopped,

    /// <summary>A quick fix or refactoring is offered for the caret's line.</summary>
    Lightbulb,
}

/// <summary>Text drawn inside a line that is not in the document: an inlay hint, or completion text not yet accepted.</summary>
public enum InlineStyle
{
    /// <summary>A small label on a tinted pill (a parameter name or an inferred type).</summary>
    Hint,

    /// <summary>Faded text in the code's own font: a suggestion that Tab accepts.</summary>
    Ghost,
}

/// <summary>[Start, End) of the text, marked with a style. An empty range marks the character after it, for sight.</summary>
public readonly record struct RangeDecoration(int Start, int End, RangeStyle Style);

/// <summary>A line's background, held by its start offset so it follows the line through edits.</summary>
public readonly record struct LineDecoration(int LineStart, LineStyle Style);

/// <summary>A gutter mark, held by its line's start offset.</summary>
public readonly record struct GutterDecoration(int LineStart, GutterGlyph Glyph);

/// <summary>Text drawn at <see cref="Offset"/> (before the character there) that is not part of the document.</summary>
public readonly record struct InlineDecoration(int Offset, string Text, InlineStyle Style, bool PadLeft = false, bool PadRight = false);

/// <summary>
/// One provider's marks on one document (the diagnostics, the source-control changes, the breakpoints, …), for one text
/// version. Immutable; <see cref="Map"/> carries it through an edit so the marks stay on their text until the provider
/// sends a fresh set. Ranges are sorted by start, which lets a frame find the ones in sight by a binary search.
/// </summary>
public sealed class DecorationSet
{
    public static readonly DecorationSet Empty = new([], [], [], []);

    public DecorationSet(
        IEnumerable<RangeDecoration> ranges,
        IEnumerable<LineDecoration> lines,
        IEnumerable<GutterDecoration> gutter,
        IEnumerable<InlineDecoration> inline)
    {
        Ranges = [.. ranges.OrderBy(r => r.Start).ThenBy(r => r.End)];
        Lines = [.. lines.OrderBy(l => l.LineStart)];
        Gutter = [.. gutter.OrderBy(g => g.LineStart)];
        Inline = [.. inline.OrderBy(i => i.Offset)];
        MaxRangeLength = Ranges.IsEmpty ? 0 : Ranges.Max(r => r.End - r.Start);
    }

    public static DecorationSet OfRanges(IEnumerable<RangeDecoration> ranges) => new(ranges, [], [], []);

    public static DecorationSet OfGutter(IEnumerable<GutterDecoration> gutter) => new([], [], gutter, []);

    public ImmutableArray<RangeDecoration> Ranges { get; }

    public ImmutableArray<LineDecoration> Lines { get; }

    public ImmutableArray<GutterDecoration> Gutter { get; }

    public ImmutableArray<InlineDecoration> Inline { get; }

    /// <summary>The longest range: how far before a window to start looking for ranges that reach into it.</summary>
    public int MaxRangeLength { get; }

    public bool IsEmpty => Ranges.IsEmpty && Lines.IsEmpty && Gutter.IsEmpty && Inline.IsEmpty;

    /// <summary>
    /// The set after an edit. Text typed at a range's start goes before it and text typed at its end joins it; typing inside a marked
    /// word stays marked; a range the edit swallowed whole shrinks to an empty mark where it was. Line marks follow their
    /// line's start; a line mark whose line was joined into the one above lands on that line and duplicates are dropped.
    /// </summary>
    public DecorationSet Map(DocumentChange change)
    {
        if (IsEmpty || change.Changes.IsEmpty) return this;
        var changes = change.Changes;
        var after = change.After;
        var ranges = new RangeDecoration[Ranges.Length];
        for (var i = 0; i < Ranges.Length; i++)
        {
            var r = Ranges[i];
            var start = ChangeSet.Map(r.Start, changes, stickAfter: true);
            var end = Math.Max(start, ChangeSet.Map(r.End, changes, stickAfter: true));
            ranges[i] = r with { Start = start, End = end };
        }

        // A line mark rides with its line: Enter at the line's start carries it down with the text.
        int LineOf(int lineStart) => after.LineStart(after.LineOf(Math.Clamp(ChangeSet.Map(lineStart, changes, stickAfter: true), 0, after.Length)));
        var lines = Lines.Select(l => l with { LineStart = LineOf(l.LineStart) }).Distinct();
        var gutter = Gutter.Select(g => g with { LineStart = LineOf(g.LineStart) }).Distinct();
        var inline = Inline.Select(d => d with { Offset = ChangeSet.Map(d.Offset, changes, stickAfter: false) });
        return new DecorationSet(ranges, lines, gutter, inline);
    }

    /// <summary>The ranges that touch [<paramref name="from"/>, <paramref name="to"/>], in order of start.</summary>
    public IEnumerable<RangeDecoration> RangesIn(int from, int to)
    {
        if (Ranges.IsEmpty) yield break;
        // The first range that could reach into the window starts no earlier than from - the longest range.
        var lo = 0;
        var hi = Ranges.Length;
        var floor = from - MaxRangeLength;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Ranges[mid].Start < floor) lo = mid + 1;
            else hi = mid;
        }

        for (var i = lo; i < Ranges.Length && Ranges[i].Start <= to; i++)
        {
            if (Ranges[i].End >= from) yield return Ranges[i];
        }
    }

    /// <summary>The inline decorations at offsets in [<paramref name="from"/>, <paramref name="to"/>], in order.</summary>
    public IEnumerable<InlineDecoration> InlineIn(int from, int to)
    {
        if (Inline.IsEmpty) yield break;
        int lo = 0, hi = Inline.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (Inline[mid].Offset < from) lo = mid + 1;
            else hi = mid;
        }

        for (var i = lo; i < Inline.Length && Inline[i].Offset <= to; i++) yield return Inline[i];
    }

    /// <summary>A position's line start in <paramref name="text"/>: the key the line marks use.</summary>
    public static int LineKey(Rope text, int line) => text.LineStart(Math.Clamp(line, 0, text.LineCount - 1));
}

/// <summary>
/// The marks every provider put on a document, by name ("diagnostics", "git", "debug", …): each provider replaces its own
/// layer and never sees the others. Immutable like its layers, so a frame reads a consistent picture.
/// </summary>
public sealed class DecorationLayers
{
    public static readonly DecorationLayers None = new(ImmutableSortedDictionary<string, DecorationSet>.Empty, 0);

    private DecorationLayers(ImmutableSortedDictionary<string, DecorationSet> layers, long version)
    {
        Layers = layers;
        Version = version;
    }

    public ImmutableSortedDictionary<string, DecorationSet> Layers { get; }

    /// <summary>Goes up on every change, so a view can tell its picture is stale without comparing sets.</summary>
    public long Version { get; }

    public DecorationSet this[string name] => Layers.TryGetValue(name, out var set) ? set : DecorationSet.Empty;

    public DecorationLayers With(string name, DecorationSet set) =>
        new(set.IsEmpty ? Layers.Remove(name) : Layers.SetItem(name, set), Version + 1);

    public DecorationLayers Map(DocumentChange change)
    {
        if (Layers.IsEmpty) return this;
        return new(Layers.ToImmutableSortedDictionary(kv => kv.Key, kv => kv.Value.Map(change)), Version + 1);
    }

    public IEnumerable<DecorationSet> Sets => Layers.Values;
}
