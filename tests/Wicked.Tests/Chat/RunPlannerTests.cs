using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Core.Hardware;
using Wicked.Core.Store;

namespace Wicked.Tests.Chat;

public sealed class RunPlannerTests
{
    private const long Gib = 1L << 30;

    private static GgufInfo Gguf(int layers = 32, int kvHeads = 8, int context = 40960) =>
        new("llama", "Test", context, layers, 4096, 32, kvHeads, 128, 128, null, null, null, 15);

    private static StoredModel Model(string repo, string quant, long size, GgufInfo? gguf, string? mmproj = null)
    {
        var path = $"/m/{repo}/{quant}.gguf";
        var variant = new StoredVariant($"{repo}:{quant}", quant, [new StoredFile(path, size, null)], size, mmproj, gguf);
        return new StoredModel(repo, $"/m/{repo}", null, [variant], size);
    }

    private static HardwareInfo Machine(long vram, long ram = 64 * Gib) =>
        new(new CpuInfo("Test CPU", 16, true, false), ram, ram / 2, vram == 0 ? [] : [new GpuInfo(GpuVendor.Nvidia, "Test GPU", vram, vram, "600", new Version(13, 4), "10de:0000")]);

    private static string Render(IEnumerable<StoredModel> models, HardwareInfo? hardware, EngineBackendKind? backend, Func<string, ModelRunSettings>? settings = null, int? max = null) =>
        RunPlanner.Plan(models, null, hardware, backend, settings ?? (_ => new ModelRunSettings()), max).Render();

    [Fact]
    public void AutomaticDefaultsOnACudaCardLeaveTheContextAndTheLayersToTheEngine()
    {
        // The engine's --fit sets both from the memory free when the model loads: Wicked setting either turns that off, and its
        // arithmetic from the card's total memory set them too large (a 99k context and every layer on a card that had 13.6 of
        // its 16 GB free failed to load).
        var text = Render([Model("a/chat", "Q4_K_M", 5 * Gib, Gguf())], Machine(16 * Gib), EngineBackendKind.Cuda);

        Assert.Equal("""
            version = 1

            [*]
            jinja = true
            reasoning-format = deepseek

            [a/chat:Q4_K_M]
            flash-attn = on
            model = /m/a/chat/Q4_K_M.gguf
            """.Replace("\r", "", StringComparison.Ordinal) + "\n", text.Replace("\r", "", StringComparison.Ordinal));
    }

