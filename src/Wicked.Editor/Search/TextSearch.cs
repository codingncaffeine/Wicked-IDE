using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Editor.Search;

/// <summary>What to look for: the text and VS Code's three toggles.</summary>
public sealed record SearchQuery(string Text, bool MatchCase = false, bool WholeWord = false, bool Regex = false);

/// <summary>A search's result for one version of the text: every match (start, length), sorted, or why the query is bad.</summary>
public sealed record SearchResult(long Version, ImmutableArray<(int Start, int Length)> Matches, bool Capped, string? Error)
{
    public static readonly SearchResult None = new(-1, [], false, null);
}

/// <summary>
/// Finding in a document, off the UI thread: literal or regular-expression, case-sensitive or not, whole words or not.
/// Results stop at <see cref="Cap"/> matches, as VS Code's do. A regular expression has a time limit, so a pathological
/// pattern ends with an error instead of a stuck worker.
/// </summary>
public static class TextSearch
{
    public const int Cap = 19_999;

    private static readonly TimeSpan RegexLimit = TimeSpan.FromSeconds(2);

    public static SearchResult Find(Rope text, long version, SearchQuery query, CancellationToken ct = default)
    {
        if (query.Text.Length == 0) return new SearchResult(version, [], false, null);
        var all = text.ToString();
        var found = ImmutableArray.CreateBuilder<(int, int)>();
        try
        {
            if (query.Regex || query.WholeWord)
            {
                var pattern = query.Regex ? query.Text : System.Text.RegularExpressions.Regex.Escape(query.Text);
                if (query.WholeWord) pattern = $@"(?<![\p{{L}}\p{{N}}_]){pattern}(?![\p{{L}}\p{{N}}_])";
                var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (query.MatchCase ? 0 : RegexOptions.IgnoreCase);
                var regex = new Regex(pattern, options, RegexLimit);
                for (var m = regex.Match(all); m.Success; m = m.Length == 0 ? regex.Match(all, m.Index + 1 <= all.Length ? m.Index + 1 : all.Length) : m.NextMatch())
                {
                    ct.ThrowIfCancellationRequested();
                    if (m.Length == 0 && m.Index >= all.Length) break;
                    if (m.Length > 0) found.Add((m.Index, m.Length));
                    if (found.Count >= Cap) return new SearchResult(version, found.ToImmutable(), true, null);
                }
            }
            else
            {
                var comparison = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
                for (var at = all.IndexOf(query.Text, comparison); at >= 0; at = all.IndexOf(query.Text, at + query.Text.Length, comparison))
                {
                    ct.ThrowIfCancellationRequested();
                    found.Add((at, query.Text.Length));
                    if (found.Count >= Cap) return new SearchResult(version, found.ToImmutable(), true, null);
                }
            }
        }
        catch (ArgumentException ex)
        {
            return new SearchResult(version, [], false, ex.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return new SearchResult(version, found.ToImmutable(), true, "The regular expression took too long");
        }

        return new SearchResult(version, found.ToImmutable(), false, null);
    }

    /// <summary>The first match at or after <paramref name="offset"/> (wrapping), or -1.</summary>
    public static int NextIndex(ImmutableArray<(int Start, int Length)> matches, int offset)
    {
        if (matches.IsEmpty) return -1;
        var lo = 0;
        var hi = matches.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (matches[mid].Start < offset) lo = mid + 1;
            else hi = mid;
        }

        return lo < matches.Length ? lo : 0;
    }

    /// <summary>The last match before <paramref name="offset"/> (wrapping), or -1.</summary>
    public static int PreviousIndex(ImmutableArray<(int Start, int Length)> matches, int offset)
    {
        if (matches.IsEmpty) return -1;
        var next = NextIndex(matches, offset);
        var at = next == 0 && matches[0].Start >= offset ? matches.Length - 1 : next - 1;
        return at < 0 ? matches.Length - 1 : at;
    }

    /// <summary>
    /// The text a match is replaced with: the replacement as typed, or, for a regular expression, with its groups ($1,
    /// ${name}) filled in from that match.
    /// </summary>
    public static string Replacement(Rope text, (int Start, int Length) match, SearchQuery query, string replace)
    {
        if (!query.Regex) return replace;
        var options = RegexOptions.Multiline | RegexOptions.CultureInvariant | (query.MatchCase ? 0 : RegexOptions.IgnoreCase);
        var regex = new Regex(query.Text, options, RegexLimit);
        var original = text.Substring(match.Start, match.Length);
        var m = regex.Match(original);
        return m.Success ? m.Result(replace) : replace;
    }

    /// <summary>Replace All as one change set (one undo step); the carets go to the end of the last replacement.</summary>
    public static bool ReplaceAll(Document doc, SearchResult result, SearchQuery query, string replace)
    {
        if (result.Matches.IsEmpty || result.Version != doc.Version) return false;
        var changes = result.Matches.Select(m => new TextChange(m.Start, m.Length, Replacement(doc.Text, m, query, replace))).ToList();
        var shift = changes.Sum(c => c.Delta);
        var last = changes[^1];
        // The last replacement starts where it did, moved by every change before it.
        return doc.Apply(changes, [Selection.Caret(last.Offset + (shift - last.Delta) + last.Inserted.Length)]);
    }
}
