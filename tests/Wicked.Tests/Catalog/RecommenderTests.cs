using Wicked.Core.Catalog;
using Wicked.Core.Hardware;

namespace Wicked.Tests;

/// <summary>
/// The expectation table was worked out by hand from the formula (weights + cache at the context, times 1.10, against the
/// memory), not copied from the program's output. See <see cref="CatalogTestData"/> for the models; the cache costs 128 KiB per
/// token for the dense ones, so 16k of context is 2 GiB.
/// </summary>
public sealed class RecommenderTests
{
    private static readonly ModelCatalog Catalog = CatalogTestData.Catalog();

    private static string Summary(IReadOnlyList<Pick> picks) => string.Join(" | ", picks.Select(p =>
        $"{string.Join("+", new[] { p.Kind }.Concat(p.AlsoFor))}:{p.Model.Id}:{p.Tier.Quant}:{p.Context}{(p.Companion is { } c ? "+" + c.Model.Id : "")}"));

    [Theory]
    // The budget is the card less what the desktop and the engine keep: the larger of 1.5 GiB or a tenth (8 → 6.5, 12 → 10.5,
    // 16 → 14.4, 24 → 21.6 GiB). Counting the whole card called models "fits" that crashed on a 16 GB card (2026-10-21).
    // 8 GiB: only the 4B fits (Q8, room for 32k); fim-small no longer fits beside it; the tiny model is the quick one.
    [InlineData(8.0, 16L, "Everyday+Coding:small:Q8_0:32768 | Fast:tiny:Q8_0:32768")]
    // 12 GiB: the 14B at Q4 needs 11.55 at 16k, over 10.5: the 4B is the pick, with fim-small; the 14B is the Big pick, 30 of 32 layers on the GPU.
    [InlineData(12.0, 32L, "Everyday+Coding:small:Q8_0:32768+fim-small | Big:mid-new:Q4_K_M:16384")]
    // 16 GiB: the 14B at Q4; fim-small beside it trims the room to 16k; the 32B would be well under 75% on the GPU.
    [InlineData(16.0, 64L, "Everyday+Coding:mid-new:Q4_K_M:16384+fim-small | Fast:tiny:Q8_0:32768")]
    // 24 GiB: the 32B at Q4 needs 22.0, over 21.6: it is the Big pick (31 of 32 layers on the GPU); the 14B at Q8 is the everyday pick and the MoE the coding one.
    [InlineData(24.0, 64L, "Everyday:mid-new:Q8_0:32768 | Coding:coder-moe:Q4_K_M:16384 | Big:big:Q4_K_M:16384")]
    public void PicksForPlantedGpus(double vram, long ram, string expected) =>
        Assert.Equal(expected, Summary(Recommender.Recommend(CatalogTestData.Machine(vram, ram), Catalog)));

    [Fact]
    public void ACpuOnlyBoxGetsTheMixtureOfExpertsModelAndASmallFastOne()
    {
        // 36 GiB of RAM leaves a 25 GiB budget: the MoE at Q4 (20.35 needed) fits with 32k of room; the dense models are over the 9B-active limit.
        var picks = Recommender.Recommend(CatalogTestData.Machine(null, 36), Catalog);

        Assert.Equal("Everyday+Coding:coder-moe:Q4_K_M:32768 | Fast:tiny:Q8_0:32768", Summary(picks));
        Assert.Contains("Runs on your CPU", picks[0].Reason, StringComparison.Ordinal);
        Assert.Contains("completes code", picks[0].Reason, StringComparison.Ordinal);
        Assert.Null(picks[0].Companion);
    }

    [Fact]
    public void AnAppleChipUsesItsUnifiedMemoryOnceWithNoOffload()
    {
        // 24 GiB of GPU memory (a 32 GB Mac) and no extra RAM budget: as the 24 GiB card, minus any offload.
        var picks = Recommender.Recommend(CatalogTestData.Machine(24, 32, GpuVendor.Apple), Catalog);

        Assert.Equal("Everyday+Coding:big:Q4_K_M:16384 | Fast:tiny:Q8_0:32768", Summary(picks));
    }

    [Fact]
    public void AGpuWithoutKnownMemoryCountsAsCpuOnly()
    {
        var hardware = CatalogTestData.Machine(null, 36) with
        {
            Gpus = [new GpuInfo(GpuVendor.Intel, "Integrated", null, null, null, null, null)],
        };

        Assert.Equal("Everyday+Coding:coder-moe:Q4_K_M:32768 | Fast:tiny:Q8_0:32768", Summary(Recommender.Recommend(hardware, Catalog)));
    }

