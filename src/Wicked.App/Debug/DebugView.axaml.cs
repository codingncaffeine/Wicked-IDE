using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Wicked.App.Debug;

/// <summary>
/// The Run and Debug panel: the configuration and Start, then Variables, Watch, Call Stack and Breakpoints. Rows are the
/// controller's; this only turns clicks into its calls.
/// </summary>
public partial class DebugView : UserControl
{
    private readonly Dictionary<string, (int Row, string Chevron)> _sections = new()
    {
        ["VariablesBody"] = (2, "VariablesChevron"),
        ["WatchBody"] = (4, "WatchChevron"),
        ["StackBody"] = (6, "StackChevron"),
        ["BreakpointsBody"] = (8, "BreakpointsChevron"),
    };

    private bool _enableAll;

    public DebugView()
    {
        InitializeComponent();
        StackList.Tapped += (_, _) =>
        {
            if (Controller is not { } c || StackList.SelectedItem is not StackRow row) return;
            switch (row.Kind)
            {
                case StackRowKind.Frame: _ = c.SelectFrameAsync(row); break;
                case StackRowKind.Thread: _ = c.SelectThreadAsync(row); break;
                case StackRowKind.More: _ = c.LoadMoreFramesAsync(row); break;
            }
        };
        BreakpointsList.Tapped += (_, e) =>
        {
            if (Controller is not { } c || BreakpointsList.SelectedItem is not BreakpointRow row || e.Source is CheckBox || (e.Source as Visual)?.FindAncestorOfType<CheckBox>() is not null) return;
            _ = c.GoToBreakpointAsync(row);
        };
    }

    private DebugController? Controller => DataContext as DebugController;

    private static T? Node<T>(object? sender) where T : class => (sender as StyledElement)?.DataContext as T;

    private DebugTree? TreeOf(object? sender) => (sender as Visual)?.FindAncestorOfType<ListBox>()?.Name == "WatchList" ? Controller?.WatchTree : Controller?.Variables;

    private void OnStart(object? sender, RoutedEventArgs e)
    {
        if (Controller is { } c) _ = c.StartChosen?.Invoke() ?? c.StartAsync();
    }

    private void OnOpenLaunch(object? sender, RoutedEventArgs e)
    {
        if (Controller is { } c) _ = c.OpenLaunchJsonAsync();
    }

    private void OnCreateLaunch(object? sender, RoutedEventArgs e)
    {
        if (Controller is { } c) _ = c.CreateLaunchJsonAsync();
    }

    /// <summary>A section's header: its list folds away and the others share the room.</summary>
    private void OnSectionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string body } || !_sections.TryGetValue(body, out var section)) return;
        var rows = Sections.RowDefinitions;
        var open = rows[section.Row].Height.IsAbsolute;
        rows[section.Row].Height = open ? GridLength.Star : new GridLength(0);
        Sections.Children.OfType<ListBox>().First(l => Grid.GetRow(l) == section.Row).IsVisible = open;
        if (this.FindControl<Wicked.App.Shell.Icon>(section.Chevron) is { } chevron) chevron.Data = (Geometry?)Application.Current?.FindResource(open ? "Icon.ChevronDown" : "Icon.ChevronRight");
    }

    // ── Variables and Watch ──────────────────────────────────────────────────────────────────────

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        if (Node<DebugNode>(sender) is { } node && TreeOf(sender) is { } tree) _ = tree.ToggleAsync(node);
        e.Handled = true;
    }

    private void OnVariableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Node<DebugNode>(sender) is not { } node || Controller is not { } c) return;
        if (node.CanSet) c.BeginEdit(node);
        else if (node.CanExpand && TreeOf(sender) is { } tree) _ = tree.ToggleAsync(node);
    }

    private void OnWatchDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Node<DebugNode>(sender) is not { } node || Controller is not { } c) return;
        if (node.IsWatch) c.EditWatch(node);
        else if (node.CanSet) c.BeginEdit(node);
    }

    private void OnSetValue(object? sender, RoutedEventArgs e)
    {
        if (Node<DebugNode>(sender) is { } node) Controller?.BeginEdit(node);
    }

    private void OnCopyValue(object? sender, RoutedEventArgs e)
    {
        if (Node<DebugNode>(sender) is { } node) _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(node.Value);
    }

    private void OnCopyExpression(object? sender, RoutedEventArgs e)
    {
        if (Node<DebugNode>(sender) is { } node) _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(node.EvaluateName ?? node.Name);
    }

    private void OnAddToWatch(object? sender, RoutedEventArgs e)
    {
        if (Node<DebugNode>(sender) is { } node) Controller?.AddWatchExpression(node.EvaluateName ?? node.Name);
    }

    private void OnEditKey(object? sender, KeyEventArgs e)
    {
        if (Node<DebugNode>(sender) is not { } node || Controller is not { } c) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = c.CommitEditAsync(node);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            c.CancelEdit(node);
        }
    }

    private void OnAddWatch(object? sender, RoutedEventArgs e) => Controller?.AddWatch();

    private void OnRemoveAllWatches(object? sender, RoutedEventArgs e) => Controller?.RemoveAllWatches();

    private void OnEditWatch(object? sender, RoutedEventArgs e)
    {
        if (Node<DebugNode>(sender) is { } node) Controller?.EditWatch(node);
    }

    private void OnRemoveWatch(object? sender, RoutedEventArgs e)
    {
        if (Node<DebugNode>(sender) is { } node) Controller?.RemoveWatch(node);
    }

    // ── Breakpoints ──────────────────────────────────────────────────────────────────────────────

    private void OnAddFunctionBreakpoint(object? sender, RoutedEventArgs e) => Controller?.AddFunctionBreakpoint();

    private void OnToggleAllBreakpoints(object? sender, RoutedEventArgs e)
    {
        _enableAll = !_enableAll;
        Controller?.Breakpoints.SetAllEnabled(_enableAll);
    }

    private void OnRemoveAllBreakpoints(object? sender, RoutedEventArgs e) => Controller?.Breakpoints.RemoveAll();

    private void OnBreakpointDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (Node<BreakpointRow>(sender) is { } row) Controller?.EditBreakpointRow(row);
    }

    private void OnEditBreakpoint(object? sender, RoutedEventArgs e)
    {
        if (Node<BreakpointRow>(sender) is { } row) Controller?.EditBreakpointRow(row);
    }

    private void OnRemoveBreakpoint(object? sender, RoutedEventArgs e)
    {
        if (Node<BreakpointRow>(sender) is { } row) Controller?.RemoveBreakpoint(row);
    }
}
