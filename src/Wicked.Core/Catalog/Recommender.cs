using System.Globalization;
using Wicked.Core.Hardware;

namespace Wicked.Core.Catalog;

public enum PickKind
{
    Everyday,
    Coding,
    Fast,
    Big,
}

/// <summary>
/// One recommendation card: the model and quant to install, how it fits this machine, the context to run it at, and the
/// one-line reason shown to the user. <c>AlsoFor</c> lists other roles the same model won (one card, not two).
/// <c>Companion</c> is a small completion model that runs alongside a coding pick.
/// </summary>
public sealed record Pick(
    PickKind Kind,
    CatalogModel Model,
    QuantTier Tier,
    FitResult Fit,
    int Context,
    string Reason,
    IReadOnlyList<PickKind> AlsoFor,
    Pick? Companion = null);

/// <summary>The recommender's tunables; the defaults are the plan's (D25).</summary>
public sealed class RecommenderOptions
{
    /// <summary>Everyday and Coding picks must fit fully in video memory at least this large a context.</summary>
    public int MinContext { get; set; } = 16384;

    /// <summary>A context the pick should have room for if a good-enough quant allows it.</summary>
    public int ComfortableContext { get; set; } = 32768;

    /// <summary>Everyday and Coding picks never use a quant below this quality (Q4_K_M is 90).</summary>
    public int QualityFloor { get; set; } = 88;

    /// <summary>Fast and completion picks may use smaller quants.</summary>
    public int SmallQualityFloor { get; set; } = 75;

    /// <summary>Context for the completion companion.</summary>
    public int CompanionContext { get; set; } = 8192;

    /// <summary>A "Big" pick (CPU offload) must beat the Everyday pick's score by at least this much to be worth showing.</summary>
    public int BigMargin { get; set; } = 4;

    /// <summary>On a machine without a usable GPU only models with at most this many active parameters (billions) are offered.</summary>
    public double CpuOnlyMaxActive { get; set; } = 9;

    public KvCacheType Kv { get; set; } = KvCacheType.F16;
}

/// <summary>
/// "Recommend, don't list" (D25): from the machine and the catalog, two or three picks, one per role. For a role the highest
/// score among models that fit wins, ties go to the newer release; the quant is the best one that still leaves room for a
/// comfortable context. Everything is a pure function of its inputs.
/// </summary>
public static class Recommender
{
    private const long Gib = 1L << 30;

    public static IReadOnlyList<Pick> Recommend(HardwareInfo hardware, ModelCatalog catalog, RecommenderOptions? options = null)
    {
        options ??= new RecommenderOptions();
        var budget = BudgetFor(hardware);
        var gpuMode = budget.VramBytes >= 2 * Gib;
        var chat = catalog.Models.Where(m => m.Has(Capability.Chat) && !m.Has(Capability.Embeddings)).ToList();
        var picks = new List<Pick>();

        var everyday = BestFor(Role.Everyday, chat, budget, gpuMode, options);
        var coding = BestFor(Role.Coding, chat, budget, gpuMode, options);

        if (everyday is not null) picks.Add(everyday with { Kind = PickKind.Everyday });
        if (coding is not null)
        {
            var withPartner = WithCompanion(coding, catalog, budget, gpuMode, options);
            if (picks.Find(p => p.Model.Id == coding.Model.Id) is { } same)
            {
                picks[picks.IndexOf(same)] = same with
                {
                    AlsoFor = [PickKind.Coding],
                    Companion = withPartner.Companion,
                    Context = Math.Min(same.Context, withPartner.Context),
                    Reason = withPartner.Reason,
                };
            }
            else
            {
                picks.Add(withPartner with { Kind = PickKind.Coding });
            }
        }

        var taken = picks.Select(p => p.Model.Id).ToHashSet(StringComparer.Ordinal);
        var third = ThirdPick(chat.Where(m => !taken.Contains(m.Id)).ToList(), everyday, budget, gpuMode, options);
        if (third is not null) picks.Add(third);
        return picks;
    }

    /// <summary>The memory the machine offers: all of the primary GPU's memory, and three quarters of RAM less 2 GB for what spills over.</summary>
    public static FitBudget BudgetFor(HardwareInfo hardware)
    {
        var gpu = hardware.PrimaryGpu;
        var vram = gpu?.VramTotal ?? 0;
        var ram = Math.Max(0, hardware.RamTotal * 3 / 4 - 2 * Gib);
        // Apple's GPU memory is the same pool as RAM: do not count it twice.
        if (gpu?.Vendor == GpuVendor.Apple) ram = 0;
        return new FitBudget(vram, ram);
    }

