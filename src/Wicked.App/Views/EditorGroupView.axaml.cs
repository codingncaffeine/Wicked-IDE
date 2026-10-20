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
            if (_group is not null) _group.PropertyChanged += OnGroupChanged;
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
        if (tab is null)
        {
            Host.Content = null;
            return;
        }

        var services = await EditorServices.Ready;
        if (_group?.Active != tab) return;
        var editor = tab.EditorFor(services);
        Host.Content = editor;
        // Focus once the editor is in the window: a control not yet attached ignores Focus() without a word.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_group?.Active == tab) editor.Focus();
        }, Avalonia.Threading.DispatcherPriority.Loaded);
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
