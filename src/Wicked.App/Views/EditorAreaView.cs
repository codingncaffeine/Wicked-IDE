using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Wicked.App.Views;

/// <summary>The editor groups side by side, with a draggable gap between each pair; rebuilt when groups come and go.</summary>
public sealed class EditorAreaView : Grid
{
    private Wicked.App.Workbench.Workbench? _bench;

    // One view per group for the group's life: a view holds its tab's editor, which can have only one parent.
    private readonly Dictionary<Wicked.App.Workbench.EditorGroup, EditorGroupView> _views = [];

    public EditorAreaView()
    {
        DataContextChanged += (_, _) =>
        {
            if (_bench is not null) _bench.Groups.CollectionChanged -= OnGroups;
            _bench = DataContext as Wicked.App.Workbench.Workbench;
            if (_bench is not null) _bench.Groups.CollectionChanged += OnGroups;
            Rebuild();
        };
    }

    private void OnGroups(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        if (_bench is not null)
        {
            foreach (var gone in _views.Keys.Where(g => !_bench.Groups.Contains(g)).ToList()) _views.Remove(gone);
        }

        ColumnDefinitions.Clear();
        if (_bench is null) return;
        for (var i = 0; i < _bench.Groups.Count; i++)
        {
            if (i > 0)
            {
                ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1)));
                var splitter = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Classes = { "groupgap" }, HorizontalAlignment = HorizontalAlignment.Stretch };
                SetColumn(splitter, ColumnDefinitions.Count - 1);
                Children.Add(splitter);
            }

            ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 120 });
            var group = _bench.Groups[i];
            if (!_views.TryGetValue(group, out var view)) _views[group] = view = new EditorGroupView { DataContext = group };
            SetColumn(view, ColumnDefinitions.Count - 1);
            Children.Add(view);
        }
    }
}