    private static Pick? BestFor(Role role, List<CatalogModel> models, FitBudget budget, bool gpuMode, RecommenderOptions o)
    {
        Pick? best = null;
        foreach (var model in models)
        {
            if (model.Scores.For(role) is not { } score) continue;
            if (!gpuMode && model.Parameters.Active is { } active && active > o.CpuOnlyMaxActive) continue;
            if (!gpuMode && model.Parameters.Active is null && model.Parameters.Total > o.CpuOnlyMaxActive) continue;
            var floor = role == Role.Fim ? o.SmallQualityFloor : o.QualityFloor;
            var choice = ChooseTier(model, budget, gpuMode, o, floor, o.MinContext, o.ComfortableContext);
            if (choice is null) continue;
            var candidate = Build(role == Role.Coding ? PickKind.Coding : PickKind.Everyday, model, choice.Value.Tier, choice.Value.Fit, budget, gpuMode, o);
            if (best is null || Better(model, score, best.Model, best.Model.Scores.For(role) ?? 0)) best = candidate;
        }

        return best;
    }

    private static Pick? ThirdPick(List<CatalogModel> models, Pick? everyday, FitBudget budget, bool gpuMode, RecommenderOptions o)
    {
        Pick? big = null;
        if (gpuMode && everyday is not null)
        {
            var baseline = everyday.Model.Scores.Everyday ?? 0;
            foreach (var model in models)
            {
                if (model.Scores.Everyday is not { } score || score < baseline + o.BigMargin) continue;
                var choice = ChooseOffloadTier(model, budget, o);
                if (choice is null) continue;
                var candidate = BuildBig(model, choice.Value.Tier, choice.Value.Fit);
                if (big is null || Better(model, score, big.Model, big.Model.Scores.Everyday ?? 0)) big = candidate;
            }
        }

        if (big is not null) return big;

        Pick? fast = null;
        foreach (var model in models)
        {
            if (model.Scores.Fast is not { } score) continue;
            if (!gpuMode && (model.Parameters.Active ?? model.Parameters.Total) > o.CpuOnlyMaxActive) continue;
            var choice = ChooseTier(model, budget, gpuMode, o, o.SmallQualityFloor, 8192, o.ComfortableContext);
            if (choice is null) continue;
            var candidate = Build(PickKind.Fast, model, choice.Value.Tier, choice.Value.Fit, budget, gpuMode, o);
            if (fast is null || Better(model, score, fast.Model, fast.Model.Scores.Fast ?? 0)) fast = candidate;
        }

        return fast;
    }

    /// <summary>Adds a small completion model that fits next to the coding pick, or the coding pick itself when it can complete code.</summary>
    private static Pick WithCompanion(Pick coding, ModelCatalog catalog, FitBudget budget, bool gpuMode, RecommenderOptions o)
    {
        if (coding.Model.Has(Capability.Fim)) return coding with { Reason = coding.Reason + "; it also completes code as you type" };

        Pick? best = null;
        foreach (var model in catalog.Models.Where(m => m.Has(Capability.Fim) && m.Id != coding.Model.Id))
        {
            if (model.Scores.Fim is not { } score) continue;
            if (!gpuMode && (model.Parameters.Active ?? model.Parameters.Total) > 4) continue;
            // The partner must fit in what the coding model leaves, and the coding model must still keep its minimum context.
            var tier = model.Tiers.Where(t => t.Quality >= o.SmallQualityFloor).OrderByDescending(t => t.Quality)
                .FirstOrDefault(t => FitCalculator.Evaluate(t.Size, model.Shape, o.CompanionContext, budget, model.ContextLength, o.Kv).Level == (gpuMode ? FitLevel.FitsInVram : FitLevel.FitsWithOffload));
            if (tier is null) continue;
            var partnerFit = FitCalculator.Evaluate(tier.Size, model.Shape, o.CompanionContext, budget, model.ContextLength, o.Kv);
            var rest = new FitBudget(Math.Max(0, budget.VramBytes - (gpuMode ? partnerFit.RequiredBytes : 0)), Math.Max(0, budget.RamBytes - (gpuMode ? 0 : partnerFit.RequiredBytes)));
            var codingFit = FitCalculator.Evaluate(coding.Tier.Size, coding.Model.Shape, o.MinContext, rest, coding.Model.ContextLength, o.Kv);
            if (codingFit.Level != (gpuMode ? FitLevel.FitsInVram : FitLevel.FitsWithOffload)) continue;
            var candidate = new Pick(PickKind.Coding, model, tier, partnerFit, o.CompanionContext, "", [], null);
            if (best is null || Better(model, score, best.Model, best.Model.Scores.Fim ?? 0)) best = candidate;
        }

        if (best is null) return coding;
        var partnerRequired = best.Fit.RequiredBytes;
        var remaining = new FitBudget(Math.Max(0, budget.VramBytes - (gpuMode ? partnerRequired : 0)), Math.Max(0, budget.RamBytes - (gpuMode ? 0 : partnerRequired)));
        var fit = FitCalculator.Evaluate(coding.Tier.Size, coding.Model.Shape, o.MinContext, remaining, coding.Model.ContextLength, o.Kv);
        var room = Nice(Math.Min(coding.Model.ContextLength, gpuMode ? fit.MaxContextInVram : fit.MaxContextWithOffload));
        var reason = $"{Fits(gpuMode, budget, fit, room)}, with {best.Model.Name} alongside for code completion";
        var companion = best with { Kind = PickKind.Fast, Reason = $"A small model for completions as you type ({best.Tier.Quant}, about {Gb(best.Fit.RequiredBytes)} GB)" };
        return coding with { Fit = fit, Context = DefaultContext(room, coding.Model, o), Reason = reason, Companion = companion };
    }

