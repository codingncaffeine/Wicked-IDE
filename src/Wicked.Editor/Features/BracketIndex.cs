using System.Collections.Immutable;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>Two brackets that belong together, as document offsets of the brackets themselves.</summary>
public readonly record struct BracketMatch(int Open, int Close);

/// <summary>
/// Every real bracket in a document, in offset order, with its partner and its nesting depth, for bracket matching and
/// bracket-pair colourisation. Real means outside comments, strings and regular expressions, judged by the syntax
/// snapshot's token types (not colours). Built on a worker from a rope snapshot; immutable and tagged with the version
/// it was built for. The view looks brackets up with binary searches over parallel arrays and allocates nothing.
/// <para>
/// Matching follows VS Code: one stack across all pair kinds. A closer that matches the innermost opener closes it; a
/// closer that matches an outer opener closes that one and leaves the openers inside it unmatched; a closer that matches
/// nothing is unmatched. Only single-character pairs take part (<c>EditOptions.Pairs</c> entries whose Open and Close
/// differ). Full recompute: one pass over the text, measured at about 20 ms for 100,000 lines (167,000 brackets) on a worker, which is inside the
/// 30 ms worker budget, so there is no incremental mode to keep consistent with it.
/// </para>
/// </summary>
public sealed class BracketIndex
{
    private readonly int[] _offsets;
    private readonly int[] _partners;
    private readonly int[] _depths;
    private readonly byte[] _kinds;
    private readonly char[] _chars;

    private BracketIndex(long version, int[] offsets, int[] partners, int[] depths, byte[] kinds, char[] chars)
    {
        Version = version;
        _offsets = offsets;
        _partners = partners;
        _depths = depths;
        _kinds = kinds;
        _chars = chars;
    }

    public static readonly BracketIndex Empty = new(-1, [], [], [], [], []);

    /// <summary>The document version this index describes.</summary>
    public long Version { get; }

    /// <summary>The number of brackets.</summary>
    public int Count => _offsets.Length;

    public int Offset(int index) => _offsets[index];

    /// <summary>The bracket character.</summary>
    public char Char(int index) => _chars[index];

    /// <summary>The index of the partner bracket, or -1 when this one is unmatched.</summary>
    public int Partner(int index) => _partners[index];

    public bool IsMatched(int index) => _partners[index] >= 0;

    /// <summary>
    /// Nesting depth, 0 for the outermost. An unmatched closer takes the depth it would have had. The painter picks the
    /// colour (VS Code: depth mod 3 of its three bracket colours, 6 in the dark themes).
    /// </summary>
    public int Depth(int index) => _depths[index];

    public bool IsOpener(int index) => _kinds[index] >= 128;

    /// <summary>The kind of pair (the index in the pair list); an opener's value is offset by 128.</summary>
    public int Kind(int index) => _kinds[index] & 127;

    /// <summary>The first bracket at or after <paramref name="offset"/>, or <see cref="Count"/>. O(log n), no allocation.</summary>
    public int FirstAtOrAfter(int offset)
    {
        var i = Array.BinarySearch(_offsets, offset);
        return i >= 0 ? i : ~i;
    }

    /// <summary>The index of the bracket exactly at <paramref name="offset"/>, or -1.</summary>
    public int IndexAt(int offset)
    {
        var i = Array.BinarySearch(_offsets, offset);
        return i >= 0 ? i : -1;
    }

    /// <summary>
    /// The pair VS Code highlights for a caret: the bracket just before the caret, else the one just after it . Null when neither is a matched bracket.
    /// </summary>
    public BracketMatch? MatchAt(int caret)
    {
        var before = IndexAt(caret - 1);
        if (before >= 0 && _partners[before] >= 0) return Pair(before);
        var after = IndexAt(caret);
        if (after >= 0 && _partners[after] >= 0) return Pair(after);
        return null;
    }

    /// <summary>The innermost pair that encloses <paramref name="offset"/> ("go to enclosing bracket"), or null.</summary>
    public BracketMatch? Enclosing(int offset)
    {
        // The nearest matched opener before the offset whose closer lies at or after it; walk outwards through partners.
        var i = FirstAtOrAfter(offset) - 1;
        while (i >= 0)
        {
            if (IsOpener(i) && _partners[i] >= 0 && _offsets[_partners[i]] >= offset) return Pair(i);
            // Skip over a whole closed pair that ends before the offset.
            if (!IsOpener(i) && _partners[i] >= 0) i = _partners[i];
            i--;
        }

        return null;
    }

