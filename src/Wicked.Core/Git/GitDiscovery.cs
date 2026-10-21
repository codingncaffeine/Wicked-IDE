namespace Wicked.Core.Git;

/// <summary>Finds the repositories in an opened folder: the one it belongs to, and any nested inside it (submodules, cloned folders, linked work trees).</summary>
public static class GitDiscovery
{
    /// <summary>Folders never searched for repositories: they hold thousands of entries and no project of the user's.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", "bower_components", "bin", "obj", "target", ".venv", "venv", "__pycache__", ".gradle", ".idea", ".vs", ".cache", "dist", "build",
    };

    private const int MaxDepth = 5;
    private const int MaxFolders = 20_000;

    /// <summary>
    /// The repository containing <paramref name="folder"/> (it may be above it), then those found below it, the outer ones first.
    /// The search stops at <see cref="MaxDepth"/> levels and after a fixed number of folders, so a huge tree costs a bounded time.
    /// </summary>
    public static async Task<IReadOnlyList<GitRepository>> FindAsync(string folder, GitEnvironment env, CancellationToken ct = default)
    {
        var found = new List<GitRepository>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (await GitRepository.OpenAsync(folder, env, ct).ConfigureAwait(false) is { } containing && seen.Add(containing.Root)) found.Add(containing);
        foreach (var path in await Task.Run(() => NestedCandidates(folder, ct), ct).ConfigureAwait(false))
        {
            if (await GitRepository.OpenAsync(path, env, ct).ConfigureAwait(false) is { } repo && seen.Add(repo.Root)) found.Add(repo);
        }

        return found;
    }

    private static List<string> NestedCandidates(string root, CancellationToken ct)
    {
        var result = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;
        while (queue.Count > 0 && visited < MaxFolders)
        {
            ct.ThrowIfCancellationRequested();
            var (path, depth) = queue.Dequeue();
            visited++;
            if (depth > 0 && (Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git")))) result.Add(path);
            if (depth >= MaxDepth) continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(path))
                {
                    var name = Path.GetFileName(child);
                    if (Skipped.Contains(name)) continue;
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue; // a link could lead out of the folder or loop
                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that cannot be listed holds no repository for us.
            }
        }

        return result;
    }
}
