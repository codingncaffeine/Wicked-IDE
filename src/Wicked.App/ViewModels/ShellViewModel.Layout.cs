using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.Shell;

namespace Wicked.App.ViewModels;

/// <summary>
/// The View menu's layout: full screen (F11), Zen Mode (Ctrl+K Z, Escape twice to leave), the status bar, the panel's
/// place, the whole window's zoom (Ctrl+= / Ctrl+- / Ctrl+Numpad0, VS Code's keys; the editor's own font zoom keeps its
/// commands), and pinned tabs (Ctrl+K Shift+Enter).
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The window toggles full screen (F11, and Zen Mode).</summary>
    public event Action<bool>? FullScreenRequested;

    /// <summary>Zen Mode: only the editor, full screen; the side bar, panel, assistant, rail and status bar come back after.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStatusBar), nameof(ShowRail))]
    private bool _zenMode;

    private (bool Sidebar, bool Panel, bool Assistant) _beforeZen;

    public bool ShowStatusBar => Settings.StatusBarVisible && !ZenMode;

    public bool ShowRail => !ZenMode;

    /// <summary>window.zoomLevel as a scale: 1.2 to the power of the level, as VS Code zooms.</summary>
    public double WindowZoom => Math.Pow(1.2, Math.Clamp(Settings.ZoomLevel, -8, 8));

    /// <summary>bottom, left or right.</summary>
    public string PanelLocation => Settings.PanelLocation is "left" or "right" ? Settings.PanelLocation : "bottom";

    private bool _fullScreen;

    /// <summary>The window fills the screen (F11).</summary>
    public bool FullScreen => _fullScreen;

    private IEnumerable<WorkbenchCommand> LayoutCommands() =>
    [
        new("workbench.action.toggleFullScreen", "View", "Toggle Full Screen", "F11", () =>
        {
            SetFullScreen(!_fullScreen);
            return Task.CompletedTask;
        }),
        new("workbench.action.toggleZenMode", "View", "Toggle Zen Mode", "Ctrl+K Z", () =>
        {
            ToggleZenMode();
            return Task.CompletedTask;
        }),
        // Toggle Menu Bar, as VS Code's: a shown menu bar is hidden until Alt is pressed; a hidden one comes back.
        new("workbench.action.toggleMenuBar", "View", "Toggle Menu Bar", null,
            () => UpdateSettingsAsync(s => s with { MenuBarVisibility = s.MenuBarVisibility is "classic" or "compact" ? "toggle" : "classic" })),
        new("workbench.action.toggleStatusbarVisibility", "View", "Toggle Status Bar Visibility", null,
            () => UpdateSettingsAsync(s => s with { StatusBarVisible = !s.StatusBarVisible })),
        new("workbench.action.positionPanelBottom", "View", "Move Panel to Bottom", null, () => UpdateSettingsAsync(s => s with { PanelLocation = "bottom" })),
        new("workbench.action.positionPanelLeft", "View", "Move Panel to Left", null, () => UpdateSettingsAsync(s => s with { PanelLocation = "left" })),
        new("workbench.action.positionPanelRight", "View", "Move Panel to Right", null, () => UpdateSettingsAsync(s => s with { PanelLocation = "right" })),
        new("workbench.action.zoomIn", "View", "Zoom In", "Ctrl+OemPlus", () => UpdateSettingsAsync(s => s with { ZoomLevel = Math.Min(8, s.ZoomLevel + 1) })),
        new("workbench.action.zoomOut", "View", "Zoom Out", "Ctrl+OemMinus", () => UpdateSettingsAsync(s => s with { ZoomLevel = Math.Max(-8, s.ZoomLevel - 1) })),
        new("workbench.action.zoomReset", "View", "Reset Zoom", "Ctrl+NumPad0", () => UpdateSettingsAsync(s => s with { ZoomLevel = 0 })),
        new("workbench.action.pinEditor", "View", "Pin Editor", "Ctrl+K Shift+Enter", () =>
        {
            if (Workbench.ActiveTab is { } tab) Workbench.ActiveGroup.SetPinned(tab, !tab.IsPinned);
            return Task.CompletedTask;
        }),
        new("workbench.action.unpinEditor", "View", "Unpin Editor", null, () =>
        {
            if (Workbench.ActiveTab is { IsPinned: true } tab) Workbench.ActiveGroup.SetPinned(tab, false);
            return Task.CompletedTask;
        }),
    ];

    private void SetFullScreen(bool on)
    {
        _fullScreen = on;
        FullScreenRequested?.Invoke(on);
    }

    /// <summary>Into Zen Mode (remembering what was shown) or back out of it.</summary>
    public void ToggleZenMode()
    {
        if (!ZenMode)
        {
            _beforeZen = (SidebarVisible, BottomPanel.IsVisible, AssistantVisible);
            SidebarVisible = false;
            AssistantVisible = false;
            BottomPanel.IsVisible = false;
            ZenMode = true;
            SetFullScreen(true);
            return;
        }

        ZenMode = false;
        SidebarVisible = _beforeZen.Sidebar;
        AssistantVisible = _beforeZen.Assistant;
        BottomPanel.IsVisible = _beforeZen.Panel;
        SetFullScreen(false);
    }

    /// <summary>The layout settings changed: the window follows (status bar, zoom, panel place).</summary>
    private void LayoutSettingsChanged()
    {
        OnPropertyChanged(nameof(ShowStatusBar));
        OnPropertyChanged(nameof(WindowZoom));
        OnPropertyChanged(nameof(PanelLocation));
    }
}