    /// <summary>
    /// Where Jump to Bracket (Ctrl+Shift+\) takes a caret: beside an opener, to just before its closer; beside a closer, to
    /// just after its opener (so a second jump comes back); inside a pair, to just before its closer. Null when there is none.
    /// </summary>
    public int? JumpTarget(int caret)
    {
        if (MatchAt(caret) is { } m) return caret == m.Open || caret == m.Open + 1 ? m.Close : m.Open + 1;
        return Enclosing(caret)?.Close;
    }

    private BracketMatch Pair(int i)
    {
        var p = _partners[i];
        return _offsets[i] < _offsets[p] ? new BracketMatch(_offsets[i], _offsets[p]) : new BracketMatch(_offsets[p], _offsets[i]);
    }

    /// <summary>Builds the index. <paramref name="syntax"/> may be null or behind the text; lines it does not cover count as code.</summary>
    public static BracketIndex Build(Rope text, long version, SyntaxSnapshot? syntax, ImmutableArray<BracketPair> pairs = default)
    {
        var table = LineTable.From(text);
        return Build(table, version, syntax, pairs);
    }

    internal static BracketIndex Build(LineTable table, long version, SyntaxSnapshot? syntax, ImmutableArray<BracketPair> pairs)
    {
        // Character -> kind lookup; openers and closers of one pair share a kind.
        var openers = new Dictionary<char, int>();
        var closers = new Dictionary<char, int>();
        var list = pairs.IsDefault ? DefaultPairs : pairs;
        var kind = 0;
        foreach (var p in list)
        {
            if (p.Open.Length != 1 || p.Close.Length != 1 || p.Open == p.Close) continue;
            openers[p.Open[0]] = kind;
            closers[p.Close[0]] = kind;
            kind++;
        }

        if (kind == 0) return new BracketIndex(version, [], [], [], [], []);

        var offsets = new List<int>();
        var chars = new List<char>();
        var kinds = new List<byte>();
        var partners = new List<int>();
        var depths = new List<int>();
        var stack = new List<int>(); // indices into the lists, openers still waiting
        var valid = syntax?.ValidLines ?? 0;

        for (var line = 0; line < table.Count; line++)
        {
            var start = table.Start(line);
            var span = table.Span(line);
            var tokens = line < valid ? syntax![line] : null;
            var ti = 0;
            for (var col = 0; col < span.Length; col++)
            {
                var c = span[col];
                var isOpen = openers.TryGetValue(c, out var k);
                var isClose = !isOpen && closers.TryGetValue(c, out k);
                if (!isOpen && !isClose) continue;

                if (tokens is not null && tokens.Count > 0)
                {
                    while (ti + 1 < tokens.Count && tokens.Start(ti + 1) <= col) ti++;
                    if (tokens.TokenType(ti) != 0) continue;
                }

                var index = offsets.Count;
                offsets.Add(start + col);
                chars.Add(c);
                if (isOpen)
                {
                    kinds.Add((byte)(128 + k));
                    partners.Add(-1);
                    depths.Add(stack.Count);
                    stack.Add(index);
                    continue;
                }

                kinds.Add((byte)k);
                // Find the innermost waiting opener of this kind.
                var at = -1;
                for (var s = stack.Count - 1; s >= 0; s--)
                {
                    if ((kinds[stack[s]] & 127) == k)
                    {
                        at = s;
                        break;
                    }
                }

                if (at < 0)
                {
                    partners.Add(-1);
                    depths.Add(stack.Count);
                    continue;
                }

                var opener = stack[at];
                stack.RemoveRange(at, stack.Count - at);
                partners.Add(opener);
                partners[opener] = index;
                depths.Add(depths[opener]);
            }
        }

        return new BracketIndex(version, [.. offsets], [.. partners], [.. depths], [.. kinds], [.. chars]);
    }

    private static readonly ImmutableArray<BracketPair> DefaultPairs = [new("(", ")"), new("[", "]"), new("{", "}")];
}
