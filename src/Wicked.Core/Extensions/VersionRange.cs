using System.Globalization;

namespace Wicked.Core.Extensions;

/// <summary>
/// The range of host versions an extension asks for, in npm's short forms: <c>^1.0</c> (1.0 up to, not including, 2.0),
/// <c>~1.2</c> (1.2 up to 1.3), <c>&gt;=1.1</c>, <c>1.0</c> or <c>=1.0</c> (that version), <c>*</c> (any). Several comparators
/// separated by spaces must all hold.
/// </summary>
public sealed class VersionRange
{
    private readonly List<(string Op, Version Version)> _terms = [];

    private VersionRange()
    {
    }

    public string Text { get; private init; } = "*";

    public static bool TryParse(string? text, out VersionRange range)
    {
        range = new VersionRange { Text = text?.Trim() ?? "" };
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "*") continue;
            var op = part.StartsWith(">=", StringComparison.Ordinal) ? ">=" : part.StartsWith("<=", StringComparison.Ordinal) ? "<="
                : part[0] is '^' or '~' or '>' or '<' or '=' ? part[..1] : "";
            var number = part[op.Length..].TrimStart('v');
            if (op.Length == 0) op = "=";
            if (!TryVersion(number, out var version)) return false;
            switch (op)
            {
                case "^":
                    range._terms.Add((">=", version));
                    range._terms.Add(("<", new Version(version.Major + 1, 0)));
                    break;
                case "~":
                    range._terms.Add((">=", version));
                    range._terms.Add(("<", new Version(version.Major, version.Minor + 1)));
                    break;
                default:
                    range._terms.Add((op, version));
                    break;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="version"/> is inside the range (only major and minor count: patches never change an API).</summary>
    public bool Contains(Version version)
    {
        var v = new Version(version.Major, Math.Max(0, version.Minor));
        foreach (var (op, bound) in _terms)
        {
            var b = new Version(bound.Major, Math.Max(0, bound.Minor));
            var c = v.CompareTo(b);
            var ok = op switch { ">=" => c >= 0, "<=" => c <= 0, ">" => c > 0, "<" => c < 0, _ => c == 0 };
            if (!ok) return false;
        }

        return true;
    }

    private static bool TryVersion(string text, out Version version)
    {
        version = new Version(0, 0);
        var pieces = text.Split('.');
        if (pieces.Length is < 1 or > 3) return false;
        var numbers = new int[3];
        for (var i = 0; i < pieces.Length; i++)
        {
            if (pieces[i] is "x" or "X" or "*") break;
            if (!int.TryParse(pieces[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return false;
        }

        version = new Version(numbers[0], numbers[1]);
        return true;
    }
}
