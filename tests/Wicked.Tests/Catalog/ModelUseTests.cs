using Wicked.Core.Catalog;
using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Core.Models;
using Wicked.Core.Store;

namespace Wicked.Tests.Catalog;

/// <summary>Chat models apart from completion-only models, and the rule that hides what this machine cannot run usefully.</summary>
public sealed class ModelUseTests
{
    private static CatalogModel Find(string id) => CatalogTestData.Catalog().Models.Single(m => m.Id == id);

    private static StoredModel Stored(string repo, GgufInfo? gguf, string? catalogId = null)
    {
        var path = $"/m/{repo}/Q8_0.gguf";
        var variant = new StoredVariant($"{repo}:Q8_0", "Q8_0", [new StoredFile(path, 1L << 30, null)], 1L << 30, null, gguf);
        var manifest = catalogId is null ? null : ModelManifest.New(repo, "rev", [], catalogId: catalogId);
        return new StoredModel(repo, $"/m/{repo}", manifest, [variant], 1L << 30);
    }

    private static GgufInfo Gguf(bool? template) => new("qwen2", "Test", 32768, 24, 1536, 12, 2, 128, 128, null, null, null, 7, HasChatTemplate: template);

    [Fact]
    public void TheCatalogSaysWhichModelsChatAndWhichOnlyCompleteCode()
    {
        Assert.Equal(new ModelUse(Chats: false, Completes: true), ModelUse.Of(Find("fim-small")));
        Assert.True(ModelUse.Of(Find("fim-small")).CompletionOnly);
        Assert.Equal(new ModelUse(Chats: true, Completes: true), ModelUse.Of(Find("coder-moe")));
        Assert.False(ModelUse.Of(Find("coder-moe")).CompletionOnly);
        Assert.Equal(new ModelUse(Chats: true, Completes: false), ModelUse.Of(Find("mid")));
    }

    [Fact]
    public void AnInstalledModelTakesTheCatalogsWordEvenThoughItsFileCarriesAChatTemplate()
    {
        // The reported case: a base completion model whose GGUF has a chat template all the same (Qwen2.5-Coder 1.5B).
        var catalog = CatalogTestData.Catalog();

        Assert.True(ModelUse.Of(Stored("test/fim-small", Gguf(template: true)), null, catalog).CompletionOnly);
        Assert.True(ModelUse.Of(Stored("someone/renamed", Gguf(template: true), catalogId: "fim-small"), null, catalog).CompletionOnly);
    }

    [Fact]
    public void AModelTheCatalogDoesNotKnowChatsUnlessItsFileHasNoChatTemplate()
    {
        var catalog = CatalogTestData.Catalog();

        Assert.Equal(new ModelUse(true, false), ModelUse.Of(Stored("hub/chatty", Gguf(template: true)), null, catalog));
        Assert.Equal(new ModelUse(true, false), ModelUse.Of(Stored("hub/unread", null), null, catalog));
        Assert.Equal(new ModelUse(false, false), ModelUse.Of(Stored("hub/base", Gguf(template: false)), null, null));
    }

    [Fact]
    public void ThePlanCarriesEachModelsUseToTheRuntime()
    {
        var models = new[] { Stored("test/fim-small", Gguf(template: true)), Stored("test/mid", Gguf(template: true)) };

        var plan = RunPlanner.Plan(models, CatalogTestData.Catalog(), CatalogTestData.Machine(16, 64), EngineBackendKind.Cuda, _ => new ModelRunSettings(), null);

        Assert.True(plan.Models.Single(m => m.Id == "test/fim-small:Q8_0").Use.CompletionOnly);
        Assert.Equal(new ModelUse(true, false), plan.Models.Single(m => m.Id == "test/mid:Q8_0").Use);
    }

    [Fact]
    public void OnASixteenGigabyteCardADenseModelMostlyOffloadedIsNotUsableButAnOffloadedMixtureOfExpertsIs()
    {
        var machine = CatalogTestData.Machine(16, 64);

        // 32B dense at Q4_K_M (18 GiB) leaves under three quarters of its layers on the card: slow, as the 27B the user tried.
        Assert.All(Find("big").Tiers, t => Assert.False(ModelFit.Usable(Find("big"), t, machine), t.Quant));
        Assert.Equal(FitLevel.FitsWithOffload, ModelFit.For(Find("big"), Find("big").Tiers.Single(t => t.Quant == "Q4_K_M"), machine)!.Level);
        // A mixture of experts reads a few experts a token, so offloading it stays quick.
        Assert.True(ModelFit.Usable(Find("coder-moe"), Find("coder-moe").Tiers.Single(t => t.Quant == "Q4_K_M"), machine));
        // What fits in video memory is usable; a quant that does not fit is not, though a smaller one of the same model is.
        var mid = Find("mid");
        Assert.True(ModelFit.Usable(mid, mid.Tiers.Single(t => t.Quant == "Q8_0"), machine));
        Assert.True(ModelFit.Usable(Find("fim-mid"), Find("fim-mid").Tiers[0], machine));
    }

    [Fact]
    public void WithoutAGpuOnlyModelsSmallEnoughForTheCpuAreUsable()
    {
        var machine = CatalogTestData.Machine(null, 32);

        Assert.False(ModelFit.Usable(Find("mid"), Find("mid").Tiers.Single(t => t.Quant == "Q4_K_M"), machine)); // 14B: fits in RAM, crawls
        Assert.True(ModelFit.Usable(Find("small"), Find("small").Tiers[0], machine));
        Assert.True(ModelFit.Usable(Find("fim-mid"), Find("fim-mid").Tiers[0], machine)); // 7B
        Assert.True(ModelFit.Usable(Find("coder-moe"), Find("coder-moe").Tiers.Single(t => t.Quant == "Q4_K_M"), machine)); // 3B active
    }

    [Fact]
    public void AModelThatDoesNotFitAtAllIsNeverUsableAndAnUnknownMachineHidesNothing()
    {
        var small = CatalogTestData.Machine(8, 16);

        Assert.All(Find("big").Tiers, t => Assert.False(ModelFit.Usable(Find("big"), t, small)));
        Assert.All(Find("big").Tiers, t => Assert.True(ModelFit.Usable(Find("big"), t, null)));
    }

    public static TheoryData<double?, long> Machines => new()
    {
        { 8, 32 },
        { 12, 32 },
        { 16, 64 },
        { 24, 64 },
        { null, 32 },
        { null, 16 },
    };

    [Theory]
    [MemberData(nameof(Machines))]
    public void EveryRecommendationIsUsableSoBrowseNeverHidesAPick(double? vram, long ram)
    {
        var machine = CatalogTestData.Machine(vram, ram);

        foreach (var pick in Recommender.Recommend(machine, CatalogTestData.Catalog()))
        {
            Assert.True(ModelFit.Usable(pick.Model, pick.Tier, machine), $"{pick.Kind} {pick.Model.Id} {pick.Tier.Quant}");
            if (pick.Companion is { } partner) Assert.True(ModelFit.Usable(partner.Model, partner.Tier, machine), $"companion {partner.Model.Id}");
        }
    }
}