/// <summary>Ctrl+Tab: the group's editors, the one used last first; Tab moves while Ctrl is held, letting go of Ctrl opens.</summary>
public sealed partial class ShellViewModel
{
    private void SwitchEditor(int direction)
    {
        if (QuickInput.IsOpen && QuickInput.IsSwitcher)
        {
            QuickInput.Move(direction);
            return;
        }

        var group = Workbench.ActiveGroup;
        var recent = group.Recent.Where(group.Tabs.Contains).ToList();
        if (recent.Count < 2) return;
        QuickInput.OpenSwitcher([.. recent.Select(tab => new QuickItem(tab.Title, tab.Path is { } path && Workbench.Root is { } root ? Path.GetRelativePath(root, Path.GetDirectoryName(path)!) : "", "", () =>
        {
            group.Activate(tab);
            return Task.CompletedTask;
        }))], direction > 0 ? 1 : recent.Count - 1);
        PaletteRequested?.Invoke("");
    }
}

/// <summary>The editor grid's commands, with VS Code's ids and keys: split any way, focus and move editors by direction, maximise.</summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<WorkbenchCommand> GridCommands()
    {
        var bench = Workbench;
        Task Do(Action run)
        {
            run();
            return Task.CompletedTask;
        }

        return
        [
            new("workbench.action.splitEditorDown", "View", "Split Editor Down", null, () => Do(() => bench.Split(Wicked.App.Workbench.SplitDirection.Down))),
            new("workbench.action.splitEditorOrthogonal", "View", "Split Editor in Orthogonal Direction", "Ctrl+K Ctrl+OemPipe", () => Do(() => bench.Split(Wicked.App.Workbench.SplitDirection.Down))),
            new("workbench.action.splitEditorLeft", "View", "Split Editor Left", null, () => Do(() => bench.Split(Wicked.App.Workbench.SplitDirection.Left))),
            new("workbench.action.splitEditorUp", "View", "Split Editor Up", null, () => Do(() => bench.Split(Wicked.App.Workbench.SplitDirection.Up))),
            new("workbench.action.focusLeftGroup", "View", "Focus Left Editor Group", "Ctrl+K Ctrl+Left", () => Do(() => bench.FocusDirection(Wicked.App.Workbench.SplitDirection.Left))),
            new("workbench.action.focusRightGroup", "View", "Focus Right Editor Group", "Ctrl+K Ctrl+Right", () => Do(() => bench.FocusDirection(Wicked.App.Workbench.SplitDirection.Right))),
            new("workbench.action.focusAboveGroup", "View", "Focus Editor Group Above", "Ctrl+K Ctrl+Up", () => Do(() => bench.FocusDirection(Wicked.App.Workbench.SplitDirection.Up))),
            new("workbench.action.focusBelowGroup", "View", "Focus Editor Group Below", "Ctrl+K Ctrl+Down", () => Do(() => bench.FocusDirection(Wicked.App.Workbench.SplitDirection.Down))),
            new("workbench.action.moveEditorToNextGroup", "View", "Move Editor into Next Group", "Ctrl+Alt+Right", () => Do(() => bench.MoveActiveEditorAlong(1))),
            new("workbench.action.moveEditorToPreviousGroup", "View", "Move Editor into Previous Group", "Ctrl+Alt+Left", () => Do(() => bench.MoveActiveEditorAlong(-1))),
            new("workbench.action.moveEditorToLeftGroup", "View", "Move Editor into Left Group", null, () => Do(() => bench.MoveActiveEditor(Wicked.App.Workbench.SplitDirection.Left))),
            new("workbench.action.moveEditorToRightGroup", "View", "Move Editor into Right Group", null, () => Do(() => bench.MoveActiveEditor(Wicked.App.Workbench.SplitDirection.Right))),
            new("workbench.action.moveEditorToAboveGroup", "View", "Move Editor into Group Above", null, () => Do(() => bench.MoveActiveEditor(Wicked.App.Workbench.SplitDirection.Up))),
            new("workbench.action.moveEditorToBelowGroup", "View", "Move Editor into Group Below", null, () => Do(() => bench.MoveActiveEditor(Wicked.App.Workbench.SplitDirection.Down))),
            new("workbench.action.toggleMaximizeEditorGroup", "View", "Toggle Maximize Editor Group", "Ctrl+K Ctrl+M", () => Do(bench.ToggleMaximizeGroup)),
            new("workbench.action.closeEditorsInGroup", "View", "Close All Editors in Group", "Ctrl+K W", () => bench.CloseGroupTabsAsync(bench.ActiveGroup)),
            new("workbench.action.focusNextGroup", "View", "Focus Next Editor Group", null, () => Do(() => bench.FocusAlong(1))),
            new("workbench.action.focusPreviousGroup", "View", "Focus Previous Editor Group", null, () => Do(() => bench.FocusAlong(-1))),
            new("workbench.action.moveEditorLeftInGroup", "View", "Move Editor Left", "Ctrl+Shift+PageUp", () => Do(() => bench.MoveActiveTabInGroup(-1))),
            new("workbench.action.moveEditorRightInGroup", "View", "Move Editor Right", "Ctrl+Shift+PageDown", () => Do(() => bench.MoveActiveTabInGroup(1))),
            new("workbench.action.evenEditorWidths", "View", "Reset Editor Group Sizes", null, () => Do(bench.EvenGroupSizes)),
            new("workbench.action.joinTwoGroups", "View", "Join Editor Group with Next Group", null, () => Do(bench.JoinWithNextGroup)),
            new("workbench.action.joinAllGroups", "View", "Join All Editor Groups", null, () => Do(bench.JoinAllGroups)),
            new("workbench.action.closePanel", "View", "Hide Panel", null, () => Do(() => BottomPanel.IsVisible = false)),
            new("workbench.action.closeSidebar", "View", "Close Primary Side Bar", null, () => Do(() => SidebarVisible = false)),
            // Alt+1..9 opens the group's tab at that place, Alt+0 its last (VS Code's keys on Linux and Windows).
            .. Enumerable.Range(1, 9).Select(i => new WorkbenchCommand($"workbench.action.openEditorAtIndex{i}", "View", $"Open Editor at Index {i}", $"Alt+D{i}", () => Do(() => bench.ActivateTabAt(i - 1)))),
            new("workbench.action.lastEditorInGroup", "View", "Open Last Editor in Group", "Alt+D0", () => Do(() => bench.ActivateTabAt(-1))),
        ];
    }
}
