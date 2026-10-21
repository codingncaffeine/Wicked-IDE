using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Wicked.App.Shell;

namespace Wicked.App.Views;

/// <summary>
/// The menu bar in the title bar, as VS Code draws it: File, Edit, Selection, View, Go, Run, Terminal, Help left of the
/// command centre, one ☰ menu holding them when the bar is too narrow or window.menuBarVisibility is compact, shown only
/// while open when it is toggle (Alt opens it), never when hidden. Alt alone opens it and Alt+F (and the other underlined
/// letters) open a menu: Avalonia's main-menu handling. Items are made as a menu opens, so ticks and keys are current.
/// </summary>
public partial class MainWindow
{
    private bool? _menuFull;
    private double _menuFullWidth;

    private void InitMenuBar()
    {
        TitleBar.SizeChanged += (_, _) =>
        {
            ApplyMenuBar();
            ApplyRunControls();
        };
        MenuBar.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuBase.IsOpenProperty) return;
            // Hidden until Alt (toggle): it shows while one of its menus is open. Hidden for good: Alt opens nothing.
            if (_shell?.Settings.MenuBarVisibility == "toggle") ApplyMenuBar();
            else if (_shell?.Settings.MenuBarVisibility == "hidden" && MenuBar.IsOpen) MenuBar.Close();
        };
    }

    private void ApplyMenuBar()
    {
        if (_shell is null) return;
        var mode = _shell.Settings.MenuBarVisibility;
        if (_menuFull is null)
        {
            ShowMenus(full: true);
            MenuBar.Measure(Size.Infinity);
            _menuFullWidth = MenuBar.DesiredSize.Width;
        }

        // The room left of the command centre, less the logo, the gaps and a little air.
        var room = TitleBar.ColumnDefinitions[0].ActualWidth - 14 - 22 - 9 - 24;
        var full = mode != "compact" && (room <= 0 || _menuFullWidth <= room); // not laid out yet: as if it fits
        if (full != _menuFull) ShowMenus(full);
        MenuBar.IsVisible = mode switch
        {
            "hidden" => false,
            "toggle" => MenuBar.IsOpen,
            _ => true,
        };
        Wordmark.IsVisible = !MenuBar.IsVisible;
    }

    /// <summary>The run controls give way when the bar is narrow: the configuration's name first, then the whole control.</summary>
    private void ApplyRunControls()
    {
        var width = TitleBar.ColumnDefinitions[2].ActualWidth;
        if (width <= 0) return; // not laid out yet
        var room = width - TitleEnd.Margin.Left - TitleButtons.Bounds.Width - TitleButtons.Margin.Right;
        RunTarget.IsVisible = room >= 190;
        RunControls.IsVisible = room >= 110;
    }

    private void ShowMenus(bool full)
    {
        if (_shell is not { } shell) return;
        _menuFull = full;
        var menus = shell.MenuBar().Select(m => MenuBuilder.Sub(shell, m.Label, m.Entries)).ToList();
        if (full)
        {
            foreach (var menu in menus) menu.Name = "Menu_" + ((string)menu.Header!).TrimStart('_');
            MenuBar.ItemsSource = menus;
            return;
        }

        // Narrow or compact: one ☰ menu, the menus inside it.
        var hamburger = new MenuItem
        {
            Name = "Menu_Application",
            Header = new Icon { Data = (Geometry)this.FindResource("Icon.Menu")!, Size = 16 },
            ItemsSource = menus,
        };
        AutomationProperties.SetName(hamburger, "Application Menu");
        MenuBar.ItemsSource = new List<MenuItem> { hamburger };
    }
}
