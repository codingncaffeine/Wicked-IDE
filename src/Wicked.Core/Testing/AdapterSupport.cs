namespace Wicked.Core.Testing;

/// <summary>What the adapters share: finding project folders, and putting a test's name path into a tree.</summary>
internal static class AdapterSupport
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "node_modules", ".git", "bin", "obj", "target", "dist", "build", "out", ".venv", "venv", "env", "__pycache__", "vendor", ".vs", ".idea", ".tox", ".mypy_cache", ".pytest_cache",
    };

    /// <summary>Folders under <paramref name="root"/> (itself included) down to <paramref name="depth"/>, build output and dependency folders left out.</summary>
    public static IEnumerable<string> Folders(string root, int depth)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        while (pending.Count > 0)
        {
            var (path, d) = pending.Dequeue();
            yield return path;
            if (d >= depth) continue;
            string[] children;
            try
            {
                children = Directory.GetDirectories(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(children, StringComparer.Ordinal);
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || Skipped.Contains(name)) continue;
                pending.Enqueue((child, d + 1));
            }
        }
    }

    /// <summary>Files named like <paramref name="pattern"/> in the folders down to <paramref name="depth"/>.</summary>
    public static IEnumerable<string> Files(string root, string pattern, int depth)
    {
        foreach (var folder in Folders(root, depth))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, pattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files) yield return f;
        }
    }

    /// <summary>The group below <paramref name="parent"/> called <paramref name="name"/>, made when it is not there yet.</summary>
    public static TestNode Group(TestNode parent, Dictionary<string, TestNode> byId, string id, string name, string detail = "")
    {
        if (byId.TryGetValue(id, out var existing)) return existing;
        var node = new TestNode(id, name, TestKind.Group) { Detail = detail };
        parent.Add(node);
        byId[id] = node;
        return node;
    }

    public static string? FirstError(IEnumerable<string> lines, int max = 12)
    {
        var errors = lines.Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(max).ToList();
        return errors.Count == 0 ? null : string.Join('\n', errors);
    }

    /// <summary>The first file under the project whose text matches, found by reading source files on a worker.</summary>
    public static (string File, int Line)? Search(string folder, string filePattern, System.Text.RegularExpressions.Regex pattern, CancellationToken ct)
    {
        foreach (var file in Files(folder, filePattern, 6))
        {
            ct.ThrowIfCancellationRequested();
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            for (var i = 0; i < lines.Length; i++)
            {
                if (pattern.IsMatch(lines[i])) return (file, i + 1);
            }
        }

        return null;
    }
}
