using System.Collections.ObjectModel;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wicked.App.Shell;

/// <summary>
/// One tab of the bottom panel (Problems, Output, Terminal, …): its title, a count beside it, and the view it shows, made
/// the first time the tab is opened so a panel nobody opens costs nothing.
/// </summary>
public sealed partial class PanelTab : ObservableObject
{
    private readonly Func<Control> _make;
    private Control? _view;

    public PanelTab(string id, string title, Func<Control> make)
    {
        Id = id;
        Title = title;
        _make = make;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>A number shown beside the title (problems, running terminals); hidden when zero.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BadgeText), nameof(HasBadge))]
    private int _badge;

    public string BadgeText => Badge > 999 ? "999+" : Badge.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool HasBadge => Badge > 0;

    [ObservableProperty]
    private bool _isActive;

    public Control View => _view ??= _make();

    /// <summary>Whether the view has been made yet (tests, and the panel's lazy rule).</summary>
    public bool HasView => _view is not null;
}

/// <summary>
/// The panel under the editors (VS Code's Panel): a row of tabs, the active one's view, shown and hidden with Ctrl+J,
/// resized by its splitter and kept that size, and made full height with the maximise button.
/// </summary>
public sealed partial class BottomPanel : ObservableObject
{
    public const double DefaultHeight = 260;
    public const double MinHeight = 90;

    public ObservableCollection<PanelTab> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveView))]
    private PanelTab? _active;

    [ObservableProperty]
    private bool _isVisible;

    [ObservableProperty]
    private bool _isMaximized;

    /// <summary>The panel's height when shown and not maximised (the splitter writes it back).</summary>
    [ObservableProperty]
    private double _height = DefaultHeight;

    public Control? ActiveView => Active?.View;

    /// <summary>Adds a tab (once per id); the first one becomes the active one.</summary>
    public PanelTab Add(string id, string title, Func<Control> make)
    {
        if (Tabs.FirstOrDefault(t => t.Id == id) is { } existing) return existing;
        var tab = new PanelTab(id, title, make);
        Tabs.Add(tab);
        if (Active is null) Select(tab);
        return tab;
    }

    public PanelTab? this[string id] => Tabs.FirstOrDefault(t => t.Id == id);

    /// <summary>Opens the panel on a tab.</summary>
    public void Show(string id)
    {
        if (this[id] is not { } tab) return;
        Select(tab);
        IsVisible = true;
    }

    /// <summary>A tab's button: that tab; the active tab's button when shown hides nothing (VS Code keeps it open).</summary>
    [RelayCommand]
    private void Select(PanelTab tab)
    {
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, tab);
        Active = tab;
        IsVisible = true;
    }

    /// <summary>Ctrl+J, or a toggle command for one tab: a tab already showing hides the panel, any other shows it.</summary>
    public void Toggle(string? id = null)
    {
        if (id is null || (IsVisible && Active?.Id == id))
        {
            IsVisible = !IsVisible;
            if (IsVisible && Active is null && Tabs.Count > 0) Select(Tabs[0]);
            return;
        }

        Show(id);
    }

    [RelayCommand]
    private void Hide()
    {
        IsVisible = false;
        IsMaximized = false;
    }

    [RelayCommand]
    private void ToggleMaximized() => IsMaximized = !IsMaximized;

    /// <summary>The splitter let go: keep the size, within reason.</summary>
    public void Resized(double height) => Height = Math.Max(MinHeight, height);
}
