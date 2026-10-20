using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Wicked.Editor.Features;

/// <summary>One entry of a VS Code snippet file.</summary>
/// <param name="Name">The key in the file.</param>
/// <param name="Prefixes">What the user types to get it (a file may give one prefix or a list).</param>
/// <param name="Body">The body; an array body is joined with line feeds.</param>
/// <param name="Description">The description shown in completion.</param>
/// <param name="Scopes">Language ids it applies to; empty for every language.</param>
public sealed record SnippetDefinition(string Name, ImmutableArray<string> Prefixes, string Body, string Description, ImmutableArray<string> Scopes)
{
    public Snippet Parse() => Snippet.Parse(Body);
}

/// <summary>Loading snippet files (VS Code's JSON format: comments and trailing commas allowed) and finding snippets by prefix.</summary>
public static class Snippets
{
    /// <summary>Parses a snippet body; shorthand for <see cref="Snippet.Parse"/>.</summary>
    public static Snippet Parse(string body) => Snippet.Parse(body);

    /// <summary>Parses and expands in one step.</summary>
    public static ExpandedSnippet Expand(string body, SnippetContext? context = null, string lineIndent = "", string indentUnit = "\t") =>
        SnippetExpander.Expand(Snippet.Parse(body), context, lineIndent, indentUnit);

    /// <summary>
    /// Reads a snippet file. Entries that are not objects, or have no body, are skipped; a file that is not JSON gives an
    /// empty list (the caller logs it) rather than an exception.
    /// </summary>
    public static ImmutableArray<SnippetDefinition> LoadJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            var result = ImmutableArray.CreateBuilder<SnippetDefinition>();
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var o = entry.Value;
                if (!o.TryGetProperty("body", out var body) || !TryText(body, "\n", out var text)) continue;
                var prefixes = o.TryGetProperty("prefix", out var p) ? Strings(p) : [];
                var description = o.TryGetProperty("description", out var d) && TryText(d, "\n", out var desc) ? desc : "";
                var scopes = o.TryGetProperty("scope", out var s) && s.ValueKind == JsonValueKind.String
                    ? [.. s.GetString()!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)]
                    : ImmutableArray<string>.Empty;
                result.Add(new SnippetDefinition(entry.Name, prefixes, text, description, scopes));
            }

            return result.ToImmutable();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The snippets that apply to <paramref name="languageId"/> (no scope, or a scope naming it) and whose prefix starts with <paramref name="typed"/>, best matches first.</summary>
    public static IEnumerable<(SnippetDefinition Snippet, string Prefix)> Find(IEnumerable<SnippetDefinition> all, string typed, string? languageId)
    {
        var matches = new List<(SnippetDefinition, string, int)>();
        foreach (var def in all)
        {
            if (!def.Scopes.IsEmpty && (languageId is null || !def.Scopes.Contains(languageId, StringComparer.OrdinalIgnoreCase))) continue;
            foreach (var prefix in def.Prefixes)
            {
                if (!prefix.StartsWith(typed, StringComparison.OrdinalIgnoreCase)) continue;
                // An exact match first, then shorter prefixes.
                matches.Add((def, prefix, prefix.Length == typed.Length ? 0 : 1 + prefix.Length));
            }
        }

        return matches.OrderBy(m => m.Item3).ThenBy(m => m.Item2, StringComparer.OrdinalIgnoreCase).Select(m => (m.Item1, m.Item2));
    }

    private static ImmutableArray<string> Strings(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => [e.GetString()!],
        JsonValueKind.Array => [.. e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)],
        _ => [],
    };

    private static bool TryText(JsonElement e, string separator, out string text)
    {
        text = "";
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                text = e.GetString()!;
                return true;
            case JsonValueKind.Array:
                var sb = new StringBuilder();
                var first = true;
                foreach (var line in e.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.String) continue;
                    if (!first) sb.Append(separator);
                    sb.Append(line.GetString());
                    first = false;
                }

                text = sb.ToString();
                return true;
            default:
                return false;
        }
    }
}
