using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Wicked.App.Workbench;

/// <summary>One editor group: a tab strip and the active tab's editor. The workbench lays groups side by side.</summary>
public sealed partial class EditorGroup(Workbench workbench) : ObservableObject
{
    public Workbench Workbench { get; } = workbench;

    public ObservableCollection<EditorTab> Tabs { get; } = [];

    [ObservableProperty]
    private EditorTab? _active;

    [ObservableProperty]
    private bool _isFocused;

    private readonly List<EditorTab> _recent = [];

    /// <summary>The group's tabs, the one used last first (Ctrl+Tab's order).</summary>
    public IReadOnlyList<EditorTab> Recent => _recent;

    partial void OnActiveChanged(EditorTab? oldValue, EditorTab? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null)
        {
            newValue.IsActive = true;
            _recent.Remove(newValue);
            _recent.Insert(0, newValue);
        }

        Workbench.ActiveTabChanged();
    }

    /// <summary>The view shows the active tab again (its editor was replaced).</summary>
    public void Reshow() => OnPropertyChanged(nameof(Active));

    public void Activate(EditorTab tab)
    {
        Active = tab;
        Workbench.Focus(this);
    }

    private int PinnedCount => Tabs.Count(t => t.IsPinned);

    /// <summary>Adds a tab after the active one (never among the pinned ones); a preview tab takes the place of the previous preview.</summary>
    public void Add(EditorTab tab)
    {
        if (tab.IsPreview && Tabs.FirstOrDefault(t => t.IsPreview) is { } preview)
        {
            var at = Tabs.IndexOf(preview);
            Tabs[at] = tab;
            _recent.Remove(preview);
            preview.Close();
            Workbench.TabRemoved(preview);
        }
        else
        {
            var at = Active is null ? Tabs.Count : Tabs.IndexOf(Active) + 1;
            Tabs.Insert(Math.Max(at, PinnedCount), tab);
        }

        Workbench.TabAdded(tab);

        Activate(tab);
    }

    /// <summary>Removes a tab (the caller has dealt with unsaved changes); the neighbour to its right, else left, becomes active.</summary>
    public void Remove(EditorTab tab)
    {
        var at = Tabs.IndexOf(tab);
        if (at < 0) return;
        Tabs.RemoveAt(at);
        _recent.Remove(tab);
        tab.Close();
        Workbench.TabRemoved(tab);
        if (Active == tab) Active = Tabs.Count == 0 ? null : Tabs[Math.Min(at, Tabs.Count - 1)];
    }

    /// <summary>
    /// Takes a tab out to move it to another group: its editor stays open and nothing hears of a close (the history, unsaved
    /// backups and the language servers follow the tab, which goes on living).
    /// </summary>
    internal void Detach(EditorTab tab)
    {
        var at = Tabs.IndexOf(tab);
        if (at < 0) return;
        Tabs.RemoveAt(at);
        _recent.Remove(tab);
        if (Active == tab) Active = Tabs.Count == 0 ? null : Tabs[Math.Min(at, Tabs.Count - 1)];
    }

    /// <summary>A tab moved in from another group: after the active one (never among the pinned), and active.</summary>
    internal void Attach(EditorTab tab)
    {
        tab.IsPreview = false;
        var at = Active is null ? Tabs.Count : Tabs.IndexOf(Active) + 1;
        Tabs.Insert(tab.IsPinned ? Math.Min(at, PinnedCount) : Math.Max(at, PinnedCount), tab);
        Activate(tab);
    }

    /// <summary>Pins or unpins a tab: a pinned one moves to the end of the pinned tabs at the front, an unpinned one just after them.</summary>
    public void SetPinned(EditorTab tab, bool pinned)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0 || tab.IsPinned == pinned) return;
        tab.IsPinned = pinned;
        if (pinned) tab.IsPreview = false;
        // Counted with the tab's new state: pinned, it is the last of the pinned; unpinned, the first after them.
        var to = pinned ? PinnedCount - 1 : PinnedCount;
        if (to != from) Tabs.Move(from, to);
        Active = tab;
    }

    public void Cycle(int direction)
    {
        if (Tabs.Count < 2 || Active is null) return;
        var at = (Tabs.IndexOf(Active) + direction + Tabs.Count) % Tabs.Count;
        Activate(Tabs[at]);
    }
}

public sealed partial class EditorGroup
{
    /// <summary>A tab dragged along its own strip: to <paramref name="index"/>, kept on its side of the pinned ones.</summary>
    public void MoveWithin(EditorTab tab, int index)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0) return;
        var pinned = PinnedCount;
        var to = Math.Clamp(index > from ? index - 1 : index, 0, Tabs.Count - 1);
        to = tab.IsPinned ? Math.Min(to, pinned - 1) : Math.Max(to, pinned);
        if (to != from) Tabs.Move(from, to);
        Activate(tab);
    }
}
