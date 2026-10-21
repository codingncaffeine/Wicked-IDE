using System.Collections.Immutable;

namespace Wicked.Core.Git;

/// <summary>What to keep from a conflict.</summary>
public enum ConflictChoice
{
    /// <summary>The side being merged into (HEAD, "ours").</summary>
    Current,

    /// <summary>The side coming in ("theirs").</summary>
    Incoming,

    /// <summary>Both, current first.</summary>
    Both,

    /// <summary>Both, incoming first.</summary>
    BothIncomingFirst,

    /// <summary>The common ancestor (only when the file was written with diff3 markers).</summary>
    Base,
}

/// <summary>
/// One conflicted region of a file: the line of each marker (0-based, in the text it was parsed from). The current side is
/// the lines between <see cref="StartLine"/> and <see cref="BaseLine"/> or <see cref="SeparatorLine"/>; the incoming side is
/// the lines between <see cref="SeparatorLine"/> and <see cref="EndLine"/>.
/// </summary>
public sealed record ConflictRegion(int StartLine, int? BaseLine, int SeparatorLine, int EndLine, string CurrentLabel, string IncomingLabel, string? BaseLabel)
{
    public int CurrentFirst => StartLine + 1;

    public int CurrentEnd => BaseLine ?? SeparatorLine;

    public int BaseFirst => (BaseLine ?? SeparatorLine) + 1;

    public int IncomingFirst => SeparatorLine + 1;

    public bool HasBase => BaseLine is not null;
}

/// <summary>Finds the conflict markers git leaves in a file and resolves them.</summary>
public static class ConflictParser
{
    private const string Start = "<<<<<<<";
    private const string Base = "|||||||";
    private const string Separator = "=======";
    private const string End = ">>>>>>>";

    /// <summary>Every region, in order. A marker without its partners (a stray "=======" under a heading) is not a conflict and is skipped.</summary>
    public static ImmutableArray<ConflictRegion> Parse(IReadOnlyList<string> lines)
    {
        var regions = ImmutableArray.CreateBuilder<ConflictRegion>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsMarker(lines[i], Start)) continue;
            int? baseLine = null;
            int? separator = null;
            var end = -1;
            for (var j = i + 1; j < lines.Count; j++)
            {
                var line = lines[j];
                if (IsMarker(line, Start))
                {
                    break; // a new region begins before this one closed: this one was not a conflict
                }

                if (separator is null && baseLine is null && IsMarker(line, Base)) baseLine = j;
                else if (separator is null && IsMarker(line, Separator)) separator = j;
                else if (separator is not null && IsMarker(line, End))
                {
                    end = j;
                    break;
                }
            }

            if (separator is null || end < 0) continue;
            regions.Add(new ConflictRegion(i, baseLine, separator.Value, end, Label(lines[i], Start), Label(lines[end], End), baseLine is { } b ? Label(lines[b], Base) : null));
            i = end;
        }

        return regions.ToImmutable();
    }

    private static bool IsMarker(string line, string marker) =>
        line.StartsWith(marker, StringComparison.Ordinal) && (line.Length == marker.Length || line[marker.Length] is ' ' or '\r');

    private static string Label(string line, string marker) => line.Length > marker.Length ? line[(marker.Length + 1)..].TrimEnd('\r').Trim() : "";

    /// <summary>The lines that replace the whole region (markers included) when the user takes <paramref name="choice"/>.</summary>
    public static IReadOnlyList<string> Resolve(IReadOnlyList<string> lines, ConflictRegion region, ConflictChoice choice)
    {
        List<string> Take(int from, int to) => [.. lines.Skip(from).Take(to - from)];
        var current = Take(region.CurrentFirst, region.CurrentEnd);
        var incoming = Take(region.IncomingFirst, region.EndLine);
        return choice switch
        {
            ConflictChoice.Current => current,
            ConflictChoice.Incoming => incoming,
            ConflictChoice.Both => [.. current, .. incoming],
            ConflictChoice.BothIncomingFirst => [.. incoming, .. current],
            _ => region.HasBase ? Take(region.BaseFirst, region.SeparatorLine) : [],
        };
    }

    /// <summary>The whole text with one region resolved; line breaks are the ones the text uses.</summary>
    public static string Apply(string text, ConflictRegion region, ConflictChoice choice)
    {
        var lineBreak = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToList();
        var replacement = Resolve(lines, region, choice);
        lines.RemoveRange(region.StartLine, region.EndLine - region.StartLine + 1);
        lines.InsertRange(region.StartLine, replacement);
        return string.Join(lineBreak, lines);
    }

    /// <summary>Every region taken the same way (Accept All Current / Incoming / Both).</summary>
    public static string ApplyAll(string text, ConflictChoice choice)
    {
        var result = text;
        // Each resolution moves the lines below it, so the text is parsed again after each.
        while (Parse(TextLines.Split(result)) is { IsEmpty: false } regions) result = Apply(result, regions[0], choice);
        return result;
    }
}
