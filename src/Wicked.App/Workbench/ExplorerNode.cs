using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core;
using Wicked.Core.Files;

namespace Wicked.App.Workbench;

/// <summary>
/// A file or folder in the explorer. A folder lists its children the first time it opens (read on a worker) and again
/// when the watcher sees it change; until then it holds one placeholder so the tree shows its expander.
/// </summary>
public sealed partial class ExplorerNode : ObservableObject
{
    private static readonly ExplorerNode Placeholder = new("…", "", false, null);
    private bool _loaded;
    private bool _loading;

    public ExplorerNode(string name, string fullPath, bool isDirectory, ExplorerNode? parent)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;
        if (isDirectory) Children.Add(Placeholder);
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public ExplorerNode? Parent { get; }

    public ObservableCollection<ExplorerNode> Children { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconKey))]
    private bool _isExpanded;

    /// <summary>Which icon the explorer draws for this node.</summary>
    public string IconKey => IsDirectory ? (IsExpanded ? "Icon.FolderOpen" : "Icon.Folder")
        : Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? "Icon.FileText" : "Icon.FileCode";

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded) _ = LoadAsync();
    }

    /// <summary>Lists the folder on a worker and brings the children up to date, keeping the nodes that are still there.</summary>
    public async Task LoadAsync()
    {
        if (!IsDirectory || _loading) return;
        _loading = true;
        try
        {
            var path = FullPath;
            var entries = await Task.Run(() => List(path));
            var existing = Children.Where(c => c != Placeholder).ToDictionary(c => c.Name, StringComparer.Ordinal);
            var fresh = entries.Select(e => existing.TryGetValue(e.Name, out var node) && node.IsDirectory == e.IsDirectory
                ? node
                : new ExplorerNode(e.Name, Path.Combine(path, e.Name), e.IsDirectory, this)).ToList();
            Children.Clear();
            foreach (var node in fresh) Children.Add(node);
            _loaded = true;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Re-reads this folder and every open folder below it (the watcher saw a change).</summary>
    public async Task RefreshAsync()
    {
        if (!_loaded) return;
        await LoadAsync();
        foreach (var child in Children.Where(c => c.IsDirectory && c.IsExpanded).ToList()) await child.RefreshAsync();
    }

    public ExplorerNode? Find(string fullPath)
    {
        if (string.Equals(FullPath, fullPath, StringComparison.Ordinal)) return this;
        foreach (var child in Children)
        {
            if (child.IsDirectory && fullPath.StartsWith(child.FullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal) || child.FullPath == fullPath)
            {
                return child.Find(fullPath);
            }
        }

        return null;
    }

    /// <summary>Folders first, then files, each in natural order ignoring case, hidden clutter left out.</summary>
    private static List<(string Name, bool IsDirectory)> List(string path)
    {
        try
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };
            return Directory.EnumerateFileSystemEntries(path, "*", options)
                .Select(e => (Name: Path.GetFileName(e), IsDirectory: Directory.Exists(e)))
                .Where(e => !IgnoreRules.Hidden.Contains(e.Name))
                .OrderBy(e => e.IsDirectory ? 0 : 1)
                .ThenBy(e => e.Name, NaturalComparer.Instance)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not list {path}: {ex.Message}");
            return [];
        }
    }
}

/// <summary>"file2" before "file10", case ignored: the order file managers use.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null || y is null) return string.Compare(x, y, StringComparison.Ordinal);
        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var byLength = a.Length.CompareTo(b.Length);
                if (byLength != 0) return byLength;
                var byDigits = string.CompareOrdinal(a, b);
                if (byDigits != 0) return byDigits;
                continue;
            }

            var c = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
            if (c != 0) return c;
            i++;
            j++;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
