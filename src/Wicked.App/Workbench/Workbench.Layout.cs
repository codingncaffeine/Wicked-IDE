namespace Wicked.App.Workbench;

/// <summary>
/// The editor grid: splitting a group in any direction (the new group shows the same file, as VS Code's split does),
/// focusing and moving editors between groups by direction, closing an empty group, and maximising one group.
/// </summary>
public sealed partial class Workbench
{
    private EditorLayout? _layout;

    /// <summary>How the groups are laid out (rows and columns).</summary>
    public EditorLayout Layout => _layout ??= new EditorLayout(Groups[0]);

    /// <summary>The groups' arrangement changed (a split, a closed group, a maximised one).</summary>
    public event Action? LayoutChanged;

    /// <summary>The group shown alone (Ctrl+K Ctrl+M), or null when every group shows.</summary>
    public EditorGroup? MaximizedGroup { get; private set; }

    /// <summary>A new group beside the active one (or <paramref name="beside"/>); with <paramref name="copyActive"/>, it opens that group's file too.</summary>
    public EditorGroup Split(SplitDirection direction, bool copyActive = true, EditorGroup? beside = null)
    {
        var from = beside ?? ActiveGroup;
        var group = new EditorGroup(this);
        Layout.Split(from, group, direction);
        MaximizedGroup = null;
        SyncGroups();
        if (copyActive && from.Active is { ViewFactory: null } tab)
        {
            group.Add(new EditorTab(tab.Path, tab.Document, tab.Language, tab.Encoding, tab.LastWriteUtc));
        }

        Focus(group);
        LayoutChanged?.Invoke();
        return group;
    }

    /// <summary>The group to the right of the active one, made (empty) when there is none: where "to the side" opens.</summary>
    public EditorGroup SideGroup() => Layout.Neighbour(ActiveGroup, SplitDirection.Right) ?? Split(SplitDirection.Right, copyActive: false);

    /// <summary>Takes an empty group out of the grid; the keyboard goes to the group that took its place.</summary>
    private void RemoveGroup(EditorGroup group)
    {
        if (Groups.Count < 2) return;
        var next = Layout.Neighbour(group, SplitDirection.Left) ?? Layout.Neighbour(group, SplitDirection.Up)
            ?? Layout.Neighbour(group, SplitDirection.Right) ?? Layout.Neighbour(group, SplitDirection.Down) ?? Groups.First(g => g != group);
        Layout.Remove(group);
        if (MaximizedGroup == group) MaximizedGroup = null;
        SyncGroups();
        Focus(next);
        LayoutChanged?.Invoke();
    }

    /// <summary>The groups list in the grid's reading order (Ctrl+1, Ctrl+2... count along it).</summary>
    private void SyncGroups()
    {
        var order = Layout.Groups.ToList();
        foreach (var gone in Groups.Where(g => !order.Contains(g)).ToList()) Groups.Remove(gone);
        for (var i = 0; i < order.Count; i++)
        {
            var at = Groups.IndexOf(order[i]);
            if (at < 0) Groups.Insert(i, order[i]);
            else if (at != i) Groups.Move(at, i);
        }
    }

    /// <summary>Ctrl+K Ctrl+Arrow: the keyboard to the group in that direction.</summary>
    public void FocusDirection(SplitDirection direction)
    {
        if (Layout.Neighbour(ActiveGroup, direction) is not { } group) return;
        Focus(group);
        group.Reshow();
    }

    /// <summary>Moves the active editor to the group in that direction, making one at the edge (as VS Code does).</summary>
    public void MoveActiveEditor(SplitDirection direction)
    {
        if (ActiveTab is not { } tab) return;
        var from = ActiveGroup;
        var to = Layout.Neighbour(from, direction);
        if (to is null)
        {
            if (from.Tabs.Count < 2) return; // alone in its group: there is nothing to split away from
            to = Split(direction, copyActive: false);
        }

        MoveTab(tab, from, to);
    }

    /// <summary>Moves the active editor to the next (1) or previous (-1) group in reading order.</summary>
    public void MoveActiveEditorAlong(int step)
    {
        if (ActiveTab is not { } tab || Groups.Count < 2) return;
        var at = Groups.IndexOf(ActiveGroup) + step;
        if (at < 0 || at >= Groups.Count) return;
        MoveTab(tab, ActiveGroup, Groups[at]);
    }

    /// <summary>A tab goes to another group with its editor (not closed and opened again); an emptied group closes.</summary>
    public void MoveTab(EditorTab tab, EditorGroup from, EditorGroup to)
    {
        if (from == to || !from.Tabs.Contains(tab)) return;
        if (to.Tabs.FirstOrDefault(t => t.Path is not null && t.Path == tab.Path) is { } there)
        {
            // The file is open there already: that tab comes forward and this one goes.
            from.Detach(tab);
            tab.Close();
            TabRemoved(tab);
            to.Activate(there);
        }
        else
        {
            from.Detach(tab);
            to.Attach(tab);
        }

        if (from.Tabs.Count == 0) RemoveGroup(from);
        else LayoutChanged?.Invoke();
        Focus(to);
        to.Reshow();
    }

