using System.Globalization;
using System.Text.RegularExpressions;

namespace Wicked.Core.Terminal;

public enum LinkKind
{
    /// <summary>An address with a scheme: https, http, ftp, file, mailto and the like.</summary>
    Url,

    /// <summary>Something that looks like a file, maybe with a line and column; whether it exists is for the caller to check.</summary>
    Path,
}

/// <summary>A link found in a line of text: where it is (UTF-16 offsets into the line) and what it points at.</summary>
/// <param name="Start">The first character.</param>
/// <param name="Length">The number of characters.</param>
/// <param name="Kind">An address or a file.</param>
/// <param name="Target">The address, or the path as written.</param>
/// <param name="Line">For a path, the line number given with it (counted from 1).</param>
/// <param name="Column">For a path, the column given with it (counted from 1).</param>
public sealed record TerminalLink(int Start, int Length, LinkKind Kind, string Target, int? Line = null, int? Column = null);

/// <summary>Finds addresses and file references (<c>src/app.cs:12:5</c>, <c>app.cs(12,5)</c>, <c>File "x.py", line 3</c>) in terminal text.</summary>
public static partial class LinkDetector
{
    // A scheme, then characters an address may hold; trailing punctuation that ends a sentence is not part of it.
    [GeneratedRegex(@"\b(?:https?|ftp|file|ssh|git)://[^\s<>""'`\x00-\x1f]+|\bmailto:[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex UrlPattern();

    // ./rel, ../rel, ~/home, /abs, C:\win, or a bare name with a directory or an extension; then :line[:col] or (line[,col]).
    [GeneratedRegex(@"(?<![\w./\\~:@-])(?<path>(?:[A-Za-z]:[\\/]|~[\\/]|\.{1,2}[\\/]|/)?(?:[\w.@+~-]+[\\/])*[\w.@+~-]*[\w@+~-]\.[A-Za-z0-9_+-]{1,12}|(?:\.{1,2}[\\/]|~[\\/]|/|[A-Za-z]:[\\/])(?:[\w.@+~-]+[\\/])*[\w.@+~-]+)(?:(?<loc>:(?<l1>\d+)(?::(?<c1>\d+))?)|(?<paren>\((?<l2>\d+)(?:,\s*(?<c2>\d+))?\)))?", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex PathPattern();

    // Python tracebacks: File "path", line 3
    [GeneratedRegex(@"File ""(?<path>[^""]+)"", line (?<l>\d+)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex PythonPattern();

    /// <summary>Every link in <paramref name="text"/>, in order, none overlapping.</summary>
    public static List<TerminalLink> Find(string text)
    {
        var links = new List<TerminalLink>();
        if (text.Length < 3) return links;
        try
        {
            foreach (Match m in UrlPattern().Matches(text))
            {
                var (length, target) = TrimUrl(m.Value);
                if (length > 8 || m.Value.StartsWith("mailto", StringComparison.OrdinalIgnoreCase)) links.Add(new TerminalLink(m.Index, length, LinkKind.Url, target));
            }

            foreach (Match m in PythonPattern().Matches(text))
            {
                if (Overlaps(links, m.Index, m.Length)) continue;
                var pathGroup = m.Groups["path"];
                links.Add(new TerminalLink(m.Index, m.Length, LinkKind.Path, pathGroup.Value, Number(m.Groups["l"]), null));
            }

            foreach (Match m in PathPattern().Matches(text))
            {
                var path = m.Groups["path"];
                if (Overlaps(links, m.Index, m.Length) || !LooksLikeAPath(path.Value)) continue;
                if (!m.Groups["loc"].Success && !m.Groups["paren"].Success && !HasDirectory(path.Value) && !KnownExtensions.Contains(Extension(path.Value))) continue;
                int? line = null, column = null;
                if (m.Groups["loc"].Success)
                {
                    line = Number(m.Groups["l1"]);
                    column = Number(m.Groups["c1"]);
                }
                else if (m.Groups["paren"].Success)
                {
                    line = Number(m.Groups["l2"]);
                    column = Number(m.Groups["c2"]);
                }

                links.Add(new TerminalLink(m.Index, m.Length, LinkKind.Path, path.Value, line, column));
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // A line that takes this long to read has no links worth waiting for.
        }

        links.Sort((a, b) => a.Start.CompareTo(b.Start));
        return links;
    }

    /// <summary>The link covering character <paramref name="offset"/> of the text, or null.</summary>
    public static TerminalLink? At(string text, int offset)
    {
        foreach (var link in Find(text))
        {
            if (offset >= link.Start && offset < link.Start + link.Length) return link;
        }

        return null;
    }

    private static bool Overlaps(List<TerminalLink> links, int start, int length)
    {
        foreach (var l in links)
        {
            if (start < l.Start + l.Length && l.Start < start + length) return true;
        }

        return false;
    }

    private static int? Number(Group g) => g.Success && int.TryParse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static (int Length, string Target) TrimUrl(string url)
    {
        var end = url.Length;
        while (end > 0)
        {
            var c = url[end - 1];
            if (c is '.' or ',' or ';' or ':' or '!' or '?' or '\'' or '"') end--;
            else if (c == ')' && url[..end].Count(x => x == '(') < url[..end].Count(x => x == ')')) end--;
            else if (c == ']' && url[..end].Count(x => x == '[') < url[..end].Count(x => x == ']')) end--;
            else if (c == '}' && url[..end].Count(x => x == '{') < url[..end].Count(x => x == '}')) end--;
            else break;
        }

        return (end, url[..end]);
    }

    private static bool HasDirectory(string path) => path.AsSpan().IndexOfAny('/', '\\') >= 0;

    private static string Extension(string path)
    {
        var dot = path.LastIndexOf('.');
        return dot < 0 ? "" : path[(dot + 1)..].ToLowerInvariant();
    }

    // A bare name with a dot (Foo.Bar) is as likely to be code as a file: it counts as a file when its ending is one files have, or a position follows it.
    private static readonly HashSet<string> KnownExtensions = new(StringComparer.Ordinal)
    {
        "cs", "csx", "fs", "vb", "js", "mjs", "cjs", "ts", "tsx", "jsx", "py", "pyi", "rs", "go", "java", "kt", "kts", "scala", "c", "h", "cc", "cpp", "cxx", "hpp", "hh", "hxx", "m", "mm", "swift", "rb", "php", "pl", "pm", "lua", "sh", "bash", "zsh", "fish", "ps1", "bat", "cmd",
        "json", "jsonc", "yaml", "yml", "toml", "xml", "html", "htm", "css", "scss", "sass", "less", "md", "txt", "log", "ini", "cfg", "conf", "env", "lock", "sln", "csproj", "fsproj", "props", "targets", "axaml", "xaml", "razor", "cshtml", "sql", "gradle", "mk", "cmake", "dockerfile",
        "vue", "svelte", "dart", "ex", "exs", "erl", "hs", "ml", "clj", "zig", "nim", "jl", "tex", "csv", "tsv", "rst", "adoc", "proto", "graphql", "tf", "patch", "diff", "png", "jpg", "jpeg", "gif", "svg", "pdf", "zip", "gz", "tar", "so", "dll", "exe", "bin", "o", "a", "d", "map",
    };

    private static bool LooksLikeAPath(string path)
    {
        // A version number or a decimal is not a file; a path has a letter in its name part.
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.Length == 0) return path.Length > 1;
        if (!name.Any(char.IsLetter)) return false;
        // "e.g." and "i.e." read as files to the pattern.
        if (name.Length <= 4 && name.Count(c => c == '.') >= 2 && !path.Contains('/', StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal)) return false;
        return true;
    }
}
