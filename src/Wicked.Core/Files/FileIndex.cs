using System.Collections.Immutable;

namespace Wicked.Core.Files;

/// <summary>
/// Every file in a workspace that quick open and search consider, as paths relative to the root, gathered on a worker
/// with the ignore rules applied. Immutable: a rescan builds a new one.
/// </summary>
public sealed class FileIndex
{
    private FileIndex(string root, ImmutableArray<string> files)
    {
        Root = root;
        Files = files;
    }

    public string Root { get; }

    /// <summary>Relative paths with '/' separators, sorted.</summary>
    public ImmutableArray<string> Files { get; }

    public static FileIndex Empty(string root) => new(root, []);

    /// <summary>Walks the workspace; stops at <paramref name="limit"/> files (VS Code warns past a similar size).</summary>
    public static FileIndex Build(string root, CancellationToken ct, int limit = 200_000)
    {
        var rules = new IgnoreRules();
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push("");
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false };
        while (pending.Count > 0 && files.Count < limit)
        {
            ct.ThrowIfCancellationRequested();
            var relative = pending.Pop();
            var full = relative.Length == 0 ? root : Path.Combine(root, relative);
            var gitignore = Path.Combine(full, ".gitignore");
            if (File.Exists(gitignore))
            {
                try
                {
                    rules.AddGitignore(relative, File.ReadLines(gitignore));
                }
                catch (IOException)
                {
                }
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(full, "*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var path = relative.Length == 0 ? name : relative + "/" + name;
                var isDirectory = Directory.Exists(entry);
                if (IgnoreRules.Hidden.Contains(name)) continue;
                if (isDirectory && IgnoreRules.Heavy.Contains(name)) continue;
                if (rules.IsIgnored(path, isDirectory)) continue;
                if (isDirectory) pending.Push(path);
                else files.Add(path);
            }
        }

        files.Sort(StringComparer.Ordinal);
        return new FileIndex(root, [.. files]);
    }

    /// <summary>The best <paramref name="max"/> matches for a quick-open query, best first.</summary>
    public IReadOnlyList<string> Search(string query, int max = 50)
    {
        if (query.Length == 0) return [.. Files.Take(max)];
        var scored = new List<(int Score, string Path)>();
        foreach (var file in Files)
        {
            var score = FuzzyMatch.Score(query, file);
            if (score >= 0) scored.Add((score, file));
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenBy(s => s.Path, StringComparer.Ordinal).Take(max).Select(s => s.Path)];
    }
}
