using System.Text.RegularExpressions;
using Wicked.Core.Catalog;

namespace Wicked.Tests;

/// <summary>Invariants of the shipped catalog: it is the data the app trusts, so a bad refresh fails here and not on a user's machine.</summary>
public sealed partial class CatalogContentTests
{
    private static ModelCatalog Shipped() => ModelCatalog.Parse(CatalogService.ReadEmbedded().Json);

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*/[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepoName();

    [Fact]
    public void ParsesAndHasEightToTwelveFamilies()
    {
        var catalog = Shipped();

        Assert.Equal(ModelCatalog.SupportedSchema, catalog.Schema);
        Assert.InRange(catalog.Families.Count, 8, 12);
        Assert.True(catalog.Models.Count() >= 15);
    }

    [Fact]
    public void IdsAreUniqueAndSupersedesPointAtRealModels()
    {
        var catalog = Shipped();
        var ids = catalog.Models.Select(m => m.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(catalog.Families.Count, catalog.Families.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var model in catalog.Models) Assert.All(model.Supersedes, id => Assert.Contains(id, ids));
    }

    [Fact]
    public void EveryModelHasRealFilesWithHashesAndShapes()
    {
        foreach (var model in Shipped().Models)
        {
            Assert.Matches(RepoName(), model.Repo);
            Assert.Matches("^[0-9a-f]{40}$", model.Revision);
            Assert.NotEmpty(model.LicenseId);
            Assert.NotEmpty(model.Capabilities);
            Assert.True(model.ContextLength >= 2048, model.Id);
            Assert.True(model.Parameters.Total > 0, model.Id);
            Assert.NotEmpty(model.Tiers);
            Assert.Equal(model.Tiers.Count, model.Tiers.Select(t => t.Quant).Distinct(StringComparer.Ordinal).Count());
            foreach (var tier in model.Tiers)
            {
                Assert.InRange(tier.Quality, 1, 100);
                Assert.NotEmpty(tier.Files);
                foreach (var file in tier.Files)
                {
                    Assert.EndsWith(".gguf", file.Path, StringComparison.Ordinal);
                    Assert.True(file.Size > 0, $"{model.Id} {file.Path}");
                    Assert.Matches(Sha256(), file.Sha256 ?? "");
                }
            }

            Assert.True(model.Shape.Layers > 0 && model.Shape.KvHeads > 0 && model.Shape.KeyLength > 0 && model.Shape.ValueLength > 0, model.Id);
            Assert.True(model.Shape.FullLayers + model.Shape.WindowLayers <= model.Shape.Layers, model.Id);
            if (model.Has(Capability.Vision)) Assert.NotNull(model.Mmproj);
        }
    }

    [Fact]
    public void ScoresMatchWhatEachModelCanDo()
    {
        foreach (var model in Shipped().Models)
        {
            if (model.Scores.Fim is not null) Assert.True(model.Has(Capability.Fim), model.Id);
            if (model.Has(Capability.Fim) && !model.Has(Capability.Chat)) Assert.Null(model.Scores.Everyday);
            foreach (var role in Enum.GetValues<Role>())
            {
                if (model.Scores.For(role) is { } score) Assert.InRange(score, 1, 100);
            }
        }
    }

    [Fact]
    public void IsReadyForTheDefaultRoles()
    {
        var models = Shipped().Models.ToList();

        Assert.Contains(models, m => m.Scores.Everyday is not null);
        Assert.Contains(models, m => m.Scores.Coding is not null);
        Assert.Contains(models, m => m.Scores.Fast is not null);
        Assert.Contains(models, m => m.Has(Capability.Fim));
        Assert.Contains(models, m => m.Has(Capability.Embeddings));
    }

    [Fact]
    public void RoundTripsThroughItsOwnWriter()
    {
        var (json, _) = CatalogService.ReadEmbedded();
        var catalog = ModelCatalog.Parse(json);

        Assert.Equal(System.Text.Encoding.UTF8.GetString(json), catalog.ToJson());
    }
}
