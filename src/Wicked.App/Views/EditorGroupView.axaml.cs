using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wicked.App.Editor;
using Wicked.App.Workbench;

namespace Wicked.App.Views;

/// <summary>One group: the tab strip and the active tab's editor (made once per tab and kept while it lives).</summary>
public partial class EditorGroupView : UserControl
{
    private EditorGroup? _group;

    public EditorGroupView()
    {
        InitializeComponent();
        TabStrip.AddHandler(PointerReleasedEvent, OnTabPointer, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(GotFocusEvent, (_, _) => _group?.Workbench.Focus(_group), handledEventsToo: true);
        // Out of the window, the view lets go of its editor so another view may show it.
        DetachedFromVisualTree += (_, _) => Host.Content = null;
        AttachedToVisualTree += (_, _) => _ = ShowActiveAsync();
        DataContextChanged += (_, _) =>
        {
            if (_group is not null) _group.PropertyChanged -= OnGroupChanged;
            _group = DataContext as EditorGroup;
            if (_group is not null)
            {
                _group.PropertyChanged += OnGroupChanged;
                _group.Workbench.RecentFolders.CollectionChanged += (_, _) =>
                {
                    if (_group?.Active is null) ShowRecent();
                };
                _group.Workbench.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(Wicked.App.Workbench.Workbench.Root)) ShowRecent();
                };
            }
            _ = ShowActiveAsync();
        };
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorGroup.Active)) _ = ShowActiveAsync();
    }

    /// <summary>Puts the active tab's editor in the host and gives it the keyboard.</summary>
    private async Task ShowActiveAsync()
    {
        var tab = _group?.Active;
        Welcome.IsVisible = tab is null;
        ShowCrumbs(tab);
        if (tab is null) ShowRecent();
        if (tab is null)
        {
            Host.Content = null;
            FindBar.IsVisible = false;
            return;
        }

        if (tab.CustomView is { } custom)
        {
            Host.Content = custom;
            FindBar.IsVisible = false;
            return;
        }

        var services = await EditorServices.Ready;
        if (_group?.Active != tab) return;
        var fresh = tab.Editor is null;
        var editor = tab.EditorFor(services, _group.Workbench.Settings, _group.Workbench.Snippets);
        if (fresh)
        {
            var bench = _group.Workbench;
            editor.LostFocus += (_, _) => bench.EditorLostFocus(tab);
        }

        Host.Content = editor;
        ShowFind(editor);
        // Focus once the editor is in the window: a control not yet attached ignores Focus() without a word.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_group?.Active == tab) editor.Focus();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>The find widget belongs to the editor shown: it follows its open state and its query.</summary>
    private void ShowFind(CodeEditor editor)
    {
        if (_findOf is not null) _findOf.PropertyChanged -= OnFindChanged;
        _findOf = editor.Find;
        _findOf.PropertyChanged += OnFindChanged;
        FindBar.DataContext = _findOf;
        FindBar.IsVisible = _findOf.IsOpen;
    }

    private FindViewModel? _findOf;

    private void OnFindChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FindViewModel.IsOpen) && _findOf is not null) FindBar.IsVisible = _findOf.IsOpen;
    }

    /// <summary>The active file's place in the folder: one crumb per folder, the file last.</summary>
    private void ShowCrumbs(EditorTab? tab)
    {
        var root = _group?.Workbench.Root;
        var path = tab?.Path;
        Crumbs.IsVisible = path is not null;
        if (path is null) return;
        var relative = root is not null && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? Path.GetRelativePath(root, path) : path;
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var items = new List<Control>();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) items.Add(new Shell.Icon { Data = (Avalonia.Media.Geometry?)this.FindResource("Icon.ChevronRight"), Size = 12, Classes = { "crumbsep" } });
            items.Add(new TextBlock { Text = parts[i], Classes = { i == parts.Length - 1 ? "crumb-last" : "crumb" } });
        }

        CrumbList.ItemsSource = items;
    }

    /// <summary>The welcome page's recent folders: a click opens one.</summary>
    private void ShowRecent()
    {
        var bench = _group?.Workbench;
        var folders = bench?.RecentFolders.ToList() ?? [];
        Recent.IsVisible = folders.Count > 0 && bench?.Root is null;
        RecentList.ItemsSource = folders.Select(folder =>
        {
            var button = new Button { Classes = { "recent" }, Tag = folder, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            button.Content = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) },
                    new TextBlock { Text = Path.GetDirectoryName(folder) ?? "", Classes = { "muted" }, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis },
                },
            };
            button.Click += (_, _) =>
            {
                if (bench is not null) _ = bench.OpenFolderAsync(folder);
            };
            return button;
        }).ToList();
    }

    private void OnTabPointer(object? sender, PointerReleasedEventArgs e)
    {
        if (_group is null || e.Source is not Avalonia.Visual source) return;
        var tabBorder = source.FindAncestorOfType<Border>(includeSelf: true);
        while (tabBorder is not null && !tabBorder.Classes.Contains("tab")) tabBorder = tabBorder.FindAncestorOfType<Border>();
        if (tabBorder?.Tag is not EditorTab tab) return;
        if (e.InitialPressMouseButton == MouseButton.Middle) _ = _group.Workbench.CloseAsync(tab, _group);
        else if (e.InitialPressMouseButton == MouseButton.Left && source.FindAncestorOfType<Button>(includeSelf: true) is null) _group.Activate(tab);
    }

    private void OnClose(object? sender, RoutedEventArgs e)
    {
        if (_group is not null && (sender as Button)?.Tag is EditorTab tab) _ = _group.Workbench.CloseAsync(tab, _group);
        e.Handled = true;
    }
}
