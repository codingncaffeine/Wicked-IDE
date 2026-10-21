using System.Text;
using System.Text.RegularExpressions;
using Wicked.Core.Files;

namespace Wicked.Core.Search;

/// <summary>What to look for across a folder: the text, VS Code's three toggles, and the include and exclude globs.</summary>
public sealed record ProjectQuery(string Text, bool MatchCase = false, bool WholeWord = false, bool Regex = false, string Include = "", string Exclude = "")
{
    /// <summary>The query as a regular expression, or the reason it is not one.</summary>
    public (Regex? Regex, string? Error) Compile()
    {
        if (Text.Length == 0) return (null, null);
        var pattern = Regex ? Text : System.Text.RegularExpressions.Regex.Escape(Text);
        if (WholeWord) pattern = $@"(?<![\p{{L}}\p{{N}}_])(?:{pattern})(?![\p{{L}}\p{{N}}_])";
        try
        {
            var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (MatchCase ? 0 : RegexOptions.IgnoreCase);
            return (new Regex(pattern, options, TimeSpan.FromSeconds(2)), null);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }
    }
}

/// <summary>One match: its line and column (0-based), its length, and the line's text around it for the results list.</summary>
public sealed record TextMatch(int Line, int Column, int Length, int Offset, string Preview, int PreviewStart);

/// <summary>A file's matches, in order.</summary>
public sealed record FileMatches(string Path, IReadOnlyList<TextMatch> Matches);

/// <summary>A search's end: how many files were looked at, whether it stopped at the limit, a query error.</summary>
public sealed record SearchSummary(int FilesSearched, int Matches, int FilesWithMatches, bool Capped, string? Error);

/// <summary>
/// Searches a folder's files in parallel (D21): the workspace's file list with its ignore rules, the include and exclude
/// globs, binary and very large files skipped, the unsaved text of open files instead of what is on disk. Results come
/// out file by file as they are found; a newer search cancels the older one. Replacement writes files atomically.
/// </summary>
public static class ProjectSearch
{
    public const int MatchCap = 20_000;
    private const long LargeFile = 20 * 1024 * 1024;
    private const int PreviewWidth = 120;

    public static async Task<SearchSummary> RunAsync(
        string root,
        IReadOnlyList<string> files,
        ProjectQuery query,
        IReadOnlyDictionary<string, Func<string>> openTexts,
        Action<FileMatches> found,
        CancellationToken ct)
    {
        var (regex, error) = query.Compile();
        if (regex is null) return new SearchSummary(0, 0, 0, false, error);
        var include = GlobSet.Parse(query.Include);
        var exclude = GlobSet.Parse(query.Exclude);
        var chosen = files.Where(f => (include.IsEmpty || include.Matches(f)) && !exclude.Matches(f)).ToList();
        var total = 0;
        var withMatches = 0;
        var searched = 0;
        var capped = 0;
        var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount - 1) };
        try
        {
            await Parallel.ForEachAsync(chosen, options, async (relative, token) =>
            {
                if (Volatile.Read(ref capped) != 0) return;
                var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var text = openTexts.TryGetValue(path, out var open) ? open() : await ReadAsync(path, token).ConfigureAwait(false);
                Interlocked.Increment(ref searched);
                if (text is null) return;
                var matches = Find(text, regex, token);
                if (matches.Count == 0) return;
                var now = Interlocked.Add(ref total, matches.Count);
                if (now > MatchCap) Volatile.Write(ref capped, 1);
                Interlocked.Increment(ref withMatches);
                found(new FileMatches(path, matches));
            }).ConfigureAwait(false);
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchSummary(searched, total, withMatches, true, "The regular expression took too long");
        }

        return new SearchSummary(searched, total, withMatches, capped != 0, null);
    }

    /// <summary>A file's text, or null for one that is binary, too large or unreadable.</summary>
    private static async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > LargeFile) return null;
            var loaded = await TextFile.ReadAsync(path, false, ct).ConfigureAwait(false);
            return loaded.Kind == FileKind.Text ? loaded.Text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Every match of <paramref name="regex"/> in a text, with its line, column and a preview of its line.</summary>
    public static List<TextMatch> Find(string text, Regex regex, CancellationToken ct = default)
    {
        var result = new List<TextMatch>();
        var line = 0;
        var lineStart = 0;
        var scanned = 0;
        for (var m = regex.Match(text); m.Success; m = m.NextMatch())
        {
            ct.ThrowIfCancellationRequested();
            if (m.Length == 0) continue;
            // Count the lines between the last match and this one (CRLF and LF alike: '\n' ends a line).
            for (var i = scanned; i < m.Index; i++)
            {
                if (text[i] != '\n') continue;
                line++;
                lineStart = i + 1;
            }

            scanned = m.Index;
            var lineEnd = text.IndexOf('\n', m.Index);
            if (lineEnd < 0) lineEnd = text.Length;
            var column = m.Index - lineStart;
            // The preview: the line, cut to show the match with some of what comes before it.
            var from = Math.Max(lineStart, m.Index - 30);
            var to = Math.Min(lineEnd, from + PreviewWidth);
            var preview = text[from..to].TrimEnd('\r').Replace('\t', ' ');
            result.Add(new TextMatch(line, column, Math.Min(m.Length, Math.Max(1, lineEnd - m.Index)), m.Index, preview, m.Index - from));
            if (result.Count >= MatchCap) break;
        }

        return result;
    }

    /// <summary>
    /// Replaces matches in a file on disk (re-found in the file's current text, so a file changed since the search is
    /// still replaced correctly); returns how many were replaced. <paramref name="only"/> limits it to those offsets.
    /// </summary>
    public static async Task<int> ReplaceInFileAsync(string path, ProjectQuery query, string replacement, IReadOnlySet<int>? only, CancellationToken ct)
    {
        var (regex, _) = query.Compile();
        if (regex is null) return 0;
        var loaded = await TextFile.ReadAsync(path, true, ct).ConfigureAwait(false);
        if (loaded.Kind != FileKind.Text) return 0;
        var (text, count) = Replace(loaded.Text, regex, query.Regex, replacement, only);
        if (count == 0) return 0;
        await TextFile.WriteAsync(path, text, loaded.Encoding, ct).ConfigureAwait(false);
        return count;
    }

    /// <summary>The text with its matches replaced (groups filled in for a regular expression), and how many were.</summary>
    public static (string Text, int Count) Replace(string text, Regex regex, bool groups, string replacement, IReadOnlySet<int>? only = null)
    {
        var builder = new StringBuilder(text.Length);
        var at = 0;
        var count = 0;
        for (var m = regex.Match(text); m.Success; m = m.NextMatch())
        {
            if (m.Length == 0 || (only is not null && !only.Contains(m.Index))) continue;
            builder.Append(text, at, m.Index - at);
            builder.Append(groups ? m.Result(replacement) : replacement);
            at = m.Index + m.Length;
            count++;
        }

        builder.Append(text, at, text.Length - at);
        return (builder.ToString(), count);
    }
}

