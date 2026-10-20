using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wicked.Editor.Features;

/// <summary>
/// One piece of a transform's replacement: literal <paramref name="Text"/> (Group -1), or a capture group, optionally
/// with a case modifier (<c>upcase</c>, <c>downcase</c>, <c>capitalize</c>, <c>pascalcase</c>, <c>camelcase</c>) or
/// the conditional forms <c>+if</c>, <c>?if:else</c>, <c>-else</c>.
/// </summary>
public sealed record SnippetFormat(int Group, string? Modifier, string? IfSet, string? IfNot, string? Text);

/// <summary>
/// <c>${1/regex/format/flags}</c>: rewrites a stop's text or a variable's value. The regex is .NET's, with the flags
/// <c>g</c> (all matches, else the first), <c>i</c>, <c>m</c> and <c>s</c>.
/// </summary>
public sealed class SnippetTransform
{
    private readonly Regex _regex;
    private readonly ImmutableArray<SnippetFormat> _format;
    private readonly bool _global;

    private SnippetTransform(Regex regex, ImmutableArray<SnippetFormat> format, bool global)
    {
        _regex = regex;
        _format = format;
        _global = global;
    }

    /// <summary>Null when the regex does not compile.</summary>
    public static SnippetTransform? Create(string pattern, ImmutableArray<SnippetFormat> format, string flags)
    {
        var options = RegexOptions.CultureInvariant;
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        if (flags.Contains('s')) options |= RegexOptions.Singleline;
        try
        {
            return new SnippetTransform(new Regex(pattern, options, TimeSpan.FromSeconds(1)), format, flags.Contains('g'));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public string Apply(string input)
    {
        string Replace(Match m)
        {
            var sb = new StringBuilder();
            foreach (var part in _format) Append(sb, part, m);
            return sb.ToString();
        }

        try
        {
            return _global ? _regex.Replace(input, Replace) : _regex.Replace(input, Replace, 1);
        }
        catch (RegexMatchTimeoutException)
        {
            return input;
        }
    }

    private static void Append(StringBuilder sb, SnippetFormat part, Match m)
    {
        if (part.Group < 0)
        {
            sb.Append(part.Text);
            return;
        }

        var value = part.Group < m.Groups.Count && m.Groups[part.Group].Success ? m.Groups[part.Group].Value : "";
        if (part.IfSet is not null && part.IfNot is null && part.Modifier is null)
        {
            if (value.Length > 0) sb.Append(part.IfSet);
            return;
        }

        if (part.IfSet is not null)
        {
            sb.Append(value.Length > 0 ? part.IfSet : part.IfNot);
            return;
        }

        if (part.IfNot is not null)
        {
            sb.Append(value.Length > 0 ? value : part.IfNot);
            return;
        }

        sb.Append(part.Modifier switch
        {
            "upcase" => value.ToUpperInvariant(),
            "downcase" => value.ToLowerInvariant(),
            "capitalize" => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
            "pascalcase" => Words(value, camel: false),
            "camelcase" => Words(value, camel: true),
            _ => value,
        });
    }

    /// <summary>PascalCase / camelCase from words split at non-alphanumerics and lower-to-upper changes.</summary>
    private static string Words(string value, bool camel)
    {
        var words = Regex.Split(value, @"[^\p{L}\p{N}]+|(?<=\p{Ll})(?=\p{Lu})").Where(w => w.Length > 0).ToArray();
        var sb = new StringBuilder();
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            var first = camel && i == 0 ? char.ToLowerInvariant(w[0]) : char.ToUpperInvariant(w[0]);
            sb.Append(first).Append(w.AsSpan(1).ToString().ToLower(CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