    [Fact]
    public void ACpuEngineGetsNoGpuLayersAndNoFlashAttentionChoice()
    {
        var text = Render([Model("a/chat", "Q4_K_M", 5 * Gib, Gguf())], Machine(16 * Gib), EngineBackendKind.Cpu);

        Assert.Contains("n-gpu-layers = 0\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("flash-attn", text, StringComparison.Ordinal);
        Assert.DoesNotContain("c =", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelTooBigForVideoMemoryIsLeftToTheEngineToOffload()
    {
        // A 20 GiB model on a 16 GiB card: the engine keeps as many layers on the GPU as the free memory holds and the rest in RAM.
        var text = Render([Model("a/big", "Q4_K_M", 20 * Gib, Gguf(layers: 64))], Machine(16 * Gib), EngineBackendKind.Cuda);

        Assert.DoesNotContain("n-gpu-layers", text, StringComparison.Ordinal);
        Assert.DoesNotContain("c =", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHugeTrainedContextIsNotWrittenEitherTheEngineFitsItDown()
    {
        var text = Render([Model("a/chat", "Q4_K_M", 2 * Gib, Gguf(context: 1_048_576))], Machine(24 * Gib), EngineBackendKind.Cuda);

        Assert.DoesNotContain("c =", text, StringComparison.Ordinal);
        Assert.DoesNotContain("n-gpu-layers", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsersChoicesWinAndEveryOneIsWrittenWithTheEnginesOwnName()
    {
        var chat = Model("a/chat", "Q4_K_M", 5 * Gib, Gguf());
        var draft = Model("a/draft", "Q8_0", 1 * Gib, Gguf());
        var settings = new ModelRunSettings(
            ContextSize: 8192, GpuLayers: "20", KvCacheType: "q8_0", FlashAttention: false, DraftModelId: "a/draft:Q8_0", Reasoning: false,
            Sampling: new ChatSampling(Temperature: 0.7, TopP: 0.95, MaxTokens: 512));

        var text = Render([chat, draft], Machine(16 * Gib), EngineBackendKind.Cuda, id => id == "a/chat:Q4_K_M" ? settings : new ModelRunSettings());

        Assert.Equal("""
            [a/chat:Q4_K_M]
            c = 8192
            cache-type-k = q8_0
            cache-type-v = q8_0
            flash-attn = off
            model = /m/a/chat/Q4_K_M.gguf
            model-draft = /m/a/draft/Q8_0.gguf
            n-gpu-layers = 20
            n-predict = 512
            reasoning = off
            reasoning-budget = 0
            spec-type = draft-simple
            temp = 0.7
            top-p = 0.95
            """.Replace("\r", "", StringComparison.Ordinal), SectionOf(text, "a/chat:Q4_K_M"));
    }

    [Fact]
    public void AQuantisedValueCacheTurnsFlashAttentionOnEvenWhereTheEngineWouldChoose()
    {
        var text = Render([Model("a/chat", "Q4_K_M", 5 * Gib, Gguf())], Machine(0), EngineBackendKind.Vulkan, _ => new ModelRunSettings(KvCacheType: "q4_0"));

        Assert.Contains("flash-attn = on\n", text, StringComparison.Ordinal);
        Assert.Contains("cache-type-v = q4_0\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReasoningOnIsWrittenAndAutomaticIsLeftToTheTemplate()
    {
        var model = Model("a/chat", "Q4_K_M", 5 * Gib, Gguf());

        var on = Render([model], Machine(16 * Gib), EngineBackendKind.Cuda, _ => new ModelRunSettings(Reasoning: true));
        var auto = Render([model], Machine(16 * Gib), EngineBackendKind.Cuda);

        Assert.Contains("reasoning = on\n", on, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning =", auto, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning-budget", on, StringComparison.Ordinal);
    }

    [Fact]
    public void AProjectorFileIsPassedAndAnUnknownDraftIsDropped()
    {
        var vision = Model("a/vision", "Q4_K_M", 4 * Gib, Gguf(), mmproj: "/m/a/vision/mmproj-F16.gguf");

        var text = Render([vision], Machine(16 * Gib), EngineBackendKind.Cuda, _ => new ModelRunSettings(DraftModelId: "gone/model:Q4"));

        Assert.Contains("mmproj = /m/a/vision/mmproj-F16.gguf\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("model-draft", text, StringComparison.Ordinal);
        Assert.DoesNotContain("spec-type", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAShapeOrAMachineTheEngineIsLeftToFitItself()
    {
        var unknown = Model("local/mystery", "", 3 * Gib, null);

        var noShape = Render([unknown], Machine(16 * Gib), EngineBackendKind.Cuda);
        var noMachine = Render([Model("a/chat", "Q4_K_M", 5 * Gib, Gguf())], null, null);

        Assert.DoesNotContain("c =", noShape, StringComparison.Ordinal);
        Assert.DoesNotContain("n-gpu-layers", noShape, StringComparison.Ordinal);
        Assert.DoesNotContain("c =", noMachine, StringComparison.Ordinal);
        Assert.DoesNotContain("n-gpu-layers", noMachine, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelsMaxIsOneUnlessVideoMemoryClearlyHoldsMore()
    {
        var small = new[] { Model("a/one", "Q4_K_M", 2 * Gib, Gguf()), Model("a/two", "Q4_K_M", 2 * Gib, Gguf()), Model("a/three", "Q4_K_M", 2 * Gib, Gguf()) };
        var big = new[] { Model("a/one", "Q4_K_M", 9 * Gib, Gguf()), Model("a/two", "Q4_K_M", 9 * Gib, Gguf()) };

        int Max(IEnumerable<StoredModel> models, HardwareInfo? hw, EngineBackendKind? backend, int? setting = null, int? inForce = null) =>
            RunPlanner.Plan(models, null, hw, backend, _ => new ModelRunSettings(), setting, inForce).ModelsMax;

        Assert.Equal(3, Max(small, Machine(24 * Gib), EngineBackendKind.Cuda));
        Assert.Equal(1, Max(big, Machine(16 * Gib), EngineBackendKind.Cuda));
        Assert.Equal(1, Max(small, Machine(24 * Gib), EngineBackendKind.Cpu));
        Assert.Equal(1, Max(small, Machine(0), null));
        Assert.Equal(2, Max(small, Machine(24 * Gib), EngineBackendKind.Cuda, setting: 2));
        Assert.Equal(2, Max(small, Machine(24 * Gib), EngineBackendKind.Cuda, setting: 1, inForce: 2)); // the running router's number holds until it restarts
    }

    [Fact]
    public void WithSeveralModelsLoadedEachIsFittedByTheEngineToWhatIsFreeWhenItLoads()
    {
        var model = Model("a/chat", "Q4_K_M", 5 * Gib, Gguf());

        foreach (var max in new int?[] { 1, 3 })
        {
            var text = Render([model], Machine(16 * Gib), EngineBackendKind.Cuda, max: max);
            Assert.DoesNotContain("c =", text, StringComparison.Ordinal);
            Assert.DoesNotContain("n-gpu-layers", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheSameInputsAlwaysGiveTheSameText()
    {
        var models = new[] { Model("b/two", "Q4_K_M", 3 * Gib, Gguf()), Model("a/one", "Q8_0", 4 * Gib, Gguf()) };

        Assert.Equal(Render(models, Machine(16 * Gib), EngineBackendKind.Cuda), Render([.. models.Reverse()], Machine(16 * Gib), EngineBackendKind.Cuda));
    }

    private static string SectionOf(string preset, string name)
    {
        var start = preset.IndexOf($"[{name}]", StringComparison.Ordinal);
        var end = preset.IndexOf("\n\n[", start, StringComparison.Ordinal);
        return preset[start..(end < 0 ? preset.Length : end)].TrimEnd('\n').Replace("\r", "", StringComparison.Ordinal);
    }
}