    /// <summary>Ctrl+K Ctrl+M: the active group alone, or every group again.</summary>
    public void ToggleMaximizeGroup()
    {
        MaximizedGroup = MaximizedGroup is null && Groups.Count > 1 ? ActiveGroup : null;
        LayoutChanged?.Invoke();
    }
}

public sealed partial class Workbench
{
    /// <summary>Close All Editors in Group (Ctrl+K W): its tabs, pinned ones too, each asked about if unsaved.</summary>
    public Task CloseGroupTabsAsync(EditorGroup group) => CloseWhereAsync(group, (_, _) => true);

    /// <summary>The keyboard to the next (1) or previous (-1) group in reading order, round from the last to the first.</summary>
    public void FocusAlong(int step)
    {
        if (Groups.Count < 2) return;
        var group = Groups[(Groups.IndexOf(ActiveGroup) + step + Groups.Count) % Groups.Count];
        Focus(group);
        group.Reshow();
    }

    /// <summary>Ctrl+Shift+PageUp / PageDown: the active tab one place left or right along its strip (not past the pinned ones).</summary>
    public void MoveActiveTabInGroup(int step)
    {
        if (ActiveTab is not { } tab) return;
        var from = ActiveGroup.Tabs.IndexOf(tab);
        // MoveWithin takes the place to insert before, counted with the tab still in the strip.
        ActiveGroup.MoveWithin(tab, step > 0 ? from + 2 : Math.Max(0, from - 1));
    }

    /// <summary>Alt+1..9: the active group's tab at that place; Alt+0 (-1): its last.</summary>
    public void ActivateTabAt(int index)
    {
        var tabs = ActiveGroup.Tabs;
        if (tabs.Count == 0) return;
        if (index < 0) index = tabs.Count - 1;
        if (index < tabs.Count) ActiveGroup.Activate(tabs[index]);
    }

    /// <summary>Every group back to an even share of its row or column.</summary>
    public void EvenGroupSizes()
    {
        Layout.EvenSizes();
        LayoutChanged?.Invoke();
    }

    /// <summary>The active group's editors move into the next group (the previous one when it is the last), and it closes.</summary>
    public void JoinWithNextGroup()
    {
        if (Groups.Count < 2) return;
        var from = ActiveGroup;
        var at = Groups.IndexOf(from);
        var into = at + 1 < Groups.Count ? Groups[at + 1] : Groups[at - 1];
        JoinInto(from, into, ActiveTab);
    }

    /// <summary>Every group's editors move into the active group, which is then the only one.</summary>
    public void JoinAllGroups()
    {
        var into = ActiveGroup;
        var keep = ActiveTab;
        foreach (var group in Groups.Where(g => g != into).ToList()) JoinInto(group, into, keep);
    }

    /// <summary>Moves a group's editors into another; the editor that was active (<paramref name="keep"/>) stays active.</summary>
    private void JoinInto(EditorGroup from, EditorGroup into, EditorTab? keep)
    {
        foreach (var tab in from.Tabs.ToList()) MoveTab(tab, from, into);
        if (from.Tabs.Count == 0 && Groups.Contains(from)) RemoveGroup(from); // an empty group has no tab whose move closes it
        if (keep is not null && into.Tabs.Contains(keep)) into.Activate(keep);
        Focus(into);
    }
}

/// <summary>Where a dragged tab is let go over a group: its tab strip (at an index), its middle, or one of its edges.</summary>
public enum TabDropZone
{
    Strip,
    Middle,
    Left,
    Right,
    Up,
    Down,
}

public sealed partial class Workbench
{
    /// <summary>
    /// A tab dragged and let go, as VS Code's tabs drag: on a strip it goes to that place (reordered in its own group, moved
    /// into another); on a group's middle it moves into that group; on an edge that group is split that way and the tab
    /// moves into the new part.
    /// </summary>
    public void DropTab(EditorTab tab, EditorGroup from, EditorGroup target, TabDropZone zone, int index = -1)
    {
        if (!from.Tabs.Contains(tab)) return;
        if (zone is TabDropZone.Strip or TabDropZone.Middle)
        {
            if (target == from)
            {
                if (zone == TabDropZone.Strip && index >= 0) from.MoveWithin(tab, index);
                return;
            }

            MoveTab(tab, from, target);
            if (zone == TabDropZone.Strip && index >= 0) target.MoveWithin(tab, index);
            return;
        }

        // An edge: alone in its own group there is nothing to split away from.
        if (target == from && from.Tabs.Count == 1) return;
        var direction = zone switch
        {
            TabDropZone.Left => SplitDirection.Left,
            TabDropZone.Right => SplitDirection.Right,
            TabDropZone.Up => SplitDirection.Up,
            _ => SplitDirection.Down,
        };
        var fresh = Split(direction, copyActive: false, beside: target);
        MoveTab(tab, from, fresh);
    }
}
