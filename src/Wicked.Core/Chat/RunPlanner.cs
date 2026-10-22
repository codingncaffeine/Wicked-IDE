using System.Globalization;
using Wicked.Core.Catalog;
using Wicked.Core.Engines;
using Wicked.Core.Hardware;
using Wicked.Core.Store;

namespace Wicked.Core.Chat;

/// <summary>One installed model as the router will run it: its section of the preset, and what the window shows about it.</summary>
internal sealed record PlannedModel(
    string Id,
    string DisplayName,
    string ModelPath,
    string? MmprojPath,
    bool Reasons,
    int? Context,
    PresetSection Section,
    ModelUse Use);

/// <summary>The preset for a router: the shared block, one section per model, and the models-loaded-at-once limit the router starts with.</summary>
internal sealed record RunPlan(int ModelsMax, IReadOnlyDictionary<string, string> Globals, IReadOnlyList<PlannedModel> Models)
{
    public string Render() => PresetWriter.Render(Globals, Models.Select(m => m.Section));
}

/// <summary>
/// Turns the store, the machine and the user's choices into a router preset (D3, D9). What the user did not choose about memory
/// (the context, the GPU layers) is left to the engine, which fits both to the memory free when the model loads; the models
/// loaded at once follow the fit arithmetic the Browse page uses. Everything here is deterministic and
/// reads only its arguments, so the same machine and store always give the same preset text.
/// </summary>
internal static class RunPlanner
{
    /// <summary>Video memory kept free for the desktop and the engine's compute buffers (llama-server's own <c>--fit-target</c> default is 1024 MiB).</summary>
    public const long VramMargin = 1L << 30;

    public const int MaxModelsAuto = 4;


