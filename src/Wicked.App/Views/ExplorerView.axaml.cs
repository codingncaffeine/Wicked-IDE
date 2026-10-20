using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wicked.App.Workbench;

namespace Wicked.App.Views;

/// <summary>The file tree: a click previews a file, a double click (or Enter) keeps it open, a folder click opens it.</summary>
public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();
        Tree.AddHandler(PointerReleasedEvent, OnTreeClick, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.DoubleTapped += (_, _) => OpenSelected(preview: false);
        Tree.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            OpenSelected(preview: false);
            e.Handled = true;
        };
    }

    private Wicked.App.Workbench.Workbench? Bench => DataContext as Wicked.App.Workbench.Workbench;

    private void OnTreeClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (Tree.SelectedItem is ExplorerNode { IsDirectory: true } folder && e.Source is not null && IsOnItemContent(e.Source))
        {
            folder.IsExpanded = !folder.IsExpanded;
            return;
        }

        OpenSelected(preview: true);
    }

    private static bool IsOnItemContent(object source) => source is TextBlock or Shell.Icon or StackPanel;

    private void OpenSelected(bool preview)
    {
        if (Tree.SelectedItem is ExplorerNode { IsDirectory: false } file && Bench is { } bench) _ = bench.OpenAsync(file.FullPath, preview);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (Bench is not { Dialogs: { } dialogs } bench) return;
        if (await dialogs.PickFolderAsync() is { } folder) await bench.OpenFolderAsync(folder);
    }
}
