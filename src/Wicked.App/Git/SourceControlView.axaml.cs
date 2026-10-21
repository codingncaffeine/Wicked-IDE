using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Wicked.App.Git;

/// <summary>
/// The Source Control panel's view: clicks and keys on the lists, the menus, and keeping the selection and the section heights
/// as the lists change. The state and the work are the view model's.
/// </summary>
public partial class SourceControlView : UserControl
{
    private readonly List<string> _selectedKeys = [];
    private bool _restoring;
    private SourceControlViewModel? _vm;

    public SourceControlView()
    {
        InitializeComponent();
        ChangeList.AddHandler(PointerReleasedEvent, OnChangeListClick, RoutingStrategies.Bubble, handledEventsToo: true);
        ChangeList.KeyDown += OnChangeListKey;
        ChangeList.ContextRequested += OnChangeContext;
        ChangeList.SelectionChanged += (_, _) =>
        {
            if (_restoring) return;
            _selectedKeys.Clear();
            _selectedKeys.AddRange(ChangeList.SelectedItems?.OfType<ScmRow>().Select(r => r.Key) ?? []);
        };
        GraphList.SelectionChanged += (_, _) =>
        {
            if (_vm is not null && GraphList.SelectedItem is CommitRowVm row) _ = _vm.SelectCommitAsync(row);
        };
        GraphList.ContextRequested += OnGraphContext;
        GraphList.AddHandler(ScrollViewer.ScrollChangedEvent, OnGraphScrolled, RoutingStrategies.Bubble);
        HistoryList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && HistoryList.SelectedItem is CommitRowVm row && _vm is not null) _ = _vm.OpenHistoryEntryAsync(row);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        CommitFileList.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && CommitFileList.SelectedItem is CommitFileRow row && _vm is not null) _ = _vm.OpenCommitFileCommand.ExecuteAsync(row);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        DataContextChanged += (_, _) => Attach(DataContext as SourceControlViewModel);
    }

    private void Attach(SourceControlViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.FocusCommitRequested -= FocusCommit;
        }

        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnViewModelChanged;
        vm.FocusCommitRequested += FocusCommit;
        SetHeights();
    }

    private void FocusCommit() => Dispatcher.UIThread.Post(() => CommitBox.Focus(), DispatcherPriority.Background);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SourceControlViewModel.Rows):
                RestoreSelection();
                break;
            case nameof(SourceControlViewModel.GraphExpanded) or nameof(SourceControlViewModel.HistoryExpanded) or nameof(SourceControlViewModel.StashesExpanded):
                SetHeights();
                break;
        }
    }

    /// <summary>A replaced list drops the selection; the rows that are still there are selected again.</summary>
    private void RestoreSelection()
    {
        if (_selectedKeys.Count == 0) return;
        var keys = _selectedKeys.ToHashSet(StringComparer.Ordinal);
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is null) return;
            _restoring = true;
            try
            {
                ChangeList.SelectedItems?.Clear();
                foreach (var row in _vm.Rows.Where(r => keys.Contains(r.Key))) ChangeList.SelectedItems?.Add(row);
            }
            finally
            {
                _restoring = false;
            }
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The graph and the history take the room that is left when open; closed, they are just their headings.</summary>
    private void SetHeights()
    {
        if (_vm is null) return;
        var rows = Sections.RowDefinitions;
        rows[4].Height = _vm.GraphExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        rows[7].Height = _vm.HistoryExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        rows[0].Height = new GridLength(_vm.GraphExpanded || _vm.HistoryExpanded ? 1 : 2, GridUnitType.Star);
    }

    // ── Clicks and keys ──────────────────────────────────────────────────────────────────────────

    private static T? RowAt<T>(object? source) where T : class =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as T;

    private static bool InsideButton(object? source) => (source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null;

    private void OnChangeListClick(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is null || e.InitialPressMouseButton != MouseButton.Left || InsideButton(e.Source)) return;
        var modifiers = e.KeyModifiers;
        switch (RowAt<ScmRow>(e.Source))
        {
            case ScmGroupRow or ScmFolderRow:
                _vm.ToggleRow(RowAt<ScmRow>(e.Source)!);
                break;
            case ScmFileRow file when (modifiers & (KeyModifiers.Control | KeyModifiers.Shift)) == 0:
                _ = _vm.OpenChangesCommand.ExecuteAsync(file);
                break;
        }
    }

    private IReadOnlyList<ScmFileRow> SelectedFiles => [.. ChangeList.SelectedItems?.OfType<ScmFileRow>() ?? []];

    private void OnChangeListKey(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        var files = SelectedFiles;
        switch (e.Key)
        {
            case Key.Enter when files.Count > 0:
                _ = _vm.OpenChangesCommand.ExecuteAsync(files[0]);
                break;
            case Key.Delete when files.Count > 0:
                _ = _vm.DiscardRowsAsync(files);
                break;
            case Key.Add or Key.OemPlus when files.Count > 0:
                _ = _vm.StageRowsAsync(files);
                break;
            case Key.Subtract or Key.OemMinus when files.Count > 0:
                _ = _vm.UnstageRowsAsync(files);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private static MenuItem Item(string header, Func<Task> run, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => _ = run();
        return item;
    }

    private void OnChangeContext(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null) return;
        // A right click on a row not in the selection selects it first, so the menu acts on what was clicked.
        if (RowAt<ScmFileRow>(e.Source) is { } clicked && ChangeList.SelectedItems?.Contains(clicked) != true)
        {
            ChangeList.SelectedItems?.Clear();
            ChangeList.SelectedItems?.Add(clicked);
        }

        var files = SelectedFiles;
        if (files.Count == 0) return;
        var vm = _vm;
        var items = new List<Control>
        {
            Item("Open Changes", () => vm.OpenChangesCommand.ExecuteAsync(files[0]), files.Count == 1),
            Item("Open File", () => vm.OpenFileCommand.ExecuteAsync(files[0]), files.Count == 1),
            new Separator(),
            Item(files.Any(f => f.IsConflict) ? "Mark as Resolved (Stage)" : "Stage Changes", () => vm.StageRowsAsync(files), files.Any(f => f.CanStage)),
            Item("Unstage Changes", () => vm.UnstageRowsAsync(files), files.Any(f => f.IsStaged)),
            Item("Discard Changes", () => vm.DiscardRowsAsync(files), files.Any(f => f.CanDiscard)),
        };
        if (files.Any(f => f.IsConflict))
        {
            items.Add(new Separator());
            items.Add(Item("Accept Current Version (Ours)", () => vm.TakeSideAsync(files.Where(f => f.IsConflict).ToList(), ours: true)));
            items.Add(Item("Accept Incoming Version (Theirs)", () => vm.TakeSideAsync(files.Where(f => f.IsConflict).ToList(), ours: false)));
        }

        items.Add(new Separator());
        items.Add(Item("Add to .gitignore", () => vm.IgnoreAsync(files), files.Any(f => f.Group == ScmGroup.Untracked)));
        items.Add(Item("Copy Path", () => vm.CopyPathsAsync(files, relative: false)));
        items.Add(Item("Copy Relative Path", () => vm.CopyPathsAsync(files, relative: true)));
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer }.Open(ChangeList);
        e.Handled = true;
    }

    private void OnGraphContext(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is null || RowAt<CommitRowVm>(e.Source) is not { } row) return;
        GraphList.SelectedItem = row;
        var vm = _vm;
        var items = new List<Control>
        {
            Item("Checkout (Detached)", () => vm.CheckoutCommitCommand.ExecuteAsync(row)),
            Item("Create Branch From Here...", () => vm.BranchAtCommitCommand.ExecuteAsync(row)),
            Item("Create Tag...", () => vm.TagAtCommitCommand.ExecuteAsync(row)),
            new Separator(),
            Item("Cherry-pick", () => vm.CherryPickCommand.ExecuteAsync(row)),
            Item("Revert", () => vm.RevertCommitCommand.ExecuteAsync(row)),
            new Separator(),
            Item("Copy Commit Hash", () => vm.CopyHashCommand.ExecuteAsync(row)),
        };
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.Pointer }.Open(GraphList);
        e.Handled = true;
    }

    private void OnGraphScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm is null || e.Source is not ScrollViewer sv) return;
        if (sv.Extent.Height > 0 && sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 120) _ = _vm.LoadMoreGraphAsync();
    }

    // ── Menus ────────────────────────────────────────────────────────────────────────────────────

    private void OnBranchClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _ = _vm.CheckoutToAsync();
    }

    private void OnMoreClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var vm = _vm;
        var items = new List<Control>
        {
            Item("Pull", vm.PullAsync),
            Item("Push", vm.PushAsync),
            Item("Push (Force with Lease)", vm.ForcePushAsync),
            Item("Fetch", vm.FetchAsync),
            Item("Sync", vm.SyncAsync),
            new Separator(),
            Item("Checkout to...", vm.CheckoutToAsync),
            Item("Create Branch...", vm.CreateBranchAsync),
            Item("Rename Branch...", vm.RenameBranchAsync),
            Item("Delete Branch...", vm.DeleteBranchAsync),
            Item("Merge Branch...", vm.MergeBranchAsync),
            Item("Create Tag...", vm.CreateTagAsync),
            new Separator(),
            Item("Stage All Changes", vm.StageAllAsync),
            Item("Unstage All Changes", vm.UnstageAllAsync),
            new Separator(),
            Item("Stash", vm.StashPushAsync),
            Item("Pop Latest Stash", vm.PopLatestStashAsync),
            new Separator(),
            Item("Show Git Output", () =>
            {
                vm.ShowOutputCommand.Execute(null);
                return Task.CompletedTask;
            }),
        };
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.Bottom, PlacementTarget = MoreButton }.Open(MoreButton);
    }

    private void OnCommitMoreClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var vm = _vm;
        var items = new List<Control>
        {
            Item("Commit", vm.CommitAsync),
            Item("Commit and Push", vm.CommitAndPushAsync),
            Item(vm.Amend ? "Stop Amending" : "Amend Last Commit", () =>
            {
                vm.Amend = !vm.Amend;
                return Task.CompletedTask;
            }),
            new Separator(),
            Item("Undo Last Commit", vm.UndoLastCommitAsync),
        };
        new ContextMenu { ItemsSource = items, Placement = PlacementMode.Bottom, PlacementTarget = CommitMoreButton }.Open(CommitMoreButton);
    }
}
