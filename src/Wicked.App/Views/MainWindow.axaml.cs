using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
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
        StartupTimeline.Mark("platform window");
        AccessibleNames.Register();
        InitializeComponent();
        StartupTimeline.Mark("window XAML");
        WindowFrame.Apply(this);
        WindowFrame.Drags(this, TitleBar);
        InitMenuBar();
        Opened += OnFirstOpened;
        Deactivated += (_, _) => _shell?.Workbench.WindowDeactivated();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Bubble);
        // Files and folders dropped on the window open (the assistant's box takes its own drops, for pictures).
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            if (e.Handled) return;
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.Handled || _shell is null) return;
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            if (paths.Count == 0) return;
            e.Handled = true;
            _ = _shell.OpenDroppedAsync(paths);
        });
        // The mouse's side buttons go Back and Forward, as in VS Code and a browser.
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
            if (_shell is null || kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed)) return;
            _ = kind == PointerUpdateKind.XButton1Pressed ? _shell.Workbench.GoBackAsync() : _shell.Workbench.GoForwardAsync();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Escape answers an open dialog with Cancel, and closes the Settings sheet, before whatever has the keyboard sees it.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && DialogLayer.IsVisible)
            {
                Answer(SaveChoice.Cancel);
                e.Handled = true;
                return;
            }

            // The second key of a chord is the chord's, as in VS Code: Ctrl+K Ctrl+Left focuses the left group, never moves the
            // editor's caret a word or reaches the terminal's shell. A second key no chord has is swallowed.
            if (_shell is not null && _shell.Keys.ChordPending && !DialogLayer.IsVisible && e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
            {
                if (_shell.Keys.Match(e) is { } chord) _ = RunAsync(chord);
                e.Handled = true;
                return;
            }

            // The window's Shift+Alt keys (Format Document is Shift+Alt+F) before the menus' underlined letters: Avalonia would
            // take Shift+Alt+F for Alt+F and open the File menu. Keys the editor or the terminal keep are not the window's.
            if (_shell is not null && e.KeyModifiers == (KeyModifiers.Shift | KeyModifiers.Alt) && !DialogLayer.IsVisible
                && FocusManager?.GetFocusedElement() is not Terminal.TerminalView && _shell.Keys.Match(e) is { } shiftAlt)
            {
                _ = RunAsync(shiftAlt);
                e.Handled = true;
                return;
            }

            // Ctrl+Tab before keyboard navigation takes Tab: it opens the switcher, and while Ctrl is held Tab and Shift+Tab move
            // through it (letting go chooses, below).
            if (_shell is not null && e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                if (_shell.QuickInput is { IsOpen: true, IsSwitcher: true }) _shell.QuickInput.Move(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                else if (_shell.Keys.Match(e) is { } command) _ = RunAsync(command);
                else return;
                e.Handled = true;
                return;
            }

            // Escape twice in Zen Mode leaves it, as in VS Code (the first one still does its usual work).
            if (_shell is { ZenMode: true } && e.Key == Key.Escape)
            {
                var now = DateTime.UtcNow;
                if (now - _lastEscape < TimeSpan.FromMilliseconds(600)) _shell.ToggleZenMode();
                _lastEscape = now;
            }

            if (_shell is null || e.Key != Key.Escape) return;
            if (_shell.Models.Prompt is not null) return; // the question has its own Escape
            if (_shell.Models.RunSettings is not null) _shell.Models.RunSettings = null;
            else if (_shell.Chat.PromptEditor is not null) _shell.Chat.PromptEditor = null;
            else if (_shell.WhatsNew.IsOpen) _shell.WhatsNew.IsOpen = false;
            else if (_shell.NewProject.IsOpen) _shell.NewProject.CloseCommand.Execute(null);
            else if (_shell.Notifications.IsOpen) _shell.Notifications.IsOpen = false;
            else if (_shell.SettingsOpen) _shell.SettingsOpen = false;
            else return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Letting go of Ctrl chooses the switcher's selected editor.
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (_shell is not { QuickInput: { IsOpen: true, IsSwitcher: true } } || e.Key is not (Key.LeftCtrl or Key.RightCtrl)) return;
            _ = _shell.QuickInput.ChooseAsync();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
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
            _shell.BottomPanel.PropertyChanged += (_, pe) =>
            {
                if (pe.PropertyName is nameof(BottomPanel.IsVisible) or nameof(BottomPanel.IsMaximized)) ApplyPanelRows();
            };
            ApplyPanelRows();
            _shell.PaletteRequested += OnPaletteRequested;
            _shell.FocusPartRequested += FocusPart;
            _shell.QuitRequested += Close;
            _shell.FullScreenRequested += on =>
            {
                if (on && WindowState != WindowState.FullScreen) _beforeFullScreen = WindowState;
                WindowState = on ? WindowState.FullScreen : _beforeFullScreen;
            };
            _shell.ActivateRequested += () =>
            {
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
            };
            _shell.SearchFocusRequested += () => Avalonia.Threading.Dispatcher.UIThread.Post(SearchPanel.FocusQuery, Avalonia.Threading.DispatcherPriority.Loaded);
            _shell.Workbench.Dialogs = this;
            _shell.Clipboard = text => Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;
            ApplyColumns();
            ApplyMenuBar();
            ApplyChrome(_shell.Settings.TitleBarStyle == "native");
            Root.RowDefinitions[2].Height = new GridLength(_shell.ShowStatusBar ? 28 : 0);
            ApplyZoom(_shell.WindowZoom);
        };
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The sheet takes the keyboard when it opens (so typing does not go to the editor behind it) and gives it back.
        if (e.PropertyName == nameof(ShellViewModel.SettingsOpen) && _shell is not null)
        {
            var open = _shell.SettingsOpen;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (open) this.GetVisualDescendants().OfType<NumericUpDown>().FirstOrDefault(n => n.Name == "FontSizeBox")?.Focus();
                else _shell.Workbench.ActiveTab?.Editor?.Focus();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        if (e.PropertyName is nameof(ShellViewModel.SidebarVisible) or nameof(ShellViewModel.AssistantVisible) or nameof(ShellViewModel.ShowRail)) ApplyColumns();
        if (e.PropertyName == nameof(ShellViewModel.Settings) && _shell is not null) ApplyChrome(_shell.Settings.TitleBarStyle == "native");
        if (e.PropertyName == nameof(ShellViewModel.Settings)) ApplyMenuBar();
        if (e.PropertyName == nameof(ShellViewModel.ShowStatusBar) && _shell is not null) Root.RowDefinitions[2].Height = new GridLength(_shell.ShowStatusBar ? 28 : 0);
        if (e.PropertyName == nameof(ShellViewModel.WindowZoom) && _shell is not null) ApplyZoom(_shell.WindowZoom);
        if (e.PropertyName == nameof(ShellViewModel.PanelLocation)) ApplyPanelRows();

        // The assistant opens with the cursor in its composer (Ctrl+L, as in Cursor).
        if (e.PropertyName == nameof(ShellViewModel.AssistantVisible) && _shell is { AssistantVisible: true }) AssistantChat.FocusComposer();
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
        columns[0].Width = new GridLength(_shell.ShowRail ? 52 : 0); // the rail, hidden in Zen Mode
    }

    private DateTime _lastEscape;
    private WindowState _beforeFullScreen = WindowState.Normal;

    /// <summary>window.zoomLevel: everything inside the frame scaled; 1 is no transform at all, so nothing is resampled.</summary>
    private void ApplyZoom(double zoom) =>
        Zoom.LayoutTransform = Math.Abs(zoom - 1) < 0.001 ? null : new Avalonia.Media.ScaleTransform(zoom, zoom);

    /// <summary>
    /// window.titleBarStyle: the desktop's own frame (its title bar and buttons; Wicked's bar stays as a toolbar with the
    /// command centre, without the caption buttons), or Wicked's drawn one.
    /// </summary>
    private void ApplyChrome(bool native)
    {
        if (native == _nativeChrome) return;
        _nativeChrome = native;
        ExtendClientAreaToDecorationsHint = !native;
        WindowDecorations = native ? WindowDecorations.Full : WindowDecorations.None;
        foreach (var button in TitleBar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("caption"))) button.IsVisible = !native;
    }

    private bool _nativeChrome;

    /// <summary>
    /// The panel beside the editors (workbench.panel.defaultLocation): below them, or to their left or right. Hidden it takes
    /// no room, shown it has the size it was left at, maximised all of it.
    /// </summary>
    private void ApplyPanelRows()
    {
        if (_shell is null) return;
        var panel = _shell.BottomPanel;
        var side = _shell.PanelLocation is "left" or "right";
        var rows = EditorColumn.RowDefinitions;
        var columns = EditorColumn.ColumnDefinitions;
        if (!side && rows[2].Height.IsAbsolute && rows[2].Height.Value > 0 && !panel.IsMaximized) panel.Resized(rows[2].Height.Value);
        if (side && columns.Count == 3 && columns[PanelSlot()].Width is { IsAbsolute: true, Value: > 0 } width && !panel.IsMaximized) _panelWidth = width.Value;

        // The three parts in a row (bottom) or in columns (left: panel, gap, editor; right: editor, gap, panel).
        while (columns.Count < 3) columns.Add(new ColumnDefinition(0, GridUnitType.Pixel));
        int EditorAt() => side && _shell.PanelLocation == "left" ? 2 : 0;
        int PanelSlot() => _shell.PanelLocation == "left" ? 0 : 2;
        Grid.SetRow(Editor, 0);
        Grid.SetRow(DebugToolbar, 0);
        Grid.SetRow(PanelSplitter, side ? 0 : 1);
        Grid.SetRow(PanelArea, side ? 0 : 2);
        Grid.SetColumn(Editor, side ? EditorAt() : 0);
        Grid.SetColumn(DebugToolbar, side ? EditorAt() : 0);
        Grid.SetColumn(PanelSplitter, side ? 1 : 0);
        Grid.SetColumn(PanelArea, side ? PanelSlot() : 0);
        PanelSplitter.ResizeDirection = side ? Avalonia.Controls.GridResizeDirection.Columns : Avalonia.Controls.GridResizeDirection.Rows;

        var shown = panel.IsVisible;
        var editorSize = shown && panel.IsMaximized ? new GridLength(0) : GridLength.Star;
        var gap = new GridLength(shown && !panel.IsMaximized ? 6 : 0);
        var panelSize = !shown ? new GridLength(0) : panel.IsMaximized ? GridLength.Star : new GridLength(side ? _panelWidth : panel.Height);
        if (side)
        {
            rows[0].Height = GridLength.Star;
            rows[1].Height = new GridLength(0);
            rows[2].Height = new GridLength(0);
            columns[EditorAt()].Width = editorSize;
            columns[1].Width = gap;
            columns[PanelSlot()].Width = panelSize;
            columns[PanelSlot()].MinWidth = shown ? 200 : 0;
            rows[2].MinHeight = 0;
        }
        else
        {
            columns[0].Width = GridLength.Star;
            columns[1].Width = new GridLength(0);
            columns[2].Width = new GridLength(0);
            columns[2].MinWidth = 0;
            rows[0].Height = editorSize;
            rows[1].Height = gap;
            rows[2].Height = panelSize;
            rows[2].MinHeight = shown ? BottomPanel.MinHeight : 0;
        }
    }

    private double _panelWidth = 420;

    /// <summary>F6: the keyboard to the next part that is showing — the side bar, the editor, the panel, the assistant.</summary>
    private void FocusPart(int direction)
    {
        Control[] parts = [SidePanel, Editor, PanelArea, Assistant];
        var visible = parts.Where(p => p.IsEffectivelyVisible && p.Bounds.Width > 0 && p.Bounds.Height > 0).ToList();
        if (visible.Count == 0) return;
        var focused = FocusManager?.GetFocusedElement() as Visual;
        var at = visible.FindIndex(p => focused is not null && (ReferenceEquals(p, focused) || p.IsVisualAncestorOf(focused)));
        var next = visible[((at < 0 ? (direction > 0 ? -1 : 0) : at) + direction + visible.Count) % visible.Count];
        if (ReferenceEquals(next, Editor) && _shell?.Workbench.ActiveTab?.Editor is { } editor)
        {
            editor.Focus();
            return;
        }

        var target = next.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
        target?.Focus(NavigationMethod.Tab);
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

    /// <summary>A status bar item that runs a command (its Tag): Ln/Col goes to a line, the encoding item picks one...</summary>
    private void OnStatusCommand(object? sender, RoutedEventArgs e)
    {
        if (_shell is not null && sender is Control { Tag: string id }) _ = _shell.RunCommandAsync(id);
    }

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

    public async Task<bool> ConfirmAsync(string message, string detail, string confirm)
    {
        var saveLabel = DialogSave.Content;
        DialogSave.Content = confirm;
        DialogDontSave.IsVisible = false;
        try
        {
            return await AskSaveAsync(message, detail) == SaveChoice.Save;
        }
        finally
        {
            DialogSave.Content = saveLabel;
            DialogDontSave.IsVisible = true;
        }
    }

    public Task<string?> AskNameAsync(string prompt, string initial)
    {
        var answer = new TaskCompletionSource<string?>();
        if (_shell is null) return Task.FromResult<string?>(null);
        _shell.QuickInput.OpenPrompt(prompt, initial, text => answer.TrySetResult(text));
        Avalonia.Threading.Dispatcher.UIThread.Post(Palette.FocusQuery);
        return answer.Task;
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

    /// <summary>
    /// Closing: unsaved files are kept for next time (files.hotExit, VS Code's default) or, with it off, asked about once;
    /// the session is remembered, then the window really closes.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closingConfirmed || _shell is null) return;
        e.Cancel = true;
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        if (_shell is null) return;
        if (_shell.Settings.HotExit != "off") await _shell.HotExit.FlushAsync();
        else if (!await _shell.Workbench.ConfirmCloseAllAsync()) return;
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
        StartupTimeline.Mark("shown");
        RequestAnimationFrame(_ => StartupTimeline.FirstFrame());
    }
}
