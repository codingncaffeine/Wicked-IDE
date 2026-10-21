using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Wicked.App.Shell;
using Wicked.App.ViewModels;

namespace Wicked.App.Views;

/// <summary>The Keyboard Shortcuts editor: the search, the list, and the box a new key is pressed into.</summary>
public partial class ShortcutsView : UserControl
{
    private ShortcutRow? _recording;
    private string? _recorded;

    public ShortcutsView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => ShortcutsSearch.Focus(), DispatcherPriority.Loaded);
        // Before the list and its items see them: Enter opens the record box, Delete removes the keys.
        ShortcutRows.AddHandler(KeyDownEvent, OnRowsKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>What "Open Keyboard Shortcuts (JSON)" does; set by the window.</summary>
    public Func<Task>? OpenJson { get; set; }

    private ShortcutsViewModel? Model => DataContext as ShortcutsViewModel;

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string Gesture(KeyEventArgs e) => new KeyGesture(e.Key, e.KeyModifiers).ToString();

    private void OnOpenJson(object? sender, RoutedEventArgs e) => _ = OpenJson?.Invoke();

    /// <summary>Record Keys on: a key pressed in the search box is searched for, not typed.</summary>
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { RecordKeys: true } model || IsModifier(e.Key)) return;
        e.Handled = true;
        model.Record(Gesture(e));
    }

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ShortcutRows.SelectedItem is ShortcutRow row) StartRecording(row);
    }

    private void OnRowsKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShortcutRows.SelectedItem is not ShortcutRow row || Model is not { } model) return;
        switch (e.Key)
        {
            case Key.Enter:
                StartRecording(row);
                e.Handled = true;
                break;
            case Key.Delete when row.Keys is not null:
                _ = model.RemoveAsync(row);
                e.Handled = true;
                break;
        }
    }

    /// <summary>A row's right-click menu, as VS Code's.</summary>
    private void OnRowsContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Control { DataContext: ShortcutRow row } || Model is not { } model) return;
        ShortcutRows.SelectedItem = row;
        MenuItem Item(string header, Action act, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => act();
            return item;
        }

        var menu = new ContextMenu
        {
            ItemsSource = new List<MenuItem>
            {
                Item(row.Keys is null ? "Add Keybinding..." : "Change Keybinding...", () => StartRecording(row)),
                Item("Remove Keybinding", () => _ = model.RemoveAsync(row), row.Keys is not null),
                Item("Reset Keybinding", () => _ = model.ResetAsync(row), row.User || row.Keys is null),
                Item("Copy Command ID", () => _ = CopyAsync(row.Id)),
                Item("Show Same Keybindings", () => model.ShowSame(row.Keys!), row.Keys is not null),
            },
        };
        menu.Open(ShortcutRows);
        e.Handled = true;
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
    }

    // ── Change Keybinding ────────────────────────────────────────────────────────────────────────

    /// <summary>Opens the box for <paramref name="row"/>'s new keys.</summary>
    public void StartRecording(ShortcutRow row)
    {
        _recording = row;
        _recorded = null;
        RecordTitle.Text = $"Press the keys for {row.Title}, then Enter";
        RecordedText.Text = "";
        RecordConflict.IsVisible = false;
        RecordLayer.IsVisible = true;
        Dispatcher.UIThread.Post(() => RecordBox.Focus(), DispatcherPriority.Loaded);
    }

    private void StopRecording()
    {
        RecordLayer.IsVisible = false;
        _recording = null;
        ShortcutRows.Focus();
    }

    private void OnRecordKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (_recording is not { } row || Model is not { } model || IsModifier(e.Key)) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            StopRecording();
            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            if (_recorded is { } keys) _ = model.ChangeAsync(row, keys);
            StopRecording();
            return;
        }

        // A second key makes a chord; a third starts over.
        var gesture = Gesture(e);
        _recorded = _recorded is { } first && !first.Contains(' ', StringComparison.Ordinal) ? $"{first} {gesture}" : gesture;
        RecordedText.Text = KeyNames.Readable(_recorded);
        var others = model.CommandsWith(_recorded, row.Id);
        RecordConflict.IsVisible = others.Count > 0;
        RecordConflict.Text = others.Count == 1 ? $"1 existing command has these keys: {others[0]}" : $"{others.Count} existing commands have these keys: {string.Join(", ", others.Take(3))}";
    }
}
