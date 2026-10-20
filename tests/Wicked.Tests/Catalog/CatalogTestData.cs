using Wicked.Core.Catalog;
using Wicked.Core.Hardware;

namespace Wicked.Tests;

/// <summary>A hand-built catalog and machines for the recommender's expectation table, so the expectations do not move when the real catalog is refreshed.</summary>
internal static class CatalogTestData
{
    public const long Gib = 1L << 30;

    // 32 layers, 8 KV heads of 128+128 = 128 KiB of cache per token.
    public static readonly ModelShape Dense = new(32, 32, 0, 0, 8, 128, 128, 128, 128);

    // 48 layers, 4 KV heads = 96 KiB per token.
    public static readonly ModelShape Moe = new(48, 48, 0, 0, 4, 128, 128, 128, 128);

    // 24 layers, 4 KV heads = 48 KiB per token.
    public static readonly ModelShape Small = new(24, 24, 0, 0, 4, 128, 128, 128, 128);

    public static QuantTier Tier(string quant, double gib) =>
        new(quant, [new CatalogFile(quant + ".gguf", (long)(gib * Gib), new string('a', 64))], QuantQuality.Estimate(quant));

    public static CatalogModel Model(string id, DateOnly released, RoleScores scores, ModelShape shape, ParameterCount parameters, Capability[] caps, int context, params QuantTier[] tiers) =>
        new(id, id, "test/" + id, "rev", "apache-2.0", null, false, caps, context, parameters, released, scores, [], shape, tiers);

    private static readonly Capability[] Chat = [Capability.Chat, Capability.Tools];

    public static ModelCatalog Catalog() => new(1, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero),
    [
        new CatalogFamily("test", "Test", "Tests", null,
        [
            Model("big", new(2026, 8, 1), new(Everyday: 90, Coding: 88), Dense, new(32), Chat, 131072, Tier("Q8_0", 34), Tier("Q6_K", 26), Tier("Q4_K_M", 18)),
            Model("mid", new(2026, 3, 1), new(Everyday: 80, Coding: 75), Dense, new(14), Chat, 131072, Tier("Q8_0", 14), Tier("Q6_K", 11), Tier("Q4_K_M", 8)),
            Model("mid-new", new(2026, 6, 1), new(Everyday: 80, Coding: 75), Dense, new(14), Chat, 131072, Tier("Q8_0", 14), Tier("Q6_K", 11), Tier("Q4_K_M", 8)),
            Model("coder-moe", new(2026, 4, 1), new(Everyday: 60, Coding: 85, Fast: 70, Fim: 82), Moe, new(30, 3), [Capability.Chat, Capability.Fim], 131072, Tier("Q8_0", 31), Tier("Q6_K", 24), Tier("Q4_K_M", 17)),
            Model("small", new(2026, 5, 1), new(Everyday: 55, Coding: 45, Fast: 85), Small, new(4), Chat, 131072, Tier("Q8_0", 4.2), Tier("Q6_K", 3.2), Tier("Q4_K_M", 2.5)),
            Model("tiny", new(2026, 5, 1), new(Everyday: 40, Coding: 30, Fast: 92), Small, new(1.7), Chat, 131072, Tier("Q8_0", 1.8), Tier("Q6_K", 1.4), Tier("Q4_K_M", 1.1)),
            Model("fim-small", new(2025, 1, 1), new(Fim: 60), Small, new(1.5), [Capability.Fim], 32768, Tier("Q8_0", 1.6)),
            Model("fim-mid", new(2025, 1, 1), new(Fim: 75), Small, new(7), [Capability.Fim], 32768, Tier("Q8_0", 8)),
        ]),
    ]);

    public static HardwareInfo Machine(double? vramGib, long ramGib, GpuVendor vendor = GpuVendor.Nvidia) =>
        new(new CpuInfo("Test CPU", 16, true, false), ramGib * Gib, ramGib * Gib / 2,
            vramGib is { } v ? [new GpuInfo(vendor, "Test GPU", (long)(v * Gib), (long)(v * Gib), "1", vendor == GpuVendor.Nvidia ? new Version(13, 4) : null, null)] : []);
}