    public static readonly IReadOnlySet<string> CacheTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "f32", "f16", "bf16", "q8_0", "q4_0", "q4_1", "iq4_nl", "q5_0", "q5_1",
    };

    public static RunPlan Plan(
        IEnumerable<StoredModel> installed,
        ModelCatalog? catalog,
        HardwareInfo? hardware,
        EngineBackendKind? backend,
        Func<string, ModelRunSettings> settingsFor,
        int? modelsMaxSetting,
        int? modelsMaxInForce = null)
    {
        var variants = installed.SelectMany(m => m.Variants.Select(v => (Model: m, Variant: v)))
            .OrderBy(x => x.Variant.Id, StringComparer.Ordinal)
            .ToList();
        var byId = variants.ToDictionary(x => x.Variant.Id, x => x.Variant, StringComparer.Ordinal);
        var budget = hardware is null ? null : Recommender.BudgetFor(hardware);
        var modelsMax = Math.Max(1, modelsMaxInForce ?? modelsMaxSetting ?? AutoModelsMax(variants.Select(x => (x.Model, x.Variant)), catalog, budget, backend));

        var globals = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Thoughts come back in reasoning_content, apart from the answer; the chat template is the model's own.
            ["jinja"] = "true",
            ["reasoning-format"] = "deepseek",
        };

        var planned = new List<PlannedModel>();
        foreach (var (model, variant) in variants)
        {
            var settings = settingsFor(variant.Id);
            var catalogModel = catalog?.Models.FirstOrDefault(m => string.Equals(m.Repo, model.Id, StringComparison.OrdinalIgnoreCase));
            planned.Add(PlanOne(model, variant, catalogModel, settings, byId, backend, catalog));
        }

        return new RunPlan(modelsMax, globals, planned);
    }

    private static PlannedModel PlanOne(
        StoredModel model, StoredVariant variant, CatalogModel? catalogModel, ModelRunSettings settings,
        Dictionary<string, StoredVariant> byId, EngineBackendKind? backend, ModelCatalog? catalog)
    {
        var extra = new Dictionary<string, string>(StringComparer.Ordinal);
        // The context and the GPU layers the user did not choose are left unset: the engine's --fit sets them at load time from
        // the memory really free then (the desktop's share, other programs, its own compute buffers), keeping 1 GiB spare.
        // Wicked's own arithmetic from the card's total memory set both too large (2026-10-21, a 16 GB card with 13.6 GB free:
        // a 99k context with every layer on the GPU failed to load; a 27B model with 47 layers forced on crashed on its first
        // reply), and setting either one turns the engine's fitting off. The real context is read from the engine once loaded.
        int? context = settings.ContextSize;
        string? layers = settings.GpuLayers;
        if (layers is null && backend == EngineBackendKind.Cpu) layers = "0";

        var quantisedValue = settings.KvCacheType is { } t && t is not ("f16" or "f32" or "bf16");
        var flash = settings.FlashAttention ?? (backend is EngineBackendKind.Cuda or EngineBackendKind.Vulkan or EngineBackendKind.Metal ? true : null);
        // A quantised value cache only works with flash attention, so it is not left to the engine's own choice.
        if (quantisedValue && settings.FlashAttention is null) flash = true;
        if (flash is { } f) extra["flash-attn"] = f ? "on" : "off";

        if (settings.KvCacheType is { } cache)
        {
            extra["cache-type-k"] = cache;
            extra["cache-type-v"] = cache;
        }

        if (settings.DraftModelId is { } draftId && byId.TryGetValue(draftId, out var draft))
        {
            // Verified on b11765: the draft file alone is loaded but not used; speculation has to be switched on with spec-type.
            extra["model-draft"] = draft.PrimaryPath;
            extra["spec-type"] = "draft-simple";
        }

        switch (settings.Reasoning)
        {
            case true:
                extra["reasoning"] = "on";
                break;
            case false:
                extra["reasoning"] = "off";
                extra["reasoning-budget"] = "0";
                break;
        }

        if (settings.Sampling is { } s)
        {
            if (s.Temperature is { } temperature) extra["temp"] = temperature.ToString("0.###", CultureInfo.InvariantCulture);
            if (s.TopP is { } topP) extra["top-p"] = topP.ToString("0.###", CultureInfo.InvariantCulture);
            if (s.MaxTokens is { } max) extra["n-predict"] = max.ToString(CultureInfo.InvariantCulture);
        }

        var section = PresetSection.ForModel(variant.Id, variant.PrimaryPath, variant.MmprojPath, context, layers, extra);
        var name = catalogModel?.Name ?? RepoName(model.Id);
        var display = variant.Quant.Length > 0 && variant.Id != model.Id ? $"{name} ({variant.Quant})" : name;
        var reasons = catalogModel?.Has(Capability.Reasoning) == true;
        return new PlannedModel(variant.Id, display, variant.PrimaryPath, variant.MmprojPath, reasons, context, section, ModelUse.Of(model, variant, catalog));
    }

    /// <summary>One model at a time unless video memory clearly holds the largest few together (each with a modest context).</summary>
    public static int AutoModelsMax(IEnumerable<(StoredModel Model, StoredVariant Variant)> variants, ModelCatalog? catalog, FitBudget? budget, EngineBackendKind? backend)
    {
        if (!HasGpu(budget, backend) || budget is null) return 1;
        var room = budget.VramBytes - VramMargin;
        var needs = new List<long>();
        foreach (var (model, variant) in variants)
        {
            var shape = catalog?.Models.FirstOrDefault(m => string.Equals(m.Repo, model.Id, StringComparison.OrdinalIgnoreCase))?.Shape ?? (variant.Gguf is { } g ? ModelShape.From(g) : null);
            var kvBytes = shape?.KvBytes(8192) ?? 0;
            needs.Add((long)((variant.Size + FileLength(variant.MmprojPath) + kvBytes) * FitCalculator.Headroom));
        }

        needs.Sort();
        needs.Reverse();
        var count = 1;
        while (count < MaxModelsAuto && count < needs.Count && needs.Take(count + 1).Sum() <= room) count++;
        return count;
    }

    /// <summary>The name to show for a model that is not in the catalog: the repo's name without its publisher or its <c>-GGUF</c> suffix.</summary>
    private static string RepoName(string modelId)
    {
        var name = modelId[(modelId.IndexOf('/', StringComparison.Ordinal) + 1)..]; // not a path: a model id
        return name.EndsWith("-GGUF", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
    }

    private static bool HasGpu(FitBudget? budget, EngineBackendKind? backend) =>
        backend is not EngineBackendKind.Cpu && budget is { VramBytes: > 0 };

    private static long FileLength(string? path)
    {
        if (path is null) return 0;
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
