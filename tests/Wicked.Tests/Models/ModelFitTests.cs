using Wicked.Core.Catalog;
using Wicked.Core.Models;

namespace Wicked.Tests.Models;

/// <summary>The Browse page's default quant and fit badges follow the recommender's own rule.</summary>
public sealed class ModelFitTests
{
    public static TheoryData<double?, long> Machines => new()
    {
        { 8, 32 },
        { 12, 32 },
        { 16, 64 },
        { 24, 64 },
        { null, 32 },
    };

    [Theory]
    [MemberData(nameof(Machines))]
    public void TheDefaultQuantIsTheOneTheRecommenderWouldInstall(double? vram, long ram)
    {
        var machine = CatalogTestData.Machine(vram, ram);
        var catalog = CatalogTestData.Catalog();
        var picks = Recommender.Recommend(machine, catalog).Where(p => p.Kind is PickKind.Everyday or PickKind.Coding).ToList();
        Assert.NotEmpty(picks);
        foreach (var pick in picks)
        {
            Assert.Equal(pick.Tier.Quant, ModelFit.DefaultTier(pick.Model, machine).Quant);
        }
    }

    [Fact]
    public void AModelTooBigForThisPcGetsAWontFitBadgeAtItsSmallestDecentQuant()
    {
        var machine = CatalogTestData.Machine(8, 16);
        var big = CatalogTestData.Catalog().Models.First(m => m.Id == "big");
        var tier = ModelFit.DefaultTier(big, machine);
        Assert.Equal(FitLevel.WontFit, ModelFit.For(big, tier, machine)!.Level);
        Assert.Equal("Q4_K_M", tier.Quant); // the smallest one: if nothing fits, say so at the cheapest
    }

    [Fact]
    public void FitIsUnknownUntilTheHardwareIsKnown()
    {
        var model = CatalogTestData.Catalog().Models.First();
        Assert.Null(ModelFit.For(model, model.Tiers[0], null));
        Assert.NotNull(ModelFit.DefaultTier(model, null)); // a sensible default even before detection
    }

    [Theory]
    [InlineData(FitLevel.FitsInVram, "Fits in video memory")]
    [InlineData(FitLevel.FitsWithOffload, "Fits with CPU offload")]
    [InlineData(FitLevel.WontFit, "Will not fit")]
    public void EveryLevelHasWords(FitLevel level, string words) => Assert.Equal(words, ModelFit.Describe(level));
}
