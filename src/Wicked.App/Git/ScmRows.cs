using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Wicked.Core.Git;

namespace Wicked.App.Git;

/// <summary>The groups the Source Control panel lists, in VS Code's order.</summary>
public enum ScmGroup
{
    Merge,
    Staged,
    Changes,
    Untracked,
}

/// <summary>One row of the changes list: a group heading, a folder (in tree mode) or a file.</summary>
public abstract class ScmRow
{
    /// <summary>Identifies the row across refreshes, so a selection and a scroll position survive one.</summary>
    public abstract string Key { get; }
}

public sealed class ScmGroupRow(ScmGroup group, int count, bool expanded) : ScmRow
{
    public ScmGroup Group { get; } = group;

    public int Count { get; } = count;

    public bool Expanded { get; } = expanded;

    public string Title => Group switch
    {
        ScmGroup.Merge => "Merge Changes",
        ScmGroup.Staged => "Staged Changes",
        ScmGroup.Changes => "Changes",
        _ => "Untracked Changes",
    };

    public string Chevron => Expanded ? "Icon.ChevronDown" : "Icon.ChevronRight";

    public bool CanStageAll => Group is ScmGroup.Changes or ScmGroup.Untracked or ScmGroup.Merge;

    public bool CanUnstageAll => Group == ScmGroup.Staged;

    public bool CanDiscardAll => Group is ScmGroup.Changes or ScmGroup.Untracked;

    public string StageAllTip => Group == ScmGroup.Merge ? "Mark all as resolved" : "Stage all";

    public override string Key => "group:" + Group;
}

public sealed class ScmFolderRow(ScmGroup group, string path, int depth, bool expanded, int count) : ScmRow
{
    public ScmGroup Group { get; } = group;

    public string Path { get; } = path;

    public string Name { get; } = path[(path.LastIndexOf('/') + 1)..]; // not a path: a repository path with '/'

    public int Depth { get; } = depth;

    public bool Expanded { get; } = expanded;

    public int Count { get; } = count;

    public double Indent => 8 + (Depth * 12);

    public string Chevron => Expanded ? "Icon.ChevronDown" : "Icon.ChevronRight";

    public override string Key => $"folder:{Group}:{Path}";
}

public sealed class ScmFileRow : ScmRow
{
    public ScmFileRow(GitChange change, ScmGroup group, int depth, bool tree)
    {
        Change = change;
        Group = group;
        Depth = depth;
        var slash = change.Path.LastIndexOf('/'); // not a path: a repository path with '/'
        Name = slash < 0 ? change.Path : change.Path[(slash + 1)..];
        Directory = tree || slash < 0 ? "" : change.Path[..slash];
        var kind = group switch
        {
            ScmGroup.Staged => change.Index,
            ScmGroup.Untracked => GitChangeKind.Untracked,
            ScmGroup.Merge => GitChangeKind.Conflicted,
            _ => change.WorkTree,
        };
        (Letter, Tone) = kind switch
        {
            GitChangeKind.Modified => ("M", "modified"),
            GitChangeKind.Added => ("A", "added"),
            GitChangeKind.Deleted => ("D", "deleted"),
            GitChangeKind.Renamed => ("R", "renamed"),
            GitChangeKind.Copied => ("C", "renamed"),
            GitChangeKind.TypeChanged => ("T", "modified"),
            GitChangeKind.Untracked => ("U", "untracked"),
            GitChangeKind.Conflicted => ("!", "conflict"),
            _ => ("?", "modified"),
        };
        Tooltip = change.IsConflicted ? $"{change.Path} - {change.ConflictText}" : change.OriginalPath is null ? change.Path : $"{change.OriginalPath} -> {change.Path}";
        Detail = change.IsConflicted ? change.ConflictText : change.OriginalPath is { } from ? $"from {from}" : "";
    }

    public GitChange Change { get; }

    public ScmGroup Group { get; }

