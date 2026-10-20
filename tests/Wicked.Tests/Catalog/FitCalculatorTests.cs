using Wicked.Core.Catalog;
using Wicked.Core.Store;

namespace Wicked.Tests;

public sealed class FitCalculatorTests
{
    private const long Gib = 1L << 30;

    [Fact]
    public void KvCacheIsLayersTimesHeadsTimesWidthTimesBytesTimesContext()
    {
        // 32 layers x 8 heads x (128 + 128) x 2 bytes x 8192 tokens = exactly 1 GiB.
        Assert.Equal(Gib, CatalogTestData.Dense.KvBytes(8192));
        Assert.Equal(Gib * 34 / 64, CatalogTestData.Dense.KvBytes(8192, KvCacheType.Q8_0));
        Assert.Equal(Gib * 18 / 64, CatalogTestData.Dense.KvBytes(8192, KvCacheType.Q4_0));
    }

    [Fact]
    public void WindowedLayersStopGrowingAtTheirWindow()
    {
        var shape = new ModelShape(Layers: 10, FullLayers: 2, WindowLayers: 8, Window: 1024, KvHeads: 2, KeyLength: 256, ValueLength: 256, WindowKeyLength: 128, WindowValueLength: 128);

        // Full: 2 x 2 x 512 x 2 = 4096 B per token. Window: 8 x 2 x 256 x 2 = 8192 B per token, up to 1024 tokens.
        Assert.Equal(4096L * 100 + 8192L * 100, shape.KvBytes(100));
        Assert.Equal(4096L * 100_000 + 8192L * 1024, shape.KvBytes(100_000));
    }

    [Fact]
    public void ThreeLevelsFollowTheMemory()
    {
        // 10 GiB of weights + 2 GiB cache at 16k, times 1.1 = 13.2 GiB.
        long weights = 10 * Gib;
        var shape = CatalogTestData.Dense;

        Assert.Equal(FitLevel.FitsInVram, FitCalculator.Evaluate(weights, shape, 16384, new(14 * Gib, 0), 131072).Level);
        Assert.Equal(FitLevel.FitsWithOffload, FitCalculator.Evaluate(weights, shape, 16384, new(8 * Gib, 8 * Gib), 131072).Level);
        Assert.Equal(FitLevel.WontFit, FitCalculator.Evaluate(weights, shape, 16384, new(8 * Gib, 4 * Gib), 131072).Level);
        Assert.Equal((long)(12 * Gib * 1.1), FitCalculator.Evaluate(weights, shape, 16384, new(14 * Gib, 0), 131072).RequiredBytes);
    }

    [Fact]
    public void GpuLayersScaleWithWhatFits()
    {
        // 13.2 GiB needed over 32 layers = 0.4125 GiB a layer; 6 GiB of VRAM holds 14.
        var result = FitCalculator.Evaluate(10 * Gib, CatalogTestData.Dense, 16384, new(6 * Gib, 20 * Gib), 131072);

        Assert.Equal(FitLevel.FitsWithOffload, result.Level);
        Assert.Equal(14, result.GpuLayers);
        Assert.Equal(32, FitCalculator.Evaluate(10 * Gib, CatalogTestData.Dense, 16384, new(20 * Gib, 0), 131072).GpuLayers);
        Assert.Equal(0, FitCalculator.Evaluate(10 * Gib, CatalogTestData.Dense, 16384, new(0, 20 * Gib), 131072).GpuLayers);
    }

    [Fact]
    public void LargestContextIsTheEdgeOfWhatFits()
    {
        long weights = 10 * Gib;
        var shape = CatalogTestData.Dense;
        var memory = 16 * Gib;

        var context = FitCalculator.LargestContext(weights, shape, memory, 131072);

        Assert.Equal(0, context % 1024);
        Assert.True((weights + shape.KvBytes(context)) * 1.1 <= memory);
        Assert.True((weights + shape.KvBytes(context + 1024)) * 1.1 > memory);
        // (16 / 1.1 - 10) GiB / 128 KiB = 36.4k tokens.
        Assert.Equal(36 * 1024, context);
    }

    [Fact]
    public void LargestContextIsCappedByTheModelAndZeroWhenNothingFits()
    {
        Assert.Equal(32768, FitCalculator.LargestContext(1 * Gib, CatalogTestData.Dense, 100 * Gib, 32768));
        Assert.Equal(0, FitCalculator.LargestContext(20 * Gib, CatalogTestData.Dense, 16 * Gib, 131072));
        Assert.Equal(0, FitCalculator.LargestContext(10 * Gib, CatalogTestData.Dense, 0, 131072));
    }

    [Fact]
    public void CacheTypeBuysContext()
    {
        var f16 = FitCalculator.LargestContext(10 * Gib, CatalogTestData.Dense, 16 * Gib, 262144);
        var q8 = FitCalculator.LargestContext(10 * Gib, CatalogTestData.Dense, 16 * Gib, 262144, KvCacheType.Q8_0);

        Assert.True(q8 > f16 * 18 / 10, $"{q8} vs {f16}");
    }

    [Fact]
    public void ShapeComesFromTheGgufHeader()
    {
        ModelShape? From(GgufBuilder b)
        {
            var header = GgufReader.ReadHeaderAsync(new MemoryStream(b.Build()), TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            return ModelShape.From(GgufInfo.From(header));
        }

        var llama = From(new GgufBuilder().Llama(layers: 32, embedding: 4096, heads: 32, kvHeads: 8))!;
        Assert.Equal((32, 32, 0), (llama.Layers, llama.FullLayers, llama.WindowLayers));
        Assert.Equal(8, llama.KvHeads);
        Assert.Equal(128, llama.KeyLength);

        // Qwen-style hybrid: every 4th layer is full attention.
        var hybrid = From(new GgufBuilder().Llama("qwen35", layers: 64, embedding: 5120, heads: 24, kvHeads: 4).U32("qwen35.full_attention_interval", 4).U32("qwen35.attention.key_length", 256))!;
        Assert.Equal(16, hybrid.FullLayers);
        Assert.Equal(256, hybrid.KeyLength);

        Assert.Null(From(new GgufBuilder().Str("general.architecture", "llama")));
    }

    [Fact]
    public async Task SlidingPatternAndSharedLayersShapeTheCache()
    {
        var builder = new GgufBuilder().Llama("gemma4", layers: 6, embedding: 1024, heads: 4, kvHeads: 2)
            .U32("gemma4.attention.key_length", 512).U32("gemma4.attention.sliding_window", 512).U32("gemma4.attention.shared_kv_layers", 2)
            .BoolArray("gemma4.attention.sliding_window_pattern", true, true, true, false, true, false);
        var header = await GgufReader.ReadHeaderAsync(new MemoryStream(builder.Build()), TestContext.Current.CancellationToken);

        var shape = ModelShape.From(GgufInfo.From(header))!;

        // 6 layers, the last 2 share a cache: of the 4 that own one, 3 are windowed and 1 is global.
        Assert.Equal((6, 1, 3, 512), (shape.Layers, shape.FullLayers, shape.WindowLayers, shape.Window));
    }
}
