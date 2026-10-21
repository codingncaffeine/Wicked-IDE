using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Layout;
using Wicked.App.Workbench;

namespace Wicked.App.Views;

/// <summary>
/// The editor groups as the workbench's grid lays them out: rows and columns of groups with a draggable gap between
/// neighbours, or one group alone when it is maximised. Rebuilt when the grid changes; the sizes dragged to are kept.
/// </summary>
public sealed class EditorAreaView : Grid
{
    private Wicked.App.Workbench.Workbench? _bench;

    // One view per group for the group's life: a view holds its tab's editor, which can have only one parent.
    private readonly Dictionary<EditorGroup, EditorGroupView> _views = [];

    // The grid each row or column was drawn in, to read the sizes the splitters left before drawing again.
    private readonly Dictionary<LayoutBranch, Grid> _grids = [];

    public EditorAreaView()
    {
        DataContextChanged += (_, _) =>
        {
            if (_bench is not null)
            {
                _bench.Groups.CollectionChanged -= OnGroups;
                _bench.LayoutChanged -= Rebuild;
            }

            _bench = DataContext as Wicked.App.Workbench.Workbench;
            if (_bench is not null)
            {
                _bench.Groups.CollectionChanged += OnGroups;
                _bench.LayoutChanged += Rebuild;
            }

            Rebuild();
        };
    }

    private void OnGroups(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        if (_bench?.Layout is { SizesReset: true } layout) layout.SizesReset = false;
        else KeepSizes();
        foreach (var grid in _grids.Values) grid.Children.Clear();
        _grids.Clear();
        Children.Clear();
        if (_bench is null) return;
        foreach (var gone in _views.Keys.Where(g => !_bench.Groups.Contains(g)).ToList()) _views.Remove(gone);
        Children.Add(_bench.MaximizedGroup is { } alone ? ViewOf(alone) : Build(_bench.Layout.Root));
    }

    private EditorGroupView ViewOf(EditorGroup group)
    {
        if (!_views.TryGetValue(group, out var view)) _views[group] = view = new EditorGroupView { DataContext = group };
        // A view drawn before sits in a grid that is being replaced: it leaves that grid first.
        if (view.Parent is Panel old) old.Children.Remove(view);
        return view;
    }

    private Control Build(LayoutNode node)
    {
        if (node is LayoutLeaf leaf) return ViewOf(leaf.Group);
        var branch = (LayoutBranch)node;
        var grid = new Grid();
        _grids[branch] = grid;
        for (var i = 0; i < branch.Children.Count; i++)
        {
            if (i > 0)
            {
                var splitter = new GridSplitter
                {
                    ResizeDirection = branch.Horizontal ? GridResizeDirection.Columns : GridResizeDirection.Rows,
                    Classes = { "groupgap" },
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                };
                Place(grid, splitter, branch.Horizontal, Add(grid, branch.Horizontal, new GridLength(1)));
            }

            var child = Build(branch.Children[i]);
            var at = Add(grid, branch.Horizontal, new GridLength(Math.Max(0.05, branch.Children[i].Size), GridUnitType.Star));
            if (branch.Horizontal) grid.ColumnDefinitions[at].MinWidth = 120;
            else grid.RowDefinitions[at].MinHeight = 80;
            Place(grid, child, branch.Horizontal, at);
        }

        return grid;
    }

    private static int Add(Grid grid, bool horizontal, GridLength size)
    {
        if (horizontal)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(size));
            return grid.ColumnDefinitions.Count - 1;
        }

        grid.RowDefinitions.Add(new RowDefinition(size));
        return grid.RowDefinitions.Count - 1;
    }

    private static void Place(Grid grid, Control child, bool horizontal, int at)
    {
        if (horizontal) SetColumn(child, at);
        else SetRow(child, at);
        grid.Children.Add(child);
    }

    /// <summary>The sizes the splitters left, read back into the grid's parts (as shares of their row or column).</summary>
    private void KeepSizes()
    {
        foreach (var (branch, grid) in _grids)
        {
            var sizes = branch.Horizontal
                ? grid.ColumnDefinitions.Where((_, i) => i % 2 == 0).Select(c => c.ActualWidth).ToList()
                : grid.RowDefinitions.Where((_, i) => i % 2 == 0).Select(r => r.ActualHeight).ToList();
            var total = sizes.Sum();
            if (total <= 0 || sizes.Count != branch.Children.Count) continue;
            for (var i = 0; i < sizes.Count; i++) branch.Children[i].Size = sizes[i] / total * branch.Children.Count;
        }
    }
}
