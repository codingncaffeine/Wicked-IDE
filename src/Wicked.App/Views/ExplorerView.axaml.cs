using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wicked.App.Workbench;

namespace Wicked.App.Views;

/// <summary>
/// The file tree: a click previews a file, a double click (or Enter) keeps it open, a folder click opens it; a right
/// click offers VS Code's file actions; F2 renames and Delete moves to the Trash.
/// </summary>
public partial class ExplorerView : UserControl
{
    public ExplorerView()
    {
        InitializeComponent();
        Tree.AddHandler(PointerReleasedEvent, OnTreeClick, RoutingStrategies.Bubble, handledEventsToo: true);
        Tree.AddHandler(PointerPressedEvent, OnRightPress, RoutingStrategies.Tunnel);
        Tree.DoubleTapped += (_, _) => OpenSelected(preview: false);
        Tree.KeyDown += OnTreeKey;
        Tree.ContextRequested += OnContextRequested;
        DataContextChanged += (_, _) =>
        {
            if (Bench is { } bench) bench.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(Wicked.App.Workbench.Workbench.Revealed) && bench.Revealed is { } node) SelectWhenShown(node, 20);
            };
        };
    }

    private Wicked.App.Workbench.Workbench? Bench => DataContext as Wicked.App.Workbench.Workbench;

    /// <summary>
    /// Selects a node once its row exists: the rows of folders just opened are made on the next layout passes, and a
    /// TreeView drops a selection whose row is not there yet. Tries again a few times, then gives up quietly.
    /// </summary>
    private void SelectWhenShown(ExplorerNode node, int tries) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (Bench?.Revealed != node) return;
        // A layout pass now builds the rows of the folders just opened, so the selection has a row to land on.
        Tree.UpdateLayout();
        Tree.SelectedItem = node;
        if (Tree.SelectedItem == node)
        {
            Tree.ScrollIntoView(node);
            return;
        }

        if (tries > 0) SelectWhenShown(node, tries - 1);
    }, Avalonia.Threading.DispatcherPriority.Background);

    private ExplorerNode? Selected => Tree.SelectedItem as ExplorerNode;

    /// <summary>Where a new file goes: the selected folder, the selected file's folder, or the root.</summary>
    private string? TargetFolder => Selected is { } node
        ? node.IsDirectory ? node.FullPath : Path.GetDirectoryName(node.FullPath)
        : Bench?.Root;

    private void OnTreeKey(object? sender, KeyEventArgs e)
    {
        if (Bench is not { } bench) return;
        // Ctrl+C, Ctrl+X, Ctrl+V on the tree: the files themselves, as in VS Code's Explorer.
        if (e.KeyModifiers == KeyModifiers.Control && e.Key is Key.C or Key.X or Key.V)
        {
            if (e.Key == Key.V) { if (TargetFolder is { } into) _ = bench.PasteFilesAsync(into); }
            else if (Selected is { } chosen) bench.CopyFiles([chosen.FullPath], cut: e.Key == Key.X);
            e.Handled = true;
            return;
        }

        if (Selected is not { } node) return;
        switch (e.Key)
        {
            case Key.Enter:
                OpenSelected(preview: false);
                break;
            case Key.F2:
                _ = bench.RenameAsync(node.FullPath);
                break;
            case Key.Delete:
                _ = bench.TrashAsync(node.FullPath);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnTreeClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if (Selected is { IsDirectory: true } folder && e.Source is not null && IsOnItemContent(e.Source))
        {
            folder.IsExpanded = !folder.IsExpanded;
            return;
        }

        OpenSelected(preview: true);
    }

    /// <summary>A right click selects the row under it first, so the menu acts on what was clicked.</summary>
    private void OnRightPress(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(Tree).Properties.IsRightButtonPressed) return;
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<TreeViewItem>(includeSelf: true) is { DataContext: ExplorerNode node }) Tree.SelectedItem = node;
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Bench is not { } bench) return;
        var node = Selected;
        var folder = TargetFolder ?? bench.Root;
        var items = new List<Control>();
        MenuItem Item(string header, string? keys, Func<Task> run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, InputGesture = keys is null ? null : KeyGesture.Parse(keys), IsEnabled = enabled };
            item.Click += (_, _) => _ = run();
            return item;
        }

        var shell = TopLevel.GetTopLevel(this)?.DataContext as Wicked.App.ViewModels.ShellViewModel;
        Task Do(Action run)
        {
            run();
            return Task.CompletedTask;
        }

        // VS Code's order: making, opening and revealing; comparing; searching; the clipboard; paths; renaming and deleting.
        if (folder is not null && node is not { IsDirectory: false })
        {
            items.Add(Item("New File...", null, () => bench.NewFileAsync(folder)));
            items.Add(Item("New Folder...", null, () => bench.NewFolderAsync(folder)));
            items.Add(new Separator());
        }

        if (node is { IsDirectory: false }) items.Add(Item("Open to the Side", null, () => bench.OpenToSideAsync(node.FullPath)));
        if (node is not null)
        {
            items.Add(Item(OperatingSystem.IsWindows() ? "Reveal in File Explorer" : OperatingSystem.IsMacOS() ? "Reveal in Finder" : "Open Containing Folder", null, () =>
            {
                Wicked.App.Workbench.Workbench.Reveal(node.FullPath);
                return Task.CompletedTask;
            }));
        }

        if (shell is not null && (node?.FullPath ?? folder) is { } here)
        {
            items.Add(Item("Open in Integrated Terminal", null, () => Do(() => shell.OpenTerminalIn(here))));
            if (node is { IsDirectory: false } file)
            {
                items.Add(new Separator());
                items.Add(Item("Select for Compare", null, () => Do(() => shell.SelectForCompare(file.FullPath))));
                if (shell.CompareSource is { } source && source != file.FullPath)
                {
                    items.Add(Item($"Compare with {Path.GetFileName(source)}", null, () => Do(() => shell.CompareWithSelected(file.FullPath))));
                }
            }
            else
            {
                items.Add(new Separator());
                items.Add(Item("Find in Folder...", null, () => Do(() => shell.FindInFolder(here))));
            }
        }

        items.Add(new Separator());
        if (node is not null)
        {
            items.Add(Item("Cut", "Ctrl+X", () => Do(() => bench.CopyFiles([node.FullPath], cut: true))));
            items.Add(Item("Copy", "Ctrl+C", () => Do(() => bench.CopyFiles([node.FullPath], cut: false))));
        }

        if (folder is not null && node is not { IsDirectory: false }) items.Add(Item("Paste", "Ctrl+V", () => bench.PasteFilesAsync(folder), enabled: bench.FileClipboard is not null));
        if (node is not null)
        {
            items.Add(new Separator());
            items.Add(Item("Copy Path", null, () => CopyAsync(node.FullPath)));
            items.Add(Item("Copy Relative Path", null, () => CopyAsync(bench.Root is { } root ? Path.GetRelativePath(root, node.FullPath) : node.FullPath)));
            items.Add(new Separator());
            items.Add(Item("Rename...", "F2", () => bench.RenameAsync(node.FullPath)));
            items.Add(Item("Delete", "Delete", () => bench.TrashAsync(node.FullPath)));
        }

        // No separator first, last or twice in a row (what a node offers varies).
        items = [.. items.Where((c, i) => c is not Separator || (i > 0 && items[i - 1] is not Separator))];
        while (items.Count > 0 && items[0] is Separator) items.RemoveAt(0);
        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        if (items.Count == 0) return;
        var menu = new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer };
        menu.Open(Tree);
        e.Handled = true;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    private static bool IsOnItemContent(object source) => source is TextBlock or Shell.Icon or StackPanel;

    private void OpenSelected(bool preview)
    {
        // A click previews and leaves the keyboard in the tree (as in VS Code); Enter or a double click goes to the editor.
        if (Selected is { IsDirectory: false } file && Bench is { } bench) _ = bench.OpenAsync(file.FullPath, preview, focus: !preview);
    }

    private async void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (Bench is not { Dialogs: { } dialogs } bench) return;
        if (await dialogs.PickFolderAsync() is { } folder) await bench.OpenFolderAsync(folder);
    }

    private void OnNewProject(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.DataContext is Wicked.App.ViewModels.ShellViewModel shell) shell.StartNewProjectCommand.Execute(null);
    }

    private void OnNewFile(object? sender, RoutedEventArgs e)
    {
        if (Bench is { } bench && TargetFolder is { } folder) _ = bench.NewFileAsync(folder);
    }

    private void OnNewFolder(object? sender, RoutedEventArgs e)
    {
        if (Bench is { } bench && TargetFolder is { } folder) _ = bench.NewFolderAsync(folder);
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        if (Bench?.ExplorerRoot is { } root) _ = root.RefreshAsync();
    }

    private void OnCollapseAll(object? sender, RoutedEventArgs e)
    {
        if (Bench?.ExplorerRoot is not { } root) return;
        var stack = new Stack<ExplorerNode>(root.Children);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            node.IsExpanded = false;
            foreach (var child in node.Children) stack.Push(child);
        }
    }
}
