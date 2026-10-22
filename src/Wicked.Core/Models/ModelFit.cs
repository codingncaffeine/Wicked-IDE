using Wicked.Core.Catalog;
using Wicked.Core.Hardware;

namespace Wicked.Core.Models;

/// <summary>The fit of a catalog model on this machine, for the browse page's badges and its default quant.</summary>
public static class ModelFit
{
    /// <summary>The context the badges are judged at: the recommender's minimum, or the model's own maximum when smaller.</summary>
    public static int BadgeContext(int nativeContext) => Math.Min(new RecommenderOptions().MinContext, Math.Max(1024, nativeContext));

    public static FitResult? For(ModelShape shape, long weightsBytes, int nativeContext, HardwareInfo? hardware) =>
        hardware is null ? null : FitCalculator.Evaluate(weightsBytes, shape, BadgeContext(nativeContext), Recommender.BudgetFor(hardware), nativeContext);

    public static FitResult? For(CatalogModel model, QuantTier tier, HardwareInfo? hardware) => For(model.Shape, tier.Size, model.ContextLength, hardware);

    /// <summary>
    /// The quant installed when the user does not choose (the recommender's rule): the best one of Q4_K_M quality or better that
    /// fits the machine the way the machine wants (fully in video memory when there is a GPU) with room for a comfortable context;
    /// failing that the best that fits at all; for a model too big for this PC, the smallest decent quant, so the badge says so.
    /// </summary>
    public static QuantTier DefaultTier(CatalogModel model, HardwareInfo? hardware)
    {
        var options = new RecommenderOptions();
        var tiers = model.Tiers.OrderByDescending(t => t.Quality).ToList();
        if (hardware is null || tiers.Count == 0) return tiers.OrderBy(t => Math.Abs(t.Quality - 90)).FirstOrDefault() ?? model.Tiers[0];

        var budget = Recommender.BudgetFor(hardware);
        var gpuMode = budget.VramBytes >= 2L << 30;
        var wanted = gpuMode ? FitLevel.FitsInVram : FitLevel.FitsWithOffload;
        var context = BadgeContext(model.ContextLength);
        var cozy = Math.Min(options.ComfortableContext, model.ContextLength);
        QuantTier? fallback = null;
        foreach (var tier in tiers.Where(t => t.Quality >= options.QualityFloor))
        {
            var fit = FitCalculator.Evaluate(tier.Size, model.Shape, context, budget, model.ContextLength);
            if (fit.Level != wanted) continue;
            if ((gpuMode ? fit.MaxContextInVram : fit.MaxContextWithOffload) >= cozy) return tier;
            fallback ??= tier;
        }

        if (fallback is not null) return fallback;
        // Nothing decent fits the way we want: the best that fits at all, else the smallest, so the badge is honest.
        foreach (var tier in tiers)
        {
            if (FitCalculator.Evaluate(tier.Size, model.Shape, context, budget, model.ContextLength).Level != FitLevel.WontFit) return tier;
        }

        return tiers[^1];
    }

    /// <summary>
    /// Whether this machine can run the quant usefully, by the recommender's own rule: fully in video memory; offloaded to the CPU
    /// only when that stays quick (a mixture-of-experts model, or most layers still on the GPU); on a machine without a usable
    /// GPU, a model small enough for the CPU. Browse hides what fails it. True when the machine is not known yet.
    /// </summary>
    public static bool Usable(CatalogModel model, QuantTier tier, HardwareInfo? hardware) =>
        hardware is null || Usable(For(model, tier, hardware)!, model.Shape.Layers, model.Parameters.IsMoe, model.Parameters.Active ?? model.Parameters.Total, Recommender.BudgetFor(hardware));

    /// <summary>The same rule for a fit already judged; <paramref name="activeBillions"/> is null when the parameter count is not known (a Hugging Face repo).</summary>
    public static bool Usable(FitResult fit, int layers, bool moe, double? activeBillions, FitBudget budget)
    {
        if (budget.VramBytes >= Recommender.GpuModeVram)
        {
            return fit.Level == FitLevel.FitsInVram
                || fit.Level == FitLevel.FitsWithOffload && (moe || fit.GpuLayers >= layers * Recommender.MostlyOnGpu);
        }

        return fit.Level != FitLevel.WontFit && (activeBillions is not { } active || active <= new RecommenderOptions().CpuOnlyMaxActive);
    }

    public static string Describe(FitLevel level) => level switch
    {
        FitLevel.FitsInVram => "Fits in video memory",
        FitLevel.FitsWithOffload => "Fits with CPU offload",
        _ => "Will not fit",
    };
}
