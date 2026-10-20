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
    // 8 GiB GPU: only the 4B fits (Q8 with room for 64k); the tiny model is the quick one; fim-small fits beside it, which trims the context to 16k.
    [InlineData(8.0, 16L, "Everyday+Coding:small:Q8_0:16384+fim-small | Fast:tiny:Q8_0:32768")]
    // 12 GiB: the 14B at Q4 (16k of room); the companion would push it under 16k so there is none; no offload candidate is mostly on the GPU.
    [InlineData(12.0, 32L, "Everyday+Coding:mid-new:Q4_K_M:16384 | Fast:tiny:Q8_0:32768")]
    // 16 GiB: the 14B at Q4 with 32k; the 1.5B completion model fits beside it; the 32B would be only 72% on the GPU, below the 75% bar.
    [InlineData(16.0, 64L, "Everyday+Coding:mid-new:Q4_K_M:32768+fim-small | Fast:tiny:Q8_0:32768")]
    // 24 GiB: the 32B at Q4 fits with 16k of room (22.0 needed); the companion would take it to 22.2 so there is none.
    [InlineData(24.0, 64L, "Everyday+Coding:big:Q4_K_M:16384 | Fast:tiny:Q8_0:32768")]
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
        // 20 GiB of VRAM: "big" at Q4 needs 22.0, so it offloads; 20 / (22/32) = 29 of 32 layers (90%) stay on the GPU.
        var picks = Recommender.Recommend(CatalogTestData.Machine(20, 64), Catalog);

        var big = picks.Single(p => p.Kind == PickKind.Big);
        Assert.Equal("big", big.Model.Id);
        Assert.Equal("Q4_K_M", big.Tier.Quant);
        Assert.Equal(FitLevel.FitsWithOffload, big.Fit.Level);
        Assert.Equal(29, big.Fit.GpuLayers);
        Assert.Equal("mid-new", picks[0].Model.Id);
        Assert.Contains("29 of 32 layers on the GPU", big.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewerReleaseWinsATieAndThenTheIdDoes()
    {
        var picks = Recommender.Recommend(CatalogTestData.Machine(12, 32), Catalog);
        Assert.Equal("mid-new", picks[0].Model.Id);

        var sameDay = CatalogTestData.Catalog() with
        {
            Families = [CatalogTestData.Catalog().Families[0] with { Models = [.. CatalogTestData.Catalog().Families[0].Models.Select(m => m.Id == "mid-new" ? m with { Released = new DateOnly(2026, 3, 1) } : m)] }],
        };
        Assert.Equal("mid", Recommender.Recommend(CatalogTestData.Machine(12, 32), sameDay)[0].Model.Id);
    }

    [Fact]
    public void ReasonsNameTheGpuAndTheContext()
    {
        var picks = Recommender.Recommend(CatalogTestData.Machine(16, 64), Catalog);

        Assert.Equal("Fits your 16 GB GPU with room for 32k context, with fim-small alongside for code completion", picks[0].Reason);
        Assert.StartsWith("Small and quick: fits your 16 GB GPU with room for 128k context", picks[1].Reason, StringComparison.Ordinal);
        Assert.Equal("fim-small", picks[0].Companion!.Model.Id);
        Assert.Contains("completions as you type", picks[0].Companion!.Reason, StringComparison.Ordinal);
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
        Assert.Equal(16 * CatalogTestData.Gib, gpu.VramBytes);
        Assert.Equal(46 * CatalogTestData.Gib, gpu.RamBytes);
        Assert.Equal(0, Recommender.BudgetFor(CatalogTestData.Machine(24, 32, GpuVendor.Apple)).RamBytes);
        Assert.Equal(0, Recommender.BudgetFor(CatalogTestData.Machine(null, 1)).RamBytes);
    }
}
