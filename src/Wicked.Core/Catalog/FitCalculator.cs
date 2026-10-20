using Wicked.Core.Store;

namespace Wicked.Core.Catalog;

/// <summary>Precision of the KV cache (<c>-ctk/-ctv</c>); sets the bytes per cached element.</summary>
public enum KvCacheType
{
    F16,
    Q8_0,
    Q4_0,
}

/// <summary>
/// The attention layout a model needs for cache sizing. <c>FullLayers</c> keep a cache entry for every token of the context;
/// <c>WindowLayers</c> keep only the last <c>Window</c> tokens (sliding-window attention); the remaining layers (recurrent or
/// linear attention) keep nothing that grows. Key and value widths are per KV head; windowed layers may use narrower ones.
/// </summary>
public sealed record ModelShape(
    int Layers,
    int FullLayers,
    int WindowLayers,
    int Window,
    double KvHeads,
    int KeyLength,
    int ValueLength,
    int WindowKeyLength,
    int WindowValueLength)
{
    private static double BytesPerElement(KvCacheType type) => type switch
    {
        KvCacheType.Q8_0 => 34.0 / 32,
        KvCacheType.Q4_0 => 18.0 / 32,
        _ => 2,
    };

    /// <summary>Bytes of KV cache for <paramref name="context"/> tokens: layers × KV heads × (key + value width) × bytes × tokens held, summed over the layer kinds.</summary>
    public long KvBytes(long context, KvCacheType type = KvCacheType.F16)
    {
        var bytes = BytesPerElement(type);
        var full = FullLayers * KvHeads * (KeyLength + ValueLength) * context;
        var windowed = WindowLayers * KvHeads * (WindowKeyLength + WindowValueLength) * Math.Min(context, Window);
        return (long)((full + windowed) * bytes);
    }

    /// <summary>Reads the shape from a GGUF header summary; null when the file lacks the layer count or head sizes.</summary>
    public static ModelShape? From(GgufInfo info)
    {
        if (info.BlockCount is not { } layers || info.HeadDimension is not { } key || info.HeadCountKv is not { } kv) return null;
        var value = info.ValueLength ?? key;
        var own = Math.Max(1, layers - (info.SharedKvLayers ?? 0));
        int full = own;
        int windowed = 0;
        if (info.SlidingPattern is { Count: > 0 } pattern)
        {
            windowed = pattern.Take(own).Count(p => p);
            full = own - windowed;
        }
        else if (info.RecurrentLayers is { Count: > 0 } recurrent)
        {
            full = recurrent.Take(own).Count(r => !r);
        }
        else if (info.FullAttentionInterval is > 1 and var interval)
        {
            full = Math.Max(1, own / interval);
        }

        return new ModelShape(layers, full, windowed, info.SlidingWindow ?? 0, kv, key, value, info.KeyLengthSwa ?? key, info.ValueLengthSwa ?? value);
    }
}

public enum FitLevel
{
    FitsInVram,
    FitsWithOffload,
    WontFit,
}

/// <summary>The memory a run may use: video memory and, for layers that spill over, system memory.</summary>
public sealed record FitBudget(long VramBytes, long RamBytes);

/// <summary>
/// The verdict for one model file at one context. <c>GpuLayers</c> is the <c>-ngl</c> to use (all layers when it fits in VRAM,
/// 0 when there is no GPU memory). <c>MaxContextInVram</c> and <c>MaxContextWithOffload</c> are the largest contexts (rounded
/// down to 1024) the level allows, capped at the model's native maximum; 0 means even a minimal context does not fit.
/// </summary>
public sealed record FitResult(
    FitLevel Level,
    long WeightsBytes,
    long KvBytes,
    long RequiredBytes,
    int Context,
    int MaxContextInVram,
    int MaxContextWithOffload,
    int GpuLayers);

/// <summary>
/// Hardware fit (D9): weights + KV cache at the chosen context + 10 % headroom against video and system memory. The headroom
/// stands in for the engine's compute buffers and the desktop's own use of the card. When the engine's own
/// <c>--list-devices</c> numbers are known they are the better budget; this arithmetic is for before anything is installed.
/// </summary>
public static class FitCalculator
{
    public const double Headroom = 1.10;
    private const int Step = 1024;

    public static FitResult Evaluate(long weightsBytes, ModelShape shape, int context, FitBudget budget, int nativeContext, KvCacheType kv = KvCacheType.F16)
    {
        var kvBytes = shape.KvBytes(context, kv);
        var required = (long)((weightsBytes + kvBytes) * Headroom);
        var level = required <= budget.VramBytes ? FitLevel.FitsInVram
            : required <= budget.VramBytes + budget.RamBytes ? FitLevel.FitsWithOffload
            : FitLevel.WontFit;

        int layers;
        if (level == FitLevel.FitsInVram)
        {
            layers = shape.Layers;
        }
        else
        {
            var perLayer = (double)(weightsBytes + kvBytes) / Math.Max(1, shape.Layers) * Headroom;
            layers = perLayer <= 0 ? 0 : (int)Math.Clamp(Math.Floor(budget.VramBytes / perLayer), 0, shape.Layers);
        }

        return new FitResult(
            level, weightsBytes, kvBytes, required, context,
            LargestContext(weightsBytes, shape, budget.VramBytes, nativeContext, kv),
            LargestContext(weightsBytes, shape, budget.VramBytes + budget.RamBytes, nativeContext, kv),
            layers);
    }

    /// <summary>The largest context (a multiple of 1024) whose weights + cache + headroom fit in <paramref name="memoryBytes"/>, at most <paramref name="nativeContext"/>; 0 when nothing fits.</summary>
    public static int LargestContext(long weightsBytes, ModelShape shape, long memoryBytes, int nativeContext, KvCacheType kv = KvCacheType.F16)
    {
        bool Fits(long context) => (weightsBytes + shape.KvBytes(context, kv)) * Headroom <= memoryBytes;
        var cap = Math.Max(Step, nativeContext / Step * Step);
        if (!Fits(Step)) return 0;
        if (Fits(cap)) return cap;
        int low = 1, high = cap / Step; // multiples of Step: low fits, high does not
        while (high - low > 1)
        {
            var mid = (low + high) / 2;
            if (Fits((long)mid * Step)) low = mid;
            else high = mid;
        }

        return low * Step;
    }
}