/// <summary>
/// VS Code's include/exclude boxes: comma-separated globs (<c>*</c>, <c>**</c>, <c>?</c>, <c>{a,b}</c>, <c>[abc]</c>); a
/// glob without a slash matches in any folder, a folder name matches everything under it.
/// </summary>
public sealed class GlobSet
{
    private readonly List<Regex> _globs;

    private GlobSet(List<Regex> globs) => _globs = globs;

    public bool IsEmpty => _globs.Count == 0;

    public static GlobSet Parse(string text)
    {
        var globs = new List<Regex>();
        foreach (var raw in SplitTopLevel(text))
        {
            var glob = raw.Trim().Replace('\\', '/');
            if (glob.Length == 0) continue;
            if (glob.StartsWith("./", StringComparison.Ordinal)) glob = glob[2..];
            if (!glob.Contains('/')) glob = "**/" + glob;
            globs.Add(new Regex("^" + ToRegex(glob.TrimEnd('/')) + "(/.*)?$", RegexOptions.CultureInvariant | (OperatingSystem.IsLinux() ? 0 : RegexOptions.IgnoreCase))); // not a path
        }

        return new GlobSet(globs);
    }

    /// <summary>Whether a relative path ('/' separators) or any folder above it matches.</summary>
    public bool Matches(string relative) => _globs.Any(g => g.IsMatch(relative));

    // Commas inside braces belong to the brace ("*.{ts,js}").
    private static IEnumerable<string> SplitTopLevel(string text)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}') depth = Math.Max(0, depth - 1);
            else if (text[i] == ',' && depth == 0)
            {
                yield return text[start..i];
                start = i + 1;
            }
        }

        yield return text[start..];
    }

    private static string ToRegex(string glob)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    // "**/" is any number of folders, a trailing "**" anything at all.
                    if (i + 2 < glob.Length && glob[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }

                    break;
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '{':
                    var close = glob.IndexOf('}', i);
                    if (close < 0)
                    {
                        sb.Append(@"\{");
                        break;
                    }

                    sb.Append("(?:").Append(string.Join('|', glob[(i + 1)..close].Split(',').Select(ToRegex))).Append(')');
                    i = close;
                    break;
                case '[':
                    var end = glob.IndexOf(']', i);
                    if (end < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }

                    sb.Append('[').Append(glob[(i + 1)..end].Replace(@"\", @"\\", StringComparison.Ordinal)).Append(']');
                    i = end;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }
}
