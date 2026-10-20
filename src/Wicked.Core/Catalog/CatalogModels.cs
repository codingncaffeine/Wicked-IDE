using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wicked.Core.Catalog;

/// <summary>What a model can do. Serialised as lower-case words.</summary>
public enum Capability
{
    Chat,
    Tools,
    Reasoning,
    Vision,
    Fim,
    Embeddings,
}

/// <summary>The jobs the recommender ranks models for (D25). <c>Fim</c> is fill-in-the-middle code completion.</summary>
public enum Role
{
    Everyday,
    Coding,
    Fast,
    Fim,
}

/// <summary>One file of a quant. <c>Sha256</c> is the file's real hash from the Hub's tree (empty only if the Hub did not give one).</summary>
public sealed record CatalogFile(string Path, long Size, string? Sha256);

/// <summary>
/// A downloadable quantisation: the files to fetch (several for a split model) and a 0-100 <c>Quality</c> estimate of how
/// close it is to the full-precision weights (Q8_0 about 99, Q4_K_M about 90), used to pick the best tier that fits.
/// </summary>
public sealed record QuantTier(string Quant, IReadOnlyList<CatalogFile> Files, int Quality)
{
    [JsonIgnore]
    public long Size => Files.Sum(f => f.Size);
}

/// <summary>Parameter counts in billions. <c>Active</c> is set for mixture-of-experts models (the part used per token).</summary>
public sealed record ParameterCount(double Total, double? Active = null)
{
    [JsonIgnore]
    public bool IsMoe => Active is { } a && a < Total * 0.6;
}

/// <summary>Per-role quality scores, 0-100, null when the model is not meant for that role. These are editorial judgements from public benchmarks (see the notes beside the catalog).</summary>
public sealed record RoleScores(int? Everyday = null, int? Coding = null, int? Fast = null, int? Fim = null)
{
    public int? For(Role role) => role switch
    {
        Role.Everyday => Everyday,
        Role.Coding => Coding,
        Role.Fast => Fast,
        _ => Fim,
    };
}

/// <summary>
/// One entry of the catalog (D8, D25). <c>Repo</c> is the Hugging Face repo the files come from, <c>Revision</c> the commit it
/// was checked at, <c>ContextLength</c> the model's native maximum, <c>Shape</c> what the fit calculator needs, and
/// <c>Supersedes</c> the ids of older models this one replaces (the update notice, D7).
/// </summary>
public sealed record CatalogModel(
    string Id,
    string Name,
    string Repo,
    string Revision,
    string LicenseId,
    string? LicenseUrl,
    bool Gated,
    IReadOnlyList<Capability> Capabilities,
    int ContextLength,
    ParameterCount Parameters,
    DateOnly Released,
    RoleScores Scores,
    IReadOnlyList<string> Supersedes,
    ModelShape Shape,
    IReadOnlyList<QuantTier> Tiers,
    CatalogFile? Mmproj = null)
{
    public bool Has(Capability capability) => Capabilities.Contains(capability);
}

public sealed record CatalogFamily(string Id, string Name, string Publisher, string? Description, IReadOnlyList<CatalogModel> Models);

/// <summary>The whole catalog file. <c>Schema</c> changes only when the shape breaks old readers; <c>Generated</c> orders versions so an older file can never replace a newer one.</summary>
public sealed record ModelCatalog(int Schema, DateTimeOffset Generated, IReadOnlyList<CatalogFamily> Families)
{
    public const int SupportedSchema = 1;

    public IEnumerable<CatalogModel> Models => Families.SelectMany(f => f.Models);

    public CatalogModel? Find(string modelId) => Models.FirstOrDefault(m => m.Id == modelId);

    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public static ModelCatalog Parse(ReadOnlySpan<byte> json) =>
        JsonSerializer.Deserialize<ModelCatalog>(json, JsonOptions) ?? throw new InvalidDataException("The catalog file is empty");

    /// <summary>Canonical text of a catalog: what <c>tools/catalog</c> writes and signs.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions) + "\n";

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };
        return options;
    }
}
