namespace Wicked.Core.Catalog;

/// <summary>
/// A 0-100 estimate of how close a quantisation is to the full-precision model, from published llama.cpp perplexity and
/// KL-divergence comparisons (Q8_0 is indistinguishable, Q4_K_M loses roughly one to two percent of quality, below 3 bits
/// quality falls off quickly). It orders quants of one model against each other; it is not a benchmark score.
/// </summary>
public static class QuantQuality
{
    private static readonly Dictionary<string, int> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BF16"] = 100, ["F16"] = 100, ["F32"] = 100,
        ["Q8_0"] = 99, ["Q8_K_XL"] = 99, ["Q8_K_L"] = 99,
        ["Q6_K_XL"] = 98, ["Q6_K_L"] = 98, ["Q6_K"] = 97, ["Q6_K_S"] = 97,
        ["Q5_K_XL"] = 96, ["Q5_K_L"] = 96, ["Q5_K_M"] = 95, ["Q5_K_S"] = 94,
        ["MXFP4"] = 92, ["MXFP4_MOE"] = 92,
        ["Q4_K_XL"] = 92, ["Q4_K_L"] = 91, ["Q4_K_M"] = 90, ["Q4_K_S"] = 88, ["Q4_1"] = 88,
        ["IQ4_NL"] = 88, ["IQ4_XS"] = 87, ["Q4_0"] = 85,
        ["Q3_K_XL"] = 82, ["Q3_K_L"] = 80, ["Q3_K_M"] = 77, ["IQ3_M"] = 76, ["IQ3_S"] = 74, ["Q3_K_S"] = 72, ["IQ3_XS"] = 71, ["IQ3_XXS"] = 68,
        ["Q2_K_XL"] = 62, ["Q2_K_L"] = 58, ["Q2_K"] = 55, ["IQ2_M"] = 55, ["IQ2_S"] = 50, ["IQ2_XS"] = 46, ["IQ2_XXS"] = 42,
        ["IQ1_M"] = 30, ["IQ1_S"] = 25, ["TQ1_0"] = 22,
    };

    public static int Estimate(string quant) => Table.GetValueOrDefault(quant, 50);
}