    public int Depth { get; }

    public string Name { get; }

    /// <summary>The folder, shown quietly after the name in list mode.</summary>
    public string Directory { get; }

    /// <summary>For a conflict or a rename: the words that say more.</summary>
    public string Detail { get; }

    public string Letter { get; }

    public string Tone { get; }

    public string Tooltip { get; }

    public double Indent => 8 + (Depth * 12) + 16;

    public bool IsStaged => Group == ScmGroup.Staged;

    public bool IsConflict => Group == ScmGroup.Merge;

    public bool CanStage => Group != ScmGroup.Staged;

    public bool CanDiscard => Group is ScmGroup.Changes or ScmGroup.Untracked;

    public bool IsDeleted => Letter == "D";

    public string Path => Change.Path;

    public override string Key => $"file:{Group}:{Change.Path}";
}

/// <summary>Builds the rows for a status, on a worker: groups with counts, and in tree mode the folders between them.</summary>
public static class ScmRowBuilder
{
    public static ScmRow[] Build(GitStatus status, bool tree, ISet<ScmGroup> collapsedGroups, ISet<string> collapsedFolders)
    {
        var rows = new List<ScmRow>();
        void Group(ScmGroup group, IReadOnlyList<GitChange> changes)
        {
            if (changes.Count == 0) return;
            var open = !collapsedGroups.Contains(group);
            rows.Add(new ScmGroupRow(group, changes.Count, open));
            if (!open) return;
            if (!tree)
            {
                foreach (var c in changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase)) rows.Add(new ScmFileRow(c, group, 0, false));
                return;
            }

            AddTree(rows, group, changes, collapsedFolders);
        }

        Group(ScmGroup.Merge, [.. status.Conflicts]);
        Group(ScmGroup.Staged, [.. status.Staged]);
        Group(ScmGroup.Changes, [.. status.Unstaged]);
        Group(ScmGroup.Untracked, [.. status.Untracked]);
        return [.. rows];
    }

    private static void AddTree(List<ScmRow> rows, ScmGroup group, IReadOnlyList<GitChange> changes, ISet<string> collapsedFolders)
    {
        var filesIn = new Dictionary<string, List<GitChange>>(StringComparer.Ordinal);
        var foldersIn = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var change in changes.OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var parent = ParentOf(change.Path);
            if (!filesIn.TryGetValue(parent, out var list)) filesIn[parent] = list = [];
            list.Add(change);
            for (var folder = parent; folder.Length > 0; folder = ParentOf(folder))
            {
                counts[folder] = counts.GetValueOrDefault(folder) + 1;
                var up = ParentOf(folder);
                if (!foldersIn.TryGetValue(up, out var set)) foldersIn[up] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(folder);
            }
        }

        // Folders before files within a folder, as the explorer lists them.
        void Level(string parent, int depth)
        {
            if (foldersIn.TryGetValue(parent, out var folders))
            {
                foreach (var folder in folders)
                {
                    var open = !collapsedFolders.Contains($"{group}:{folder}");
                    rows.Add(new ScmFolderRow(group, folder, depth, open, counts[folder]));
                    if (open) Level(folder, depth + 1);
                }
            }

            if (filesIn.TryGetValue(parent, out var files))
            {
                foreach (var c in files) rows.Add(new ScmFileRow(c, group, depth, true));
            }
        }

        Level("", 0);
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/'); // not a path: a repository path with '/'
        return slash < 0 ? "" : path[..slash];
    }
}

/// <summary>A change's letter colour, taken from the theme's tokens.</summary>
public sealed class ScmToneBrush : IValueConverter
{
    public static readonly ScmToneBrush Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            "added" or "untracked" => "Brush.Success",
            "deleted" or "conflict" => "Brush.Danger",
            "renamed" => "Brush.Accent",
            _ => "Brush.Warning",
        };
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true ? brush as IBrush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