    [Fact]
    public void ABigPickAppearsWhenOffloadKeepsMostLayersOnTheGpu()
    {
        // 20 GiB of VRAM, 18 of them usable: "big" at Q4 needs 22.0, so it offloads; 18 / (22/32) = 26 of 32 layers (81%) stay on the GPU.
        var picks = Recommender.Recommend(CatalogTestData.Machine(20, 64), Catalog);

        var big = picks.Single(p => p.Kind == PickKind.Big);
        Assert.Equal("big", big.Model.Id);
        Assert.Equal("Q4_K_M", big.Tier.Quant);
        Assert.Equal(FitLevel.FitsWithOffload, big.Fit.Level);
        Assert.Equal(26, big.Fit.GpuLayers);
        Assert.Equal("mid-new", picks[0].Model.Id);
        Assert.Contains("26 of 32 layers on the GPU", big.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewerReleaseWinsATieAndThenTheIdDoes()
    {
        var picks = Recommender.Recommend(CatalogTestData.Machine(16, 64), Catalog);
        Assert.Equal("mid-new", picks[0].Model.Id);

        var sameDay = CatalogTestData.Catalog() with
        {
            Families = [CatalogTestData.Catalog().Families[0] with { Models = [.. CatalogTestData.Catalog().Families[0].Models.Select(m => m.Id == "mid-new" ? m with { Released = new DateOnly(2026, 3, 1) } : m)] }],
        };
        Assert.Equal("mid", Recommender.Recommend(CatalogTestData.Machine(16, 64), sameDay)[0].Model.Id);
    }

    [Fact]
    public void ReasonsNameTheGpuAndTheContext()
    {
        var picks = Recommender.Recommend(CatalogTestData.Machine(16, 64), Catalog);

        Assert.Equal("Fits your 16 GB GPU with room for 16k context, leaving room for the code completion model below", picks[0].Reason);
        Assert.StartsWith("Small and quick: fits your 16 GB GPU with room for 128k context", picks[1].Reason, StringComparison.Ordinal);
        Assert.Equal("fim-small", picks[0].Companion!.Model.Id);
        Assert.Equal(PickKind.Completion, picks[0].Companion!.Kind);
        Assert.StartsWith("Fills in code as you type in the editor", picks[0].Companion!.Reason, StringComparison.Ordinal);
        Assert.EndsWith("It cannot chat.", picks[0].Companion!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsWithoutChatAreNeverChatPicksAndEmbeddingsAreIgnored()
    {
        var embed = CatalogTestData.Model("embed", new(2026, 9, 1), new(Everyday: 99, Coding: 99, Fast: 99), CatalogTestData.Small, new(0.3), [Capability.Embeddings], 8192, CatalogTestData.Tier("Q8_0", 0.3));
        var withEmbed = Catalog with { Families = [.. Catalog.Families, new("e", "E", "E", null, [embed])] };

        var picks = Recommender.Recommend(CatalogTestData.Machine(16, 64), withEmbed);

        Assert.DoesNotContain(picks, p => p.Model.Id is "embed" or "fim-small" or "fim-mid");
    }

    [Fact]
    public void ATinyMachineStillGetsAnAnswerOrNothingNeverAnException()
    {
        Assert.Empty(Recommender.Recommend(CatalogTestData.Machine(null, 2), Catalog));
        Assert.NotEmpty(Recommender.Recommend(CatalogTestData.Machine(null, 8), Catalog));
    }

    [Fact]
    public void BudgetsFollowTheHardware()
    {
        var gpu = Recommender.BudgetFor(CatalogTestData.Machine(16, 64));
        Assert.Equal(16 * CatalogTestData.Gib - 16 * CatalogTestData.Gib / 10, gpu.VramBytes); // a tenth kept back
        Assert.Equal(16 * CatalogTestData.Gib, gpu.CardVramBytes);
        Assert.Equal(8 * CatalogTestData.Gib - 3 * CatalogTestData.Gib / 2, Recommender.BudgetFor(CatalogTestData.Machine(8, 16)).VramBytes); // at least 1.5 GiB kept back
        Assert.Equal(46 * CatalogTestData.Gib, gpu.RamBytes);
        Assert.Equal(0, Recommender.BudgetFor(CatalogTestData.Machine(24, 32, GpuVendor.Apple)).RamBytes);
        Assert.Equal(0, Recommender.BudgetFor(CatalogTestData.Machine(null, 1)).RamBytes);
    }
}
