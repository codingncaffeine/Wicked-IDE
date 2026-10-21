using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wicked.App.Shell;

namespace Wicked.App.Terminal;

/// <summary>
/// The terminal panel: a strip of tabs (new, split, kill, shell choice) and the active tab's terminals side by side.
/// It follows <see cref="TerminalPanelViewModel"/>; the window puts it in the bottom panel.
/// </summary>
public sealed class TerminalPanelView : UserControl
{
    private readonly Grid _body = new();
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };
    private TerminalPanelViewModel? _panel;
    private TerminalGroupViewModel? _shown;

    public TerminalPanelView()
    {
        var strip = new Border { Classes = { "tabstrip" } };
        var header = new DockPanel { LastChildFill = true };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(4, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(ActionButton("Icon.Plus", "New Terminal", () => _panel?.NewTerminal()));
        actions.Children.Add(ActionButton("Icon.ChevronDown", "Launch Profile...", ShowProfiles));
        actions.Children.Add(ActionButton("Icon.PanelRight", "Split Terminal", () => _panel?.Split()));
        actions.Children.Add(ActionButton("Icon.Trash2", "Kill Terminal", () => _panel?.KillActive()));
        header.Children.Add(actions);
        header.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, Content = _tabs });
        strip.Child = header;

        var root = new DockPanel();
        DockPanel.SetDock(strip, Dock.Top);
        root.Children.Add(strip);
        root.Children.Add(_body);
        Content = root;
        DataContextChanged += (_, _) => Bind(DataContext as TerminalPanelViewModel);
    }

    private static Button ActionButton(string icon, string tip, Action click)
    {
        var button = new Button { Classes = { "quiet" }, Padding = new Thickness(5), Content = new Icon { Data = Application.Current?.FindResource(icon) as Geometry, Size = 14 } };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void ShowProfiles()
    {
        if (_panel is null) return;
        var menu = new ContextMenu();
        foreach (var profile in _panel.Profiles)
        {
            var item = new MenuItem { Header = profile.Name };
            var chosen = profile;
            item.Click += (_, _) => _panel.NewTerminal(chosen);
            menu.Items.Add(item);
        }

        menu.Open(this);
    }

    private void Bind(TerminalPanelViewModel? panel)
    {
        if (_panel is not null)
        {
            _panel.Groups.CollectionChanged -= OnGroupsChanged;
            _panel.PropertyChanged -= OnPanelChanged;
        }

        _panel = panel;
        if (panel is null) return;
        panel.Groups.CollectionChanged += OnGroupsChanged;
        panel.PropertyChanged += OnPanelChanged;
        Rebuild();
    }

    private void OnGroupsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnPanelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalPanelViewModel.ActiveGroup) or nameof(TerminalPanelViewModel.ActiveTerminal)) Rebuild();
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void OnTerminalsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnInstanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TerminalInstanceViewModel.Pane) or nameof(TerminalInstanceViewModel.Title) or nameof(TerminalInstanceViewModel.ExitCode)) Rebuild();
    }

    private readonly HashSet<object> _watched = [];
    private TerminalInstanceViewModel? _lastFocus;
    private List<object>? _bodySignature;

    private void Watch(TerminalGroupViewModel group)
    {
        if (_watched.Add(group))
        {
            group.PropertyChanged += OnGroupChanged;
            group.Terminals.CollectionChanged += OnTerminalsChanged;
        }

        foreach (var t in group.Terminals)
        {
            if (_watched.Add(t)) t.PropertyChanged += OnInstanceChanged;
        }
    }

    private void Rebuild()
    {
        if (_panel is null) return;
        foreach (var group in _panel.Groups) Watch(group);

        _tabs.Children.Clear();
        foreach (var group in _panel.Groups) _tabs.Children.Add(Tab(group));

        // The panes are only taken out and put back when what is shown changed: a title update must not cost the terminal its focus.
        var signature = _panel.ActiveGroup is { } active ? active.Terminals.Select(t => (object?)t.Pane ?? t.StartError ?? (object)t).ToList() : [];
        if (_bodySignature is not null && _bodySignature.SequenceEqual(signature)) return;
        _bodySignature = signature;

        _body.Children.Clear();
        _body.ColumnDefinitions.Clear();
        _shown = _panel.ActiveGroup;
        if (_shown is null) return;
        var column = 0;
        foreach (var terminal in _shown.Terminals)
        {
            if (column > 0)
            {
                _body.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
                var splitter = new GridSplitter { Width = 3, Background = Brushes.Transparent, ResizeDirection = GridResizeDirection.Columns };
                Grid.SetColumn(splitter, column++);
                _body.Children.Add(splitter);
            }

            _body.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1, GridUnitType.Star)) { MinWidth = 80 });
            if (terminal.Pane is { } pane)
            {
                // A pane belongs to one parent at a time: it was in the grid just emptied above.
                if (pane.Parent is Panel old) old.Children.Remove(pane);
                if (_watched.Add(pane))
                {
                    var chosen = terminal;
                    pane.View.GotFocus += (_, _) =>
                    {
                        if (_panel?.ActiveGroup is { } g && g.Active != chosen) _panel.Activate(chosen);
                    };
                }

                Grid.SetColumn(pane, column);
                _body.Children.Add(pane);
                if (terminal == _shown.Active && _panel.ActiveTerminal == terminal && _lastFocus != terminal)
                {
                    _lastFocus = terminal;
                    FocusLater(pane);
                }
            }
            else if (terminal.StartError is { } error)
            {
                var text = new TextBlock { Text = $"The terminal could not be started: {error}", Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, column);
                _body.Children.Add(text);
            }

            column++;
        }
    }

    private static void FocusLater(TerminalPane pane) => Avalonia.Threading.Dispatcher.UIThread.Post(() => pane.View.Focus(), Avalonia.Threading.DispatcherPriority.Input);

    private Border Tab(TerminalGroupViewModel group)
    {
        var active = group == _panel?.ActiveGroup;
        var title = new TextBlock { Text = group.Terminals.Count > 1 ? string.Join(" | ", group.Terminals.Select(t => t.Title)) : group.Title, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
        var close = new Button { Classes = { "tabclose" }, Content = new Icon { Data = Application.Current?.FindResource("Icon.X") as Geometry, Size = 12 } };
        close.Click += (_, e) =>
        {
            foreach (var t in group.Terminals.ToList()) _panel?.Close(t);
            e.Handled = true;
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new Icon { Data = Application.Current?.FindResource("Icon.Terminal") as Geometry, Size = 13 }, title, close } };
        var tab = new Border { Classes = { "tab" }, Child = row };
        if (active) tab.Classes.Add("active");
        tab.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(tab).Properties.IsMiddleButtonPressed) foreach (var t in group.Terminals.ToList()) _panel?.Close(t);
            else _panel?.Activate(group);
        };
        tab.DoubleTapped += (_, _) => Rename(group);
        return tab;
    }

    private void Rename(TerminalGroupViewModel group)
    {
        if (group.Active is not { } terminal) return;
        var box = new TextBox { Text = terminal.Title, MinWidth = 140 };
        var flyout = new Flyout { Content = box };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                _panel?.Rename(terminal, box.Text);
                flyout.Hide();
            }
            else if (e.Key == Key.Escape) flyout.Hide();
        };
        flyout.ShowAt(_tabs);
        box.Focus();
        box.SelectAll();
    }
}
