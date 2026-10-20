using Wicked.Core.Catalog;

namespace Wicked.Tests;

/// <summary>The shipped catalog against planted machines: not exact picks (the data will be refreshed) but the promises the screen makes.</summary>
public sealed class RealCatalogRecommendationTests
{
    private static readonly ModelCatalog Catalog = ModelCatalog.Parse(CatalogService.ReadEmbedded().Json);

    [Theory]
    [InlineData(8.0, 16L)]
    [InlineData(12.0, 32L)]
    [InlineData(16.0, 32L)]
    [InlineData(24.0, 64L)]
    [InlineData(null, 32L)]
    [InlineData(null, 8L)]
    public void EveryMachineGetsTwoOrThreeCardsThatReallyFit(double? vram, long ram)
    {
        var hardware = CatalogTestData.Machine(vram, ram);
        var budget = Recommender.BudgetFor(hardware);

        var picks = Recommender.Recommend(hardware, Catalog);

        Assert.InRange(picks.Count, 2, 3);
        Assert.Equal(picks.Count, picks.Select(p => p.Model.Id).Distinct(StringComparer.Ordinal).Count());
        foreach (var pick in picks)
        {
            Assert.NotEmpty(pick.Reason);
            Assert.NotNull(Catalog.Find(pick.Model.Id));
            Assert.Contains(pick.Tier, pick.Model.Tiers);
            var fit = FitCalculator.Evaluate(pick.Tier.Size, pick.Model.Shape, pick.Context, budget, pick.Model.ContextLength);
            Assert.NotEqual(FitLevel.WontFit, fit.Level);
            if (pick.Kind != PickKind.Big && vram is not null) Assert.Equal(FitLevel.FitsInVram, fit.Level);
            if (pick.Companion is { } partner) Assert.Contains(Capability.Fim, partner.Model.Capabilities);
        }
    }

    [Fact]
    public void ABiggerCardNeverGetsAWeakerEverydayModel()
    {
        int Score(double vram) => Recommender.Recommend(CatalogTestData.Machine(vram, 64), Catalog).First(p => p.Kind == PickKind.Everyday).Model.Scores.Everyday!.Value;

        Assert.True(Score(8) <= Score(12));
        Assert.True(Score(12) <= Score(16));
        Assert.True(Score(16) <= Score(24));
    }
}
