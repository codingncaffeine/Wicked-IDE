using System.Text.RegularExpressions;

namespace Wicked.Core.Hub;

/// <summary>
/// What a GGUF file name says about itself. <c>Quant</c> is the upper-case quant (<c>Q4_K_M</c>, <c>IQ4_XS</c>, <c>Q8_0</c>,
/// <c>BF16</c>) or null; <c>Dynamic</c> marks Unsloth's <c>UD-</c> dynamic quants (the prefix is not part of the quant);
/// <c>Part</c> and <c>PartCount</c> are the 1-based shard number and total of a <c>-00001-of-00003</c> file;
/// <c>IsMmproj</c> marks a multimodal projector (the router requires the name to start with <c>mmproj</c>); <c>IsSidecar</c>
/// marks the other helper files the router knows (<c>mtp-</c>, <c>eagle3-</c>, <c>dflash-</c>, <c>dspark-</c>, <c>draft-</c>
/// prefixes: speculative-decoding heads that are not models on their own).
/// </summary>
public sealed record GgufName(string? Quant, bool Dynamic, int? Part, int? PartCount, bool IsMmproj, bool IsSidecar = false);

/// <summary>One downloadable choice in a repo: a quant, its files (all shards) and their total size.</summary>
public sealed record GgufVariant(string Quant, bool Dynamic, IReadOnlyList<HubFile> Files, long TotalSize)
{
    public bool IsSharded => Files.Count > 1;
}

public static partial class GgufNames
{
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<ud>UD-)?(?<q>(?:IQ|TQ|Q)\d(?:_[0-9A-Z]+)*|BF16|F16|F32|MXFP4(?:_MOE)?)(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QuantPattern();

    [GeneratedRegex(@"-(?<n>\d{5})-of-(?<m>\d{5})\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShardPattern();

    /// <summary>Parses a repo-relative path; the quant is looked for in the file name first, then in its folders (some repos use <c>Q4_K_M/model-00001-of-00002.gguf</c>).</summary>
    public static GgufName Parse(string path)
    {
        var file = path[(path.LastIndexOf('/') + 1)..]; // not a path: a Hub path
        var shard = ShardPattern().Match(file);
        var (quant, dynamic) = FindQuant(file);
        if (quant is null)
        {
            var segments = path.Split('/'); // not a path
            for (var i = segments.Length - 2; i >= 0 && quant is null; i--) (quant, dynamic) = FindQuant(segments[i]);
        }

        return new GgufName(
            quant,
            dynamic,
            shard.Success ? int.Parse(shard.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
            shard.Success ? int.Parse(shard.Groups["m"].Value, System.Globalization.CultureInfo.InvariantCulture) : null,
            file.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase),
            SidecarPrefixes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly string[] SidecarPrefixes = ["mtp-", "eagle3-", "dflash-", "dspark-", "draft-"];

    private static (string? Quant, bool Dynamic) FindQuant(string text)
    {
        var matches = QuantPattern().Matches(text);
        if (matches.Count == 0) return (null, false);
        var last = matches[^1];
        return (last.Groups["q"].Value.ToUpperInvariant(), last.Groups["ud"].Success);
    }

    /// <summary>Groups a repo's GGUF files (not projectors) into variants, sharded ones together, smallest first.</summary>
    public static IReadOnlyList<GgufVariant> Variants(IEnumerable<HubFile> files)
    {
        var groups = new Dictionary<(string Quant, bool Dynamic, string Folder), List<HubFile>>();
        foreach (var file in files)
        {
            if (file.IsDirectory || !file.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)) continue;
            var name = Parse(file.Path);
            if (name.IsMmproj || name.IsSidecar || name.Quant is null) continue;
            var slash = file.Path.LastIndexOf('/'); // not a path
            var key = (name.Quant, name.Dynamic, name.PartCount is null ? file.Path : slash < 0 ? "" : file.Path[..slash]);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(file);
        }

        return [.. groups
            .Select(g => new GgufVariant(g.Key.Quant, g.Key.Dynamic, [.. g.Value.OrderBy(f => f.Path, StringComparer.Ordinal)], g.Value.Sum(f => f.Size)))
            .OrderBy(v => v.TotalSize)];
    }

    /// <summary>The projector files of a repo, for vision models.</summary>
    public static IReadOnlyList<HubFile> Projectors(IEnumerable<HubFile> files) =>
        [.. files.Where(f => !f.IsDirectory && f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && Parse(f.Path).IsMmproj)];
}
