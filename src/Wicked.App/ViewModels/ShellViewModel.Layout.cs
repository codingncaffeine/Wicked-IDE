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
