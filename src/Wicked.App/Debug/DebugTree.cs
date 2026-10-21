using System.Collections.ObjectModel;
using Wicked.Core.Debug;

namespace Wicked.App.Debug;

/// <summary>
/// A tree of <see cref="DebugNode"/>s shown as a flat list (so a long list of variables virtualises): opening a row inserts
/// its children after it, closing removes them. Rows that were open stay open after the next stop, and rows whose value
/// changed are marked. Runs on the UI thread; the children come from a loader that works on a worker.
/// </summary>
public sealed class DebugTree(Func<DebugNode, Task<List<DebugNode>>> load)
{
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private Dictionary<string, string> _before = [];
    private int _generation;
    private bool _defaulted;

    public ObservableCollection<DebugNode> Rows { get; } = [];

    public List<DebugNode> Roots { get; private set; } = [];

    /// <summary>Replaces the roots (a new stop): rows open before open again, values that differ from before are marked.</summary>
    public async Task SetRootsAsync(List<DebugNode> roots, bool openFirstScope = false)
    {
        var generation = ++_generation;
        if (Roots.Count > 0) _before = Snapshot(Roots);
        Roots = roots;
        if (openFirstScope && !_defaulted && roots.FirstOrDefault(r => r.Kind == DebugNodeKind.Scope && !r.Expensive) is { } first)
        {
            // Locals open by themselves the first time, as in VS Code.
            _open.Add(first.Path);
            _defaulted = true;
        }

        Rows.Clear();
        foreach (var root in roots) Rows.Add(root);
        for (var i = 0; i < roots.Count; i++)
        {
            if (generation != _generation) return;
            await OpenAgainAsync(roots[i], generation).ConfigureAwait(true);
        }
    }

    /// <summary>The values of every row that has been fetched, by path: what the next stop is compared with.</summary>
    private static Dictionary<string, string> Snapshot(IEnumerable<DebugNode> roots)
    {
        var into = new Dictionary<string, string>(StringComparer.Ordinal);
        Remember(roots, into);
        return into;
    }

    private static void Remember(IEnumerable<DebugNode> nodes, Dictionary<string, string> into)
    {
        foreach (var n in nodes)
        {
            if (n.Kind is DebugNodeKind.Variable or DebugNodeKind.Watch && n.Value.Length > 0) into[n.Path] = n.Value;
            if (n.ChildrenLoaded) Remember(n.Children, into);
        }
    }

    /// <summary>Opens a row that was open at the last stop (and the rows inside it that were).</summary>
    private async Task OpenAgainAsync(DebugNode node, int generation)
    {
        if (node.Kind is DebugNodeKind.Variable or DebugNodeKind.Watch && _before.TryGetValue(node.Path, out var old)) node.Changed = old != node.Value;
        if (!node.CanExpand || !_open.Contains(node.Path) || node.Expensive) return;
        await OpenAsync(node).ConfigureAwait(true);
        if (generation != _generation) return;
        foreach (var child in node.Children.ToList())
        {
            if (generation != _generation) return;
            await OpenAgainAsync(child, generation).ConfigureAwait(true);
        }
    }

    public Task ToggleAsync(DebugNode node)
    {
        if (!node.IsExpanded) return OpenAsync(node);
        Close(node);
        return Task.CompletedTask;
    }

    /// <summary>Opens a row: fetches its children the first time, then shows them below it.</summary>
    public async Task OpenAsync(DebugNode node)
    {
        if (!node.CanExpand || node.IsExpanded) return;
        node.IsExpanded = true;
        _open.Add(node.Path);
        if (!node.ChildrenLoaded)
        {
            node.IsLoading = true;
            var generation = _generation;
            List<DebugNode> children;
            try
            {
                children = await load(node).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is DapException or IOException)
            {
                children = [new DebugNode(DebugNodeKind.Message, ex.Message) { Parent = node, Session = node.Session }];
            }

            node.IsLoading = false;
            if (generation != _generation) return;
            foreach (var child in children)
            {
                child.Parent = node;
                child.Depth = node.Depth + 1;
                if (child.Kind is DebugNodeKind.Variable && _before.TryGetValue(child.Path, out var old)) child.Changed = old != child.Value;
            }

            node.Children = children;
            node.ChildrenLoaded = true;
        }

        if (!node.IsExpanded) return;
        var at = Rows.IndexOf(node);
        if (at < 0) return;
        foreach (var child in node.Children) Rows.Insert(++at, child);
    }

    public void Close(DebugNode node)
    {
        if (!node.IsExpanded) return;
        node.IsExpanded = false;
        _open.Remove(node.Path);
        var at = Rows.IndexOf(node);
        if (at < 0) return;
        while (at + 1 < Rows.Count && Rows[at + 1].Depth > node.Depth) Rows.RemoveAt(at + 1);
    }

    /// <summary>A new session: nothing is remembered from the last one.</summary>
    public void Reset()
    {
        Clear();
        _open.Clear();
        _before = [];
        _defaulted = false;
    }

    /// <summary>Forgets the roots and the rows (the program is running again, or the session is over).</summary>
    public void Clear()
    {
        _generation++;
        if (Roots.Count > 0) _before = Snapshot(Roots);
        Roots = [];
        Rows.Clear();
    }
}
