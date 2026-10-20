// Drafts catalog/catalog.json from the editorial inputs in catalog/picks.json and the live Hugging Face Hub:
// repo revision, licence, gated flag, real file names, sizes and sha256s, the multimodal projector, and the attention
// shape read from the first bytes of a GGUF file (header only, never the weights). A person reviews the diff, then runs
// tools/catalog/sign.sh. The scores in picks.json are editorial and are never invented here.
//
//   dotnet run --project tools/catalog/draft -- [--picks catalog/picks.json] [--out catalog/catalog.json]
using System.Text.Json;
using Wicked.Core.Catalog;
using Wicked.Core.Http;
using Wicked.Core.Hub;
using Wicked.Core.Store;

var root = FindRoot();
string Arg(string name, string fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

var picksPath = Arg("--picks", Path.Combine(root, "catalog", "picks.json"));
var outPath = Arg("--out", Path.Combine(root, "catalog", "catalog.json"));
var picks = JsonSerializer.Deserialize<Picks>(await File.ReadAllTextAsync(picksPath), ModelCatalog.JsonOptions) ?? throw new InvalidDataException("empty picks");
var hub = new HubClient(WickedHttp.Shared);

var families = new List<CatalogFamily>();
foreach (var family in picks.Families)
{
    var models = new List<CatalogModel>();
    foreach (var pick in family.Models)
    {
        Console.WriteLine($"{family.Id} / {pick.Id}  <- {pick.Repo}");
        models.Add(await DraftAsync(hub, pick));
    }

    families.Add(new CatalogFamily(family.Id, family.Name, family.Publisher, family.Description, models));
}

var now = DateTimeOffset.UtcNow;
var catalog = new ModelCatalog(ModelCatalog.SupportedSchema, new DateTimeOffset(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero), families);
await File.WriteAllTextAsync(outPath, catalog.ToJson());
Console.WriteLine($"wrote {outPath}: {catalog.Models.Count()} models in {families.Count} families. Next: tools/catalog/sign.sh");
return 0;

static async Task<CatalogModel> DraftAsync(HubClient hub, PickModel pick)
{
    var info = await hub.GetModelAsync(pick.Repo);
    var files = (await hub.ListFilesAsync(pick.Repo, info.Sha)).Where(f => pick.FilePrefix is null || f.Name.StartsWith(pick.FilePrefix, StringComparison.Ordinal)).ToList();
    var variants = GgufNames.Variants(files);

    var tiers = new List<QuantTier>();
    foreach (var want in pick.Quants)
    {
        var dynamic = want.StartsWith("UD-", StringComparison.Ordinal);
        var quant = dynamic ? want[3..] : want;
        var variant = variants.FirstOrDefault(v => v.Quant == quant && v.Dynamic == dynamic)
                      ?? throw new InvalidDataException($"{pick.Repo} has no {want}");
        tiers.Add(new QuantTier(quant, [.. variant.Files.Select(f => new CatalogFile(f.Path, f.Size, f.LfsSha256))], QuantQuality.Estimate(quant)));
    }

    CatalogFile? mmproj = null;
    if (pick.Mmproj is { } pattern)
    {
        var projector = GgufNames.Projectors(files).FirstOrDefault(f => f.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"{pick.Repo} has no projector matching '{pattern}'");
        mmproj = new CatalogFile(projector.Path, projector.Size, projector.LfsSha256);
    }

    var smallest = tiers.OrderBy(t => t.Size).First();
    var shape = await ReadShapeAsync(hub, pick.Repo, info.Sha, smallest.Files[0].Path) ?? throw new InvalidDataException($"cannot read the shape of {pick.Repo}");
    if (pick.Shape is { } o)
    {
        shape = shape with
        {
            Layers = o.Layers ?? shape.Layers,
            FullLayers = o.FullLayers ?? shape.FullLayers,
            WindowLayers = o.WindowLayers ?? shape.WindowLayers,
            Window = o.Window ?? shape.Window,
            KvHeads = o.KvHeads ?? shape.KvHeads,
            KeyLength = o.KeyLength ?? shape.KeyLength,
            ValueLength = o.ValueLength ?? shape.ValueLength,
            WindowKeyLength = o.WindowKeyLength ?? shape.WindowKeyLength,
            WindowValueLength = o.WindowValueLength ?? shape.WindowValueLength,
        };
    }

    var license = pick.LicenseId ?? info.License ?? throw new InvalidDataException($"{pick.Repo} states no licence; set licenseId in picks.json");
    return new CatalogModel(
        pick.Id, pick.Name, pick.Repo, info.Sha, license, pick.LicenseUrl, info.Gated != Gating.None,
        pick.Capabilities, (int)(pick.ContextLength ?? info.Gguf?.ContextLength ?? throw new InvalidDataException($"no context length for {pick.Repo}")),
        pick.Parameters, pick.Released, pick.Scores, pick.Supersedes ?? [], shape, tiers, mmproj);
}

static async Task<ModelShape?> ReadShapeAsync(HubClient hub, string repo, string revision, string file)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, hub.ResolveUrl(repo, revision, file));
    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 32_000_000);
    using var response = await WickedHttp.Shared.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    await using var stream = await response.Content.ReadAsStreamAsync();
    var header = await GgufReader.ReadHeaderAsync(stream);
    return ModelShape.From(GgufInfo.From(header));
}

static string FindRoot()
{
    for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) return dir.FullName;
    }

    throw new InvalidOperationException("Run this from inside the repository");
}

internal sealed record Picks(IReadOnlyList<PickFamily> Families);

internal sealed record PickFamily(string Id, string Name, string Publisher, string? Description, IReadOnlyList<PickModel> Models);

internal sealed record PickModel(
    string Id,
    string Name,
    string Repo,
    string[] Quants,
    IReadOnlyList<Capability> Capabilities,
    ParameterCount Parameters,
    DateOnly Released,
    RoleScores Scores,
    IReadOnlyList<string>? Supersedes = null,
    string? FilePrefix = null,
    string? Mmproj = null,
    string? LicenseId = null,
    string? LicenseUrl = null,
    long? ContextLength = null,
    ShapeOverride? Shape = null,
    string? Basis = null);

internal sealed record ShapeOverride(
    int? Layers = null,
    int? FullLayers = null,
    int? WindowLayers = null,
    int? Window = null,
    double? KvHeads = null,
    int? KeyLength = null,
    int? ValueLength = null,
    int? WindowKeyLength = null,
    int? WindowValueLength = null);
