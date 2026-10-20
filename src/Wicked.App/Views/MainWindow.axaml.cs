using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wicked.App.Shell;
using Wicked.App.ViewModels;
using Wicked.App.Workbench;
using Wicked.Core;

namespace Wicked.App.Views;

public partial class MainWindow : Window, IWorkbenchDialogs
{
    private const int SideColumn = 1;
    private const int AssistantColumn = 5;

    // The widths the user dragged the panels to, restored when a hidden panel comes back.
    private GridLength _sideWidth = new(260);
    private GridLength _assistantWidth = new(380);
    private ShellViewModel? _shell;
    private TaskCompletionSource<SaveChoice>? _dialog;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        WindowFrame.Apply(this);
        WindowFrame.Drags(this, TitleBar);
        Opened += OnFirstOpened;
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        DataContextChanged += (_, _) =>
        {
            if (_shell is not null)
            {
                _shell.PropertyChanged -= OnShellChanged;
                _shell.PaletteRequested -= OnPaletteRequested;
            }

            _shell = DataContext as ShellViewModel;
            if (_shell is null) return;
            _shell.PropertyChanged += OnShellChanged;
            _shell.PaletteRequested += OnPaletteRequested;
            _shell.Workbench.Dialogs = this;
            ApplyColumns();
        };
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.SidebarVisible) or nameof(ShellViewModel.AssistantVisible)) ApplyColumns();
    }

    /// <summary>A hidden panel gives its column back to the editor; shown again, it returns at the width it had.</summary>
    private void ApplyColumns()
    {
        if (_shell is null) return;
        var columns = Body.ColumnDefinitions;
        if (columns[SideColumn].Width.Value > 0) _sideWidth = columns[SideColumn].Width;
        if (columns[AssistantColumn].Width.Value > 0) _assistantWidth = columns[AssistantColumn].Width;
        columns[SideColumn].Width = _shell.SidebarVisible ? _sideWidth : new GridLength(0);
        columns[SideColumn + 1].Width = new GridLength(_shell.SidebarVisible ? 6 : 0);
        columns[AssistantColumn].Width = _shell.AssistantVisible ? _assistantWidth : new GridLength(0);
        columns[AssistantColumn - 1].Width = new GridLength(_shell.AssistantVisible ? 6 : 0);
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Keys the focused control left alone go to the workbench's commands (the editor takes its own first).</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _shell is null || DialogLayer.IsVisible) return;
        if (_shell.Keys.Match(e) is not { } command) return;
        e.Handled = true;
        _ = RunAsync(command);
    }

    private static async Task RunAsync(WorkbenchCommand command)
    {
        try
        {
            await command.Run();
        }
        catch (Exception ex)
        {
            Log.Error($"The command {command.Id} failed", ex);
        }
    }

    private void OnPaletteRequested(string prefix) => Avalonia.Threading.Dispatcher.UIThread.Post(Palette.FocusQuery);

    private void OnCommandCentre(object? sender, RoutedEventArgs e) => _shell?.OpenPalette("");

    // ── Dialogs ──────────────────────────────────────────────────────────────────────────────────

    public Task<SaveChoice> AskSaveAsync(string message, string detail)
    {
        _dialog?.TrySetResult(SaveChoice.Cancel);
        _dialog = new TaskCompletionSource<SaveChoice>();
        DialogMessage.Text = message;
        DialogDetail.Text = detail;
        DialogLayer.IsVisible = true;
        DialogSave.Focus();
        return _dialog.Task;
    }

    private void Answer(SaveChoice choice)
    {
        DialogLayer.IsVisible = false;
        _dialog?.TrySetResult(choice);
        _dialog = null;
    }

    private void OnDialogSave(object? sender, RoutedEventArgs e) => Answer(SaveChoice.Save);

    private void OnDialogDontSave(object? sender, RoutedEventArgs e) => Answer(SaveChoice.DontSave);

    private void OnDialogCancel(object? sender, RoutedEventArgs e) => Answer(SaveChoice.Cancel);

    public async Task<string?> PickFolderAsync()
    {
        if (StorageProvider is not { CanPickFolder: true } storage) return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Open Folder", AllowMultiple = false });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    public async Task<IReadOnlyList<string>> PickFilesAsync()
    {
        if (StorageProvider is not { CanOpen: true } storage) return [];
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open File", AllowMultiple = true });
        return [.. picked.Select(f => f.TryGetLocalPath()).OfType<string>()];
    }

    public async Task<string?> PickSaveFileAsync(string suggestedName)
    {
        if (StorageProvider is not { CanSave: true } storage) return null;
        var picked = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save As", SuggestedFileName = suggestedName });
        return picked?.TryGetLocalPath();
    }

    // ── The window ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Closing: unsaved tabs are asked about once, the session remembered, then the window really closes.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || _shell is null) return;
        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_shell is null || !await _shell.Workbench.ConfirmCloseAllAsync()) return;
        await _shell.SaveSessionAsync();
        _closingConfirmed = true;
        Close();
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A maximised window has no edge to resize from and no border to show.
        if (change.Property == WindowStateProperty && WindowDecorations == WindowDecorations.None)
        {
            Frame.BorderThickness = new Thickness(WindowState == WindowState.Normal ? 1 : 0);
        }
    }

    private void OnFirstOpened(object? sender, EventArgs e)
    {
        // Opened fires on every Show; this runs once.
        Opened -= OnFirstOpened;
        WindowingBackend.WindowOpened(this);
    }
}
