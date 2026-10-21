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

    partial void OnActiveChanged(EditorTab? oldValue, EditorTab? newValue)
    {
        if (oldValue is not null) oldValue.IsActive = false;
        if (newValue is not null) newValue.IsActive = true;
        Workbench.ActiveTabChanged();
    }

    /// <summary>The view shows the active tab again (its editor was replaced).</summary>
    public void Reshow() => OnPropertyChanged(nameof(Active));

    public void Activate(EditorTab tab)
    {
        Active = tab;
        Workbench.Focus(this);
    }

    /// <summary>Adds a tab after the active one; a preview tab takes the place of the previous preview.</summary>
    public void Add(EditorTab tab)
    {
        if (tab.IsPreview && Tabs.FirstOrDefault(t => t.IsPreview) is { } preview)
        {
            var at = Tabs.IndexOf(preview);
            Tabs[at] = tab;
            preview.Close();
            Workbench.TabRemoved(preview);
        }
        else
        {
            var at = Active is null ? Tabs.Count : Tabs.IndexOf(Active) + 1;
            Tabs.Insert(at, tab);
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
        tab.Close();
        Workbench.TabRemoved(tab);
        if (Active == tab) Active = Tabs.Count == 0 ? null : Tabs[Math.Min(at, Tabs.Count - 1)];
    }

    public void Cycle(int direction)
    {
        if (Tabs.Count < 2 || Active is null) return;
        var at = (Tabs.IndexOf(Active) + direction + Tabs.Count) % Tabs.Count;
        Activate(Tabs[at]);
    }
}
