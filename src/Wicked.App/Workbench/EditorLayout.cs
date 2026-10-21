namespace Wicked.App.Workbench;

/// <summary>Where a new editor group goes beside another, or which neighbour to go to.</summary>
public enum SplitDirection
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>A part of the editor area: one group, or a row or column of parts, each with its share of the room.</summary>
public abstract class LayoutNode
{
    public LayoutBranch? Parent { get; internal set; }

    /// <summary>The part's share of its row or column (star sizes; the splitters change them).</summary>
    public double Size { get; set; } = 1;
}

public sealed class LayoutLeaf(EditorGroup group) : LayoutNode
{
    public EditorGroup Group { get; } = group;
}

/// <summary>Parts side by side (<see cref="Horizontal"/>) or one above another.</summary>
public sealed class LayoutBranch(bool horizontal) : LayoutNode
{
    public bool Horizontal { get; } = horizontal;

    public List<LayoutNode> Children { get; } = [];
}

/// <summary>
/// The editor groups as VS Code's editor grid lays them out: a tree of rows and columns. Splitting the way a group's row
/// already runs adds a neighbour in it; splitting across it turns the group into a new row or column of two. Closing a
/// group gives its room to its neighbours, and a row or column left with one part becomes that part.
/// </summary>
public sealed class EditorLayout(EditorGroup first)
{
    public LayoutNode Root { get; private set; } = new LayoutLeaf(first);

    /// <summary>The groups in reading order: left to right, top to bottom.</summary>
    public IEnumerable<EditorGroup> Groups => Leaves(Root).Select(l => l.Group);

    private static IEnumerable<LayoutLeaf> Leaves(LayoutNode node) =>
        node is LayoutLeaf leaf ? [leaf] : ((LayoutBranch)node).Children.SelectMany(Leaves);

    private LayoutLeaf LeafOf(EditorGroup group) => Leaves(Root).First(l => l.Group == group);

    public bool Contains(EditorGroup group) => Leaves(Root).Any(l => l.Group == group);

    /// <summary>Sizes were set here (not dragged): the view draws them rather than reading back what it showed.</summary>
    public bool SizesReset { get; set; }

    /// <summary>Every part an even share of its row or column (Reset Editor Group Sizes).</summary>
    public void EvenSizes()
    {
        var nodes = new Stack<LayoutNode>([Root]);
        while (nodes.TryPop(out var node))
        {
            node.Size = 1;
            if (node is LayoutBranch branch) foreach (var child in branch.Children) nodes.Push(child);
        }

        SizesReset = true;
    }

    /// <summary>Puts <paramref name="added"/> beside <paramref name="existing"/>, halving its room.</summary>
    public void Split(EditorGroup existing, EditorGroup added, SplitDirection direction)
    {
        var leaf = LeafOf(existing);
        var horizontal = direction is SplitDirection.Left or SplitDirection.Right;
        var before = direction is SplitDirection.Left or SplitDirection.Up;
        var fresh = new LayoutLeaf(added);
        if (leaf.Parent is { } parent && parent.Horizontal == horizontal)
        {
            var at = parent.Children.IndexOf(leaf);
            fresh.Size = leaf.Size / 2;
            leaf.Size /= 2;
            fresh.Parent = parent;
            parent.Children.Insert(before ? at : at + 1, fresh);
            return;
        }

        var branch = new LayoutBranch(horizontal) { Size = leaf.Size };
        Replace(leaf, branch);
        leaf.Size = 1;
        leaf.Parent = branch;
        fresh.Parent = branch;
        branch.Children.AddRange(before ? [fresh, leaf] : [leaf, fresh]);
    }

    /// <summary>Takes a group out; its room goes to the parts beside it.</summary>
    public void Remove(EditorGroup group)
    {
        var leaf = LeafOf(group);
        if (leaf.Parent is not { } parent) return; // the last group stays
        parent.Children.Remove(leaf);
        if (parent.Children.Count != 1) return;
        var only = parent.Children[0];
        if (only is LayoutBranch inner && parent.Parent is { } outer && inner.Horizontal == outer.Horizontal)
        {
            // A row left inside a row (or a column in a column) joins it: its parts share the room it had.
            var at = outer.Children.IndexOf(parent);
            var total = inner.Children.Sum(c => c.Size);
            outer.Children.RemoveAt(at);
            foreach (var part in inner.Children)
            {
                part.Size = parent.Size * part.Size / total;
                part.Parent = outer;
            }

            outer.Children.InsertRange(at, inner.Children);
            return;
        }

        only.Size = parent.Size;
        Replace(parent, only);
    }

    private void Replace(LayoutNode old, LayoutNode with)
    {
        var parent = old.Parent;
        with.Parent = parent;
        if (parent is null) Root = with;
        else parent.Children[parent.Children.IndexOf(old)] = with;
    }

    /// <summary>
    /// The group next to <paramref name="group"/> in a direction (Ctrl+K Ctrl+Arrow), or null at the edge: up to the nearest
    /// row or column that runs that way, one step along it, then down to the part nearest the side it was entered from.
    /// </summary>
    public EditorGroup? Neighbour(EditorGroup group, SplitDirection direction)
    {
        var horizontal = direction is SplitDirection.Left or SplitDirection.Right;
        var forward = direction is SplitDirection.Right or SplitDirection.Down;
        LayoutNode node = LeafOf(group);
        while (node.Parent is { } parent)
        {
            if (parent.Horizontal == horizontal)
            {
                var at = parent.Children.IndexOf(node) + (forward ? 1 : -1);
                if (at >= 0 && at < parent.Children.Count) return Nearest(parent.Children[at], horizontal, forward);
            }

            node = parent;
        }

        return null;
    }

    private static EditorGroup Nearest(LayoutNode node, bool horizontal, bool forward)
    {
        while (node is LayoutBranch branch) node = branch.Horizontal == horizontal ? (forward ? branch.Children[0] : branch.Children[^1]) : branch.Children[0];
        return ((LayoutLeaf)node).Group;
    }
}