    /// <summary>The best quant that fits and leaves room for the comfortable context; failing that, the best that fits the minimum.</summary>
    private static (QuantTier Tier, FitResult Fit)? ChooseTier(CatalogModel model, FitBudget budget, bool gpuMode, RecommenderOptions o, int qualityFloor, int minContext, int comfortable)
    {
        var wanted = gpuMode ? FitLevel.FitsInVram : FitLevel.FitsWithOffload;
        var tiers = model.Tiers.Where(t => t.Quality >= qualityFloor).OrderByDescending(t => t.Quality).ToList();
        var min = Math.Min(minContext, model.ContextLength);
        var cozy = Math.Min(comfortable, model.ContextLength);
        (QuantTier, FitResult)? fallback = null;
        foreach (var tier in tiers)
        {
            var fit = FitCalculator.Evaluate(tier.Size, model.Shape, min, budget, model.ContextLength, o.Kv);
            if (fit.Level != wanted) continue;
            var room = gpuMode ? fit.MaxContextInVram : fit.MaxContextWithOffload;
            if (room >= cozy) return (tier, fit);
            fallback ??= (tier, fit);
        }

        return fallback;
    }

    /// <summary>For a "Big" pick: the smallest quant that is still good enough (offloaded layers run at memory speed, so smaller is faster), provided the model needs offload but keeps most layers on the GPU or is a mixture-of-experts (cheap to offload).</summary>
    private static (QuantTier Tier, FitResult Fit)? ChooseOffloadTier(CatalogModel model, FitBudget budget, RecommenderOptions o)
    {
        foreach (var tier in model.Tiers.Where(t => t.Quality >= o.QualityFloor).OrderBy(t => t.Quality))
        {
            var fit = FitCalculator.Evaluate(tier.Size, model.Shape, o.MinContext, budget, model.ContextLength, o.Kv);
            if (fit.Level != FitLevel.FitsWithOffload) continue;
            var mostlyOnGpu = fit.GpuLayers >= model.Shape.Layers * 0.75;
            if (model.Parameters.IsMoe || mostlyOnGpu) return (tier, fit);
        }

        return null;
    }

    private static bool Better(CatalogModel a, int scoreA, CatalogModel b, int scoreB) =>
        scoreA != scoreB ? scoreA > scoreB : a.Released != b.Released ? a.Released > b.Released : string.CompareOrdinal(a.Id, b.Id) < 0;

    private static Pick Build(PickKind kind, CatalogModel model, QuantTier tier, FitResult fit, FitBudget budget, bool gpuMode, RecommenderOptions o)
    {
        var room = Nice(Math.Min(model.ContextLength, gpuMode ? fit.MaxContextInVram : fit.MaxContextWithOffload));
        var reason = Fits(gpuMode, budget, fit, room);
        if (kind == PickKind.Fast && gpuMode) reason = "Small and quick: " + char.ToLowerInvariant(reason[0]) + reason[1..];
        return new Pick(kind, model, tier, fit, DefaultContext(room, model, o), reason, []);
    }

    /// <summary>The sentence behind a card: what the pick needs and how much context the memory allows.</summary>
    private static string Fits(bool gpuMode, FitBudget budget, FitResult fit, int room) => gpuMode
        ? $"Fits your {Gb(budget.VramBytes)} GB GPU with room for {Label(room)} context"
        : $"Runs on your CPU using about {Gb(fit.RequiredBytes)} GB of RAM, with room for {Label(room)} context";

    /// <summary>The context to start the model with: what fits, but no more than the comfortable size (a huge cache costs load time for nothing) and never below 8k.</summary>
    private static int DefaultContext(int room, CatalogModel model, RecommenderOptions o) =>
        Math.Max(Math.Min(room, o.ComfortableContext), Math.Min(8192, model.ContextLength));

    private static Pick BuildBig(CatalogModel model, QuantTier tier, FitResult fit)
    {
        var context = Math.Min(model.ContextLength, 16384);
        var reason = $"Smarter but slower: runs with CPU offload ({fit.GpuLayers} of {model.Shape.Layers} layers on the GPU, about {Gb(fit.RequiredBytes)} GB in total)";
        return new Pick(PickKind.Big, model, tier, fit, context, reason, []);
    }

    /// <summary>Rounds a context down to a size people recognise: 8k, 16k, 32k, 64k, 128k, 256k.</summary>
    private static int Nice(int context)
    {
        foreach (var size in new[] { 262144, 131072, 65536, 32768, 16384, 8192, 4096 })
        {
            if (context >= size) return size;
        }

        return context;
    }

    private static string Label(int context) => context >= 1024 ? $"{(context / 1024).ToString(CultureInfo.InvariantCulture)}k" : context.ToString(CultureInfo.InvariantCulture);

    private static string Gb(long bytes) => Math.Round(bytes / (double)Gib).ToString("0", CultureInfo.InvariantCulture);
}
