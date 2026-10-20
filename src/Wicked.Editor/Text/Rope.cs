using System.Diagnostics;
using System.Text;

namespace Wicked.Editor.Text;

/// <summary>
/// An immutable text: a B-tree of string chunks in which every node knows its length and how many line breaks it holds.
/// An edit copies only the path from the root to the touched chunks (O(log n)), so the UI thread can edit a 100k-line
/// file in microseconds and hand the result to any number of worker threads without copying or locking: every version
/// stays valid for as long as someone holds it. Line breaks are '\n' only; files are normalised when they are read.
/// </summary>
public sealed class Rope
{
    internal const int MaxLeaf = 1024;
    internal const int MinLeaf = MaxLeaf / 4;
    internal const int MaxChildren = 16;

    public static readonly Rope Empty = new(new Leaf(""));

    private readonly Node _root;

    private Rope(Node root) => _root = root;

    /// <summary>The number of UTF-16 code units.</summary>
    public int Length => _root.Length;

    /// <summary>The number of lines: one more than the number of line breaks.</summary>
    public int LineCount => _root.LineBreaks + 1;

    internal int Height => _root.Height;

    public static Rope FromString(string text) => text.Length == 0 ? Empty : new Rope(Build(text.AsSpan()));

    /// <summary>Reads <paramref name="reader"/> to the end, with no line-break translation.</summary>
    public static Rope FromReader(TextReader reader)
    {
        var leaves = new List<Node>();
        var buffer = new char[MaxLeaf * 3 / 4];
        var carry = 0;
        while (true)
        {
            var read = reader.ReadBlock(buffer, carry, buffer.Length - carry);
            var filled = carry + read;
            if (filled == 0) break;

            // A chunk never ends between the halves of a surrogate pair while more text follows.
            var take = read > 0 && char.IsHighSurrogate(buffer[filled - 1]) ? filled - 1 : filled;
            if (take > 0) leaves.Add(new Leaf(new string(buffer, 0, take)));
            carry = filled - take;
            if (carry > 0) buffer[0] = buffer[take];
            if (read == 0) break;
        }

        return leaves.Count == 0 ? Empty : new Rope(BuildUp(leaves));
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────

    public char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length) throw new ArgumentOutOfRangeException(nameof(index));
            var node = _root;
            while (node is Branch b)
            {
                foreach (var child in b.Children)
                {
                    if (index < child.Length)
                    {
                        node = child;
                        break;
                    }

                    index -= child.Length;
                }
            }

