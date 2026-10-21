using Avalonia.Controls;
using Avalonia.Layout;

namespace Wicked.App.Shell;

/// <summary>One line of a menu: a registered command, an action of its own, a separator or a submenu.</summary>
public abstract record MenuEntry;

/// <summary>
/// A command by id: its title (or <paramref name="Label"/>), its keys as they are now, ticked when <paramref name="Checked"/>
/// says. <paramref name="Before"/> runs first (a tab's menu makes that tab the active one, which the command acts on).
/// </summary>
public sealed record MenuCommand(string CommandId, string? Label = null, Func<bool>? Checked = null, Action? Before = null) : MenuEntry;

/// <summary>Something that is not a command (a recent folder, a tab's own action).</summary>
public sealed record MenuAction(string Label, Func<Task> Run, string Keys = "", bool Enabled = true) : MenuEntry;

public sealed record MenuSeparator : MenuEntry
{
    public static readonly MenuSeparator Line = new();
}

/// <summary>A submenu; its entries are made when it opens, so they show the state of that moment.</summary>
public sealed record SubMenu(string Label, Func<IReadOnlyList<MenuEntry>> Entries) : MenuEntry;

/// <summary>A menu of the menu bar ("_File": the underscore marks its Alt key).</summary>
public sealed record TopMenu(string Label, Func<IReadOnlyList<MenuEntry>> Entries);

/// <summary>
/// Turns menu entries into Avalonia menu items for every menu Wicked shows (the menu bar, the editor's, the tabs', the
/// explorer's): a command shows its title and its keys as people read them, is greyed out where its keys would not apply
/// (Step Over with nothing being debugged), and runs as a key press would.
/// </summary>
public static class MenuBuilder
{
    public static List<Control> Build(ViewModels.ShellViewModel shell, IEnumerable<MenuEntry> entries)
    {
        var items = new List<Control>();
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case MenuSeparator:
                    if (items.Count > 0 && items[^1] is not Separator) items.Add(new Separator());
                    break;
                case MenuCommand c when shell.Commands.FirstOrDefault(x => x.Id == c.CommandId) is { } command:
                    var item = Item(c.Label ?? command.Title, shell.Keys.DisplayKeysFor(command.Id), command.Applies, () =>
                    {
                        c.Before?.Invoke();
                        return Run(command);
                    });
                    item.Name = "Menu_" + command.Id;
                    if (c.Checked is { } isChecked)
                    {
                        item.ToggleType = MenuItemToggleType.CheckBox;
                        item.IsChecked = isChecked();
                    }

                    items.Add(item);
                    break;
                case MenuAction a:
                    items.Add(Item(a.Label, a.Keys, a.Enabled, a.Run));
                    break;
                case SubMenu s:
                    items.Add(Sub(shell, s.Label, s.Entries));
                    break;
            }
        }

        while (items.Count > 0 && items[^1] is Separator) items.RemoveAt(items.Count - 1);
        return items;
    }

    /// <summary>A submenu whose items are made each time it opens.</summary>
    public static MenuItem Sub(ViewModels.ShellViewModel shell, object header, Func<IReadOnlyList<MenuEntry>> entries)
    {
        var item = new MenuItem { Header = header };
        // A placeholder so the item shows its arrow and can open; the real items replace it as it opens.
        item.ItemsSource = new List<Control> { new MenuItem { Header = "" } };
        item.SubmenuOpened += (_, e) =>
        {
            if (e.Source == item) item.ItemsSource = Build(shell, entries());
        };
        return item;
    }

    private static MenuItem Item(string label, string keys, bool enabled, Func<Task> run)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), HorizontalAlignment = HorizontalAlignment.Stretch };
        header.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        if (keys.Length > 0)
        {
            var shortcut = new TextBlock { Text = keys, Classes = { "menukeys" }, Margin = new Avalonia.Thickness(32, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(shortcut, 1);
            header.Children.Add(shortcut);
        }

        var item = new MenuItem { Header = header, IsEnabled = enabled, Tag = label };
        item.Click += (_, _) => _ = run();
        return item;
    }

    private static async Task Run(WorkbenchCommand command)
    {
        try
        {
            await command.Run();
        }
        catch (Exception ex)
        {
            Wicked.Core.Log.Error($"The command {command.Id} failed", ex);
        }
    }
}
