using System.Text;
using System.Text.RegularExpressions;

namespace Wicked.Core.Files;

/// <summary>
/// Which paths a workspace leaves out of quick open and search: VS Code's default exclusions plus the patterns of the
/// workspace's .gitignore files (git's syntax: globs, "**", a leading "/" anchors, a trailing "/" means folders only,
/// "!" brings a path back). Paths are relative to the workspace root, with '/' separators.
/// </summary>
public sealed class IgnoreRules
{
    /// <summary>Never listed: version-control internals and system clutter (VS Code's files.exclude).</summary>
    public static readonly string[] Hidden = [".git", ".svn", ".hg", "CVS", ".DS_Store", "Thumbs.db"];

    /// <summary>Listed in the explorer but left out of quick open and search (VS Code's search.exclude, plus build output).</summary>
    public static readonly string[] Heavy = ["node_modules", "bower_components", "bin", "obj", "target", ".venv", "__pycache__", ".gradle", ".idea", ".vs"];

    private readonly List<(Regex Pattern, bool Negated, bool DirectoryOnly, string Base)> _rules = [];

    /// <summary>Adds the patterns of a .gitignore found in <paramref name="baseDirectory"/> (relative to the root; "" for the root).</summary>
    public void AddGitignore(string baseDirectory, IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var negated = line.StartsWith('!');
            if (negated) line = line[1..];
            var directoryOnly = line.EndsWith('/'); // not a path: gitignore syntax
            line = line.TrimEnd('/'); // not a path
            var anchored = line.Contains('/');
            line = line.TrimStart('/'); // not a path
            if (line.Length == 0) continue;
            var regex = GlobToRegex(line, anchored);
            _rules.Add((new Regex(regex, RegexOptions.CultureInvariant | RegexOptions.Compiled), negated, directoryOnly, baseDirectory));
        }
    }

    /// <summary>Whether a path is ignored by the .gitignore rules (the last matching rule wins, as in git).</summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var ignored = false;
        foreach (var (pattern, negated, directoryOnly, @base) in _rules)
        {
            if (directoryOnly && !isDirectory) continue;
            if (@base.Length > 0 && !relativePath.StartsWith(@base + "/", StringComparison.Ordinal)) continue;
            var local = @base.Length > 0 ? relativePath[(@base.Length + 1)..] : relativePath;
            if (pattern.IsMatch(local)) ignored = !negated;
        }

        return ignored;
    }

    /// <summary>A gitignore glob as a regex over a relative path: unanchored patterns match a name at any depth.</summary>
    internal static string GlobToRegex(string glob, bool anchored)
    {
        var b = new StringBuilder(anchored ? "^" : "^(?:.*/)?");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" matches any number of folders, a trailing "**" everything below.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    b.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    b.Append(".*");
                    i++;
                }
            }
            else if (c == '*') b.Append("[^/]*");
            else if (c == '?') b.Append("[^/]");
            else if (c == '[')
            {
                var close = glob.IndexOf(']', i + 1);
                if (close < 0) b.Append("\\[");
                else
                {
                    var set = glob[(i + 1)..close].Replace("\\", "\\\\");
                    b.Append('[').Append(set.StartsWith('!') ? "^" + set[1..] : set).Append(']');
                    i = close;
                }
            }
            else b.Append(Regex.Escape(c.ToString()));
        }

        // A pattern naming a folder also covers everything inside it.
        b.Append("(?:/.*)?$");
        return b.ToString();
    }
}