            return ((Leaf)node).Text[index];
        }
    }

    public override string ToString() => Substring(0, Length);

    public string Substring(int start, int length)
    {
        CheckRange(start, length);
        if (length == 0) return "";
        return string.Create(length, (this, start), static (span, state) => state.Item1.CopyTo(state.start, span));
    }

    /// <summary>Copies <paramref name="destination"/>.Length characters starting at <paramref name="start"/>.</summary>
    public void CopyTo(int start, Span<char> destination)
    {
        CheckRange(start, destination.Length);
        foreach (var chunk in Chunks(start, destination.Length))
        {
            chunk.Span.CopyTo(destination);
            destination = destination[chunk.Length..];
        }
    }

    /// <summary>The text from <paramref name="start"/> for <paramref name="length"/> characters, as the chunks that hold it, in order.</summary>
    public IEnumerable<ReadOnlyMemory<char>> Chunks(int start, int length)
    {
        CheckRange(start, length);
        return ChunksIterator(_root, start, length);
    }

    private static IEnumerable<ReadOnlyMemory<char>> ChunksIterator(Node root, int start, int length)
    {
        if (length == 0) yield break;
        var stack = new Stack<(Node Node, int Offset)>();
        stack.Push((root, 0));
        var end = start + length;
        while (stack.Count > 0)
        {
            var (node, offset) = stack.Pop();
            if (offset >= end || offset + node.Length <= start) continue;
            if (node is Leaf leaf)
            {
                var from = Math.Max(start, offset) - offset;
                var to = Math.Min(end, offset + leaf.Length) - offset;
                yield return leaf.Text.AsMemory(from, to - from);
                continue;
            }

            var children = ((Branch)node).Children;
            var childOffsets = new int[children.Length];
            var o = offset;
            for (var i = 0; i < children.Length; i++)
            {
                childOffsets[i] = o;
                o += children[i].Length;
            }

            for (var i = children.Length - 1; i >= 0; i--) stack.Push((children[i], childOffsets[i]));
        }
    }

    // ── Lines (0-based) ──────────────────────────────────────────────────────────────────────────

    /// <summary>The offset where <paramref name="line"/> starts.</summary>
    public int LineStart(int line)
    {
        if ((uint)line >= (uint)LineCount) throw new ArgumentOutOfRangeException(nameof(line));
        if (line == 0) return 0;

        // Find the line-th break; the line starts just after it.
        var breaks = line;
        var offset = 0;
        var node = _root;
        while (node is Branch b)
        {
            foreach (var child in b.Children)
            {
                if (breaks <= child.LineBreaks)
                {
                    node = child;
                    break;
                }

                breaks -= child.LineBreaks;
                offset += child.Length;
            }
        }

        var text = ((Leaf)node).Text.AsSpan();
        var at = -1;
        for (var i = 0; i < breaks; i++)
        {
            var next = text[(at + 1)..].IndexOf('\n');
            Debug.Assert(next >= 0);
            at += next + 1;
        }

        return offset + at + 1;
    }

    /// <summary>The length of <paramref name="line"/>, its line break not included.</summary>
    public int LineLength(int line)
    {
        var start = LineStart(line);
        var end = line + 1 < LineCount ? LineStart(line + 1) - 1 : Length;
        return end - start;
    }

    /// <summary>The text of <paramref name="line"/>, its line break not included.</summary>
    public string Line(int line)
    {
        var start = LineStart(line);
        var end = line + 1 < LineCount ? LineStart(line + 1) - 1 : Length;
        return Substring(start, end - start);
    }

    /// <summary>The line that holds <paramref name="offset"/> (an offset at a line break belongs to the line it ends).</summary>
    public int LineOf(int offset)
    {
        if ((uint)offset > (uint)Length) throw new ArgumentOutOfRangeException(nameof(offset));
        var line = 0;
        var node = _root;
        while (node is Branch b)
        {
            var descended = false;
            foreach (var child in b.Children)
            {
                if (offset < child.Length)
                {
                    node = child;
                    descended = true;
                    break;
                }

                offset -= child.Length;
                line += child.LineBreaks;
            }

            if (!descended) return line;
        }

        return line + CountBreaks(((Leaf)node).Text.AsSpan(0, offset));
    }

    /// <summary>The (line, column) of <paramref name="offset"/>.</summary>
    public (int Line, int Column) Position(int offset)
    {
        var line = LineOf(offset);
        return (line, offset - LineStart(line));
    }

    /// <summary>The offset of (<paramref name="line"/>, <paramref name="column"/>), the column clamped to the line.</summary>
    public int Offset(int line, int column)
    {
        line = Math.Clamp(line, 0, LineCount - 1);
        return LineStart(line) + Math.Clamp(column, 0, LineLength(line));
    }

    // ── Editing ──────────────────────────────────────────────────────────────────────────────────

    public Rope Insert(int offset, string text)
    {
        if ((uint)offset > (uint)Length) throw new ArgumentOutOfRangeException(nameof(offset));
        if (text.Length == 0) return this;
        var nodes = Insert(_root, offset, text);
        return new Rope(Root(nodes));
    }

    public Rope Remove(int start, int length)
    {
        CheckRange(start, length);
        if (length == 0) return this;
        if (length == Length) return Empty;
        var node = Remove(_root, start, length);
        if (node is null) return Empty;
        while (node is Branch { Children.Length: 1 } single) node = single.Children[0];
        return new Rope(node);
    }

    public Rope Replace(int start, int length, string text) => Remove(start, length).Insert(start, text);

    private static Node Root(List<Node> nodes)
    {
        while (nodes.Count > 1) nodes = Group(nodes);
        return nodes[0];
    }

    /// <summary>Inserts into <paramref name="node"/>; returns the node or, when it overflowed, the nodes that replace it (all the same height).</summary>
    private static List<Node> Insert(Node node, int offset, string text)
    {
        if (node is Leaf leaf)
        {
            var joined = string.Concat(leaf.Text.AsSpan(0, offset), text, leaf.Text.AsSpan(offset));
            return joined.Length <= MaxLeaf ? [new Leaf(joined)] : SplitLeaves(joined.AsSpan());
        }

        var children = ((Branch)node).Children;
        var index = 0;
        while (index < children.Length - 1 && offset > children[index].Length)
        {
            offset -= children[index].Length;
            index++;
        }

        var replaced = Insert(children[index], offset, text);
        var list = new List<Node>(children.Length + replaced.Count);
        list.AddRange(children.AsSpan(0, index));
        list.AddRange(replaced);
        list.AddRange(children.AsSpan(index + 1));
        return list.Count <= MaxChildren ? [new Branch([.. list])] : Group(list);
    }

    /// <summary>Removes a range inside <paramref name="node"/>; null when nothing of it is left.</summary>
    private static Node? Remove(Node node, int start, int length)
    {
        if (node is Leaf leaf)
        {
            if (start == 0 && length == leaf.Length) return null;
            return new Leaf(string.Concat(leaf.Text.AsSpan(0, start), leaf.Text.AsSpan(start + length)));
        }

        var children = ((Branch)node).Children;
        var kept = new List<Node>(children.Length);
        var offset = 0;
        var end = start + length;
        foreach (var child in children)
        {
            var childStart = offset;
            var childEnd = offset + child.Length;
            offset = childEnd;
            if (childEnd <= start || childStart >= end)
            {
                kept.Add(child);
                continue;
            }

            var from = Math.Max(start, childStart) - childStart;
            var to = Math.Min(end, childEnd) - childStart;
            if (Remove(child, from, to - from) is { } rest) kept.Add(rest);
        }

        if (kept.Count == 0) return null;
        Rebalance(kept);
        return new Branch([.. kept]);
    }

    /// <summary>
    /// Merges neighbours that have grown too small after a removal: two small leaves into one, a branch with few
    /// children into its neighbour. Every node in <paramref name="nodes"/> has the same height, and keeps it.
    /// </summary>
    private static void Rebalance(List<Node> nodes)
    {
        for (var i = 0; i < nodes.Count - 1;)
        {
            var a = nodes[i];
            var b = nodes[i + 1];
            if (a is Leaf la && b is Leaf lb && (la.Length < MinLeaf || lb.Length < MinLeaf) && la.Length + lb.Length <= MaxLeaf)
            {
                nodes[i] = new Leaf(la.Text + lb.Text);
                nodes.RemoveAt(i + 1);
                continue;
            }

            if (a is Branch ba && b is Branch bb && (ba.Children.Length < MaxChildren / 4 || bb.Children.Length < MaxChildren / 4)
                && ba.Children.Length + bb.Children.Length <= MaxChildren)
            {
                nodes[i] = new Branch([.. ba.Children, .. bb.Children]);
                nodes.RemoveAt(i + 1);
                continue;
            }

            i++;
        }
    }

    private static Node Build(ReadOnlySpan<char> text) => BuildUp(SplitLeaves(text));

    private static Node BuildUp(List<Node> nodes) => Root(nodes);

    /// <summary>Cuts text into leaves of about three quarters full, so the next few edits fit without splitting.</summary>
    private static List<Node> SplitLeaves(ReadOnlySpan<char> text)
    {
        var target = MaxLeaf * 3 / 4;
        var leaves = new List<Node>(text.Length / target + 1);
        while (text.Length > 0)
        {
            var take = text.Length <= MaxLeaf ? text.Length : Math.Min(target, text.Length);
            if (take < text.Length && char.IsHighSurrogate(text[take - 1])) take--;
            leaves.Add(new Leaf(text[..take].ToString()));
            text = text[take..];
        }

        return leaves;
    }

    /// <summary>Groups same-height nodes into branches of at most <see cref="MaxChildren"/>, sized evenly.</summary>
    private static List<Node> Group(List<Node> nodes)
    {
        var groups = (nodes.Count + MaxChildren - 1) / MaxChildren;
        var result = new List<Node>(groups);
        var index = 0;
        for (var g = 0; g < groups; g++)
        {
            var size = (nodes.Count - index) / (groups - g);
            result.Add(new Branch([.. nodes.GetRange(index, size)]));
            index += size;
        }

        return result;
    }

    private void CheckRange(int start, int length)
    {
        if (start < 0 || length < 0 || start > Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"[{start}, {start + length}) is outside [0, {Length}]");
        }
    }

    internal static int CountBreaks(ReadOnlySpan<char> text) => text.Count('\n');

    /// <summary>Checks the tree's shape: lengths and break counts add up, every leaf at the same depth. For tests.</summary>
    internal void Validate()
    {
        var leafDepth = -1;
        Check(_root, 0);

        void Check(Node node, int depth)
        {
            switch (node)
            {
                case Leaf leaf:
                    if (leafDepth < 0) leafDepth = depth;
                    if (depth != leafDepth) throw new InvalidOperationException($"a leaf at depth {depth}, another at {leafDepth}");
                    if (leaf.Length > MaxLeaf) throw new InvalidOperationException($"a leaf of {leaf.Length}");
                    if (leaf.LineBreaks != CountBreaks(leaf.Text)) throw new InvalidOperationException("a leaf's break count is wrong");
                    break;
                case Branch branch:
                    if (branch.Children.Length is 0 or > MaxChildren) throw new InvalidOperationException($"a branch of {branch.Children.Length}");
                    if (branch.Length != branch.Children.Sum(c => c.Length)) throw new InvalidOperationException("a branch's length is wrong");
                    if (branch.LineBreaks != branch.Children.Sum(c => c.LineBreaks)) throw new InvalidOperationException("a branch's break count is wrong");
                    foreach (var child in branch.Children) Check(child, depth + 1);
                    break;
            }
        }
    }

    private abstract class Node
    {
        public abstract int Length { get; }

        public abstract int LineBreaks { get; }

        public abstract int Height { get; }
    }

    private sealed class Leaf(string text) : Node
    {
        public string Text { get; } = text;

        public override int Length => Text.Length;

        public override int LineBreaks { get; } = CountBreaks(text);

        public override int Height => 0;
    }

    private sealed class Branch : Node
    {
        public Branch(Node[] children)
        {
            Children = children;
            foreach (var c in children)
            {
                _length += c.Length;
                _breaks += c.LineBreaks;
            }

            Height = children[0].Height + 1;
        }

        private readonly int _length;
        private readonly int _breaks;

        public Node[] Children { get; }

        public override int Length => _length;

        public override int LineBreaks => _breaks;

        public override int Height { get; }
    }
}

/// <summary>Reads text files into a <see cref="Rope"/> the way the editor keeps them: '\n' line breaks, the original style remembered.</summary>
public static class TextLoading
{
    /// <summary>
    /// Normalises every line break (CRLF, lone CR, LF) to '\n' and reports which one the file mostly used, so a save can
    /// write it back that way (VS Code's rule: the dominant style wins, a file of mixed breaks is saved consistent).
    /// </summary>
    public static (Rope Text, string LineBreak) Normalise(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                {
                    cr++;
                }
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        var style = crlf > lf && crlf >= cr ? "\r\n" : cr > lf && cr > crlf ? "\r" : "\n";
        if (crlf == 0 && cr == 0) return (Rope.FromString(text), style);
        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                builder.Append('\n');
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else
            {
                builder.Append(text[i]);
            }
        }

        return (Rope.FromString(builder.ToString()), style);
    }
}
