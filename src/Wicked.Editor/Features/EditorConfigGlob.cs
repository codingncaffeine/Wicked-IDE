using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wicked.Editor.Features;

/// <summary>
/// An EditorConfig section glob, compiled to a regular expression: <c>*</c> (anything but '/'), <c>**</c> (anything),
/// <c>?</c>, <c>[seq]</c> and <c>[!seq]</c>, <c>{a,b,c}</c> alternatives (nesting allowed), <c>{1..3}</c> integer ranges
/// (negative bounds allowed), and backslash escapes. A glob with no '/' outside square brackets matches in any directory
/// below the .editorconfig; one with a '/' is relative to the .editorconfig's own directory. An unclosed
/// <c>[</c> or <c>{</c>, a <c>{single}</c> with no comma, and a class that holds '/' are literal text.
/// </summary>
public sealed class EditorConfigGlob
{
    private readonly Regex _regex;
    private readonly List<(string Group, long Low, long High)> _ranges = [];

    private EditorConfigGlob(string glob)
    {
        Glob = glob;
        var anchored = HasSeparator(glob);
        var body = anchored && glob.StartsWith('/') ? glob[1..] : glob;
        var pattern = Convert(body);
        _regex = new Regex(anchored ? $"^{pattern}$" : $"^(?:.*/)?{pattern}$", RegexOptions.CultureInvariant | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }

    public string Glob { get; }

    /// <summary>The glob compiled, or null when it cannot be (the section then matches nothing).</summary>
    public static EditorConfigGlob? Create(string glob)
    {
        try
        {
            return new EditorConfigGlob(glob);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Whether a path relative to the .editorconfig's directory, written with '/', matches.</summary>
    public bool IsMatch(string relativePath)
    {
        try
        {
            var m = _regex.Match(relativePath);
            if (!m.Success) return false;
            foreach (var (group, low, high) in _ranges)
            {
                var g = m.Groups[group];
                if (!g.Success) continue;
                if (!long.TryParse(g.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) || n < low || n > high) return false;
            }

            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool HasSeparator(string glob)
    {
        var inClass = false;
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '\\') i++;
            else if (c == '[' && !inClass && ClassEnd(glob, i) > 0) inClass = true;
            else if (c == ']' && inClass) inClass = false;
            else if (c == '/' && !inClass) return true;
        }

        return false;
    }

    /// <summary>The index of the ']' closing the class opened at <paramref name="open"/>, or -1 (also -1 when the class would hold a '/').</summary>
    private static int ClassEnd(string s, int open)
    {
        for (var i = open + 1; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == '/') return -1;
            else if (s[i] == ']' && i > open + 1) return i;
        }

        return -1;
    }

    private static int BraceEnd(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '\\') i++;
            else if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }

    private string Convert(string s)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            switch (c)
            {
                case '\\':
                    if (i + 1 < s.Length) sb.Append(Regex.Escape(s[++i].ToString()));
                    else sb.Append(@"\\");
                    break;
                case '*':
                    if (i + 1 < s.Length && s[i + 1] == '*')
                    {
                        sb.Append(".*");
                        i++;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }

                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                {
                    var end = ClassEnd(s, i);
                    if (end < 0)
                    {
                        sb.Append(@"\[");
                        break;
                    }

                    sb.Append('[');
                    var j = i + 1;
                    if (s[j] == '!')
                    {
                        sb.Append('^');
                        j++;
                    }

                    for (; j < end; j++)
                    {
                        if (s[j] == '\\' && j + 1 < end) sb.Append('\\').Append(s[++j]);
                        else if (s[j] is '[' or '^' or '\\') sb.Append('\\').Append(s[j]);
                        else sb.Append(s[j]);
                    }

                    sb.Append(']');
                    i = end;
                    break;
                }

                case '{':
                {
                    var end = BraceEnd(s, i);
                    if (end < 0)
                    {
                        sb.Append(@"\{");
                        break;
                    }

                    var inner = s[(i + 1)..end];
                    var range = Regex.Match(inner, @"^(-?\d+)\.\.(-?\d+)$");
                    if (range.Success)
                    {
                        var a = long.Parse(range.Groups[1].Value, CultureInfo.InvariantCulture);
                        var b = long.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture);
                        var name = $"r{_ranges.Count}";
                        _ranges.Add((name, Math.Min(a, b), Math.Max(a, b)));
                        sb.Append("(?<").Append(name).Append(">-?[0-9]+)");
                        i = end;
                        break;
                    }

                    var parts = SplitTopLevel(inner);
                    if (parts.Count < 2)
                    {
                        // {single}: braces are literal.
                        sb.Append(@"\{").Append(Convert(inner)).Append(@"\}");
                    }
                    else
                    {
                        sb.Append("(?:").Append(string.Join('|', parts.Select(Convert))).Append(')');
                    }

                    i = end;
                    break;
                }

                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }

    private static List<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var depth = 0;
        var inClass = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\') i++;
            else if (c == '[' && !inClass && ClassEnd(s, i) > 0) inClass = true;
            else if (c == ']' && inClass) inClass = false;
            else if (inClass) continue;
            else if (c == '{') depth++;
            else if (c == '}') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }

        parts.Add(s[start..]);
        return parts;
    }
}
