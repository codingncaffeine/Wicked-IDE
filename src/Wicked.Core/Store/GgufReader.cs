using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Wicked.Core.Store;

/// <summary>A metadata value from a GGUF header. Scalars are held as their .NET type; short numeric arrays as <c>double[]</c>; long arrays (the tokenizer's vocabulary) only as a count.</summary>
public sealed record GgufValue(GgufType Type, object? Value, GgufType? ElementType = null, long ArrayLength = 0)
{
    public long? AsInt64() => Value switch
    {
        byte b => b,
        sbyte b => b,
        ushort u => u,
        short s => s,
        uint u => u,
        int i => i,
        ulong u when u <= long.MaxValue => (long)u,
        long l => l,
        _ => null,
    };

    public double? AsDouble() => Value switch
    {
        float f => f,
        double d => d,
        bool b => b ? 1 : 0,
        _ => AsInt64(),
    };

    public string? AsString() => Value as string;
}

public enum GgufType
{
    UInt8 = 0,
    Int8 = 1,
    UInt16 = 2,
    Int16 = 3,
    UInt32 = 4,
    Int32 = 5,
    Float32 = 6,
    Bool = 7,
    String = 8,
    Array = 9,
    UInt64 = 10,
    Int64 = 11,
    Float64 = 12,
}

/// <summary>The header of a GGUF file: version, counts and the key/value metadata. The tensor table and the weights are never read.</summary>
public sealed record GgufHeader(uint Version, ulong TensorCount, IReadOnlyDictionary<string, GgufValue> Metadata)
{
    public string? String(string key) => Metadata.TryGetValue(key, out var v) ? v.AsString() : null;

    public long? Int(string key) => Metadata.TryGetValue(key, out var v) ? v.AsInt64() : null;

    public double? Number(string key) => Metadata.TryGetValue(key, out var v) ? v.AsDouble() : null;

    /// <summary>The values of a short numeric array (per-layer head counts), or null.</summary>
    public IReadOnlyList<double>? Numbers(string key) => Metadata.TryGetValue(key, out var v) ? v.Value as double[] : null;
}

/// <summary>
/// A minimal GGUF metadata reader (format version 2 and 3, little-endian): the magic, the counts, then each key/value pair of
/// the documented types. It reads the header only, with hard limits on every length so a corrupt or hostile file cannot make it
/// allocate gigabytes or loop for long.
/// </summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF" read as little-endian
    private const int MaxKeyValues = 100_000;
    private const int MaxStringBytes = 64 * 1024 * 1024;
    private const long MaxKeptArray = 65_536;

    public static async Task<GgufHeader> ReadHeaderAsync(string path, CancellationToken ct = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var stream = new BufferedStream(file, 64 * 1024);
        return await ReadHeaderAsync(stream, ct).ConfigureAwait(false);
    }

    public static async Task<GgufHeader> ReadHeaderAsync(Stream stream, CancellationToken ct = default)
    {
        var reader = new Reader(stream, ct);
        if (await reader.U32().ConfigureAwait(false) != Magic) throw new InvalidDataException("Not a GGUF file (bad magic)");
        var version = await reader.U32().ConfigureAwait(false);
        if (version is 0 or > 0xFFFF) throw new InvalidDataException("Big-endian or unsupported GGUF file");
        if (version == 1) throw new InvalidDataException("GGUF version 1 is not supported");
        var tensors = await reader.U64().ConfigureAwait(false);
        var count = await reader.U64().ConfigureAwait(false);
        if (count > MaxKeyValues) throw new InvalidDataException($"Implausible metadata count {count}");

        var metadata = new Dictionary<string, GgufValue>((int)count, StringComparer.Ordinal);
        for (ulong i = 0; i < count; i++)
        {
            var key = await reader.String().ConfigureAwait(false);
            var type = (GgufType)await reader.U32().ConfigureAwait(false);
            metadata[key] = await ReadValueAsync(reader, type, allowArray: true).ConfigureAwait(false);
        }

        return new GgufHeader(version, tensors, metadata);
    }

    private static async Task<GgufValue> ReadValueAsync(Reader r, GgufType type, bool allowArray)
    {
        switch (type)
        {
            case GgufType.UInt8: return new(type, (await r.Bytes(1).ConfigureAwait(false))[0]);
            case GgufType.Int8: return new(type, (sbyte)(await r.Bytes(1).ConfigureAwait(false))[0]);
            case GgufType.UInt16: return new(type, BinaryPrimitives.ReadUInt16LittleEndian(await r.Bytes(2).ConfigureAwait(false)));
            case GgufType.Int16: return new(type, BinaryPrimitives.ReadInt16LittleEndian(await r.Bytes(2).ConfigureAwait(false)));
            case GgufType.UInt32: return new(type, BinaryPrimitives.ReadUInt32LittleEndian(await r.Bytes(4).ConfigureAwait(false)));
            case GgufType.Int32: return new(type, BinaryPrimitives.ReadInt32LittleEndian(await r.Bytes(4).ConfigureAwait(false)));
            case GgufType.Float32: return new(type, BinaryPrimitives.ReadSingleLittleEndian(await r.Bytes(4).ConfigureAwait(false)));
            case GgufType.Bool: return new(type, (await r.Bytes(1).ConfigureAwait(false))[0] != 0);
            case GgufType.String: return new(type, await r.String().ConfigureAwait(false));
            case GgufType.UInt64: return new(type, BinaryPrimitives.ReadUInt64LittleEndian(await r.Bytes(8).ConfigureAwait(false)));
            case GgufType.Int64: return new(type, BinaryPrimitives.ReadInt64LittleEndian(await r.Bytes(8).ConfigureAwait(false)));
            case GgufType.Float64: return new(type, BinaryPrimitives.ReadDoubleLittleEndian(await r.Bytes(8).ConfigureAwait(false)));
            case GgufType.Array when allowArray:
                var element = (GgufType)await r.U32().ConfigureAwait(false);
                var length = await r.U64().ConfigureAwait(false);
                if (length > long.MaxValue) throw new InvalidDataException("Implausible array length");
                var numeric = element is not (GgufType.String or GgufType.Array);
                var keep = numeric && (long)length <= MaxKeptArray;
                var kept = keep ? new double[length] : null;
                for (long i = 0; i < (long)length; i++)
                {
                    var item = await ReadValueAsync(r, element, allowArray: false).ConfigureAwait(false);
                    if (kept is not null) kept[i] = item.AsDouble() ?? 0;
                }

                return new(type, kept, element, (long)length);
            default:
                throw new InvalidDataException($"Unknown GGUF value type {(int)type}");
        }
    }

    /// <summary>Exact-length reads over a stream, with a reused small buffer for scalars.</summary>
    private sealed class Reader(Stream stream, CancellationToken ct)
    {
        private readonly byte[] _small = new byte[8];

        public async Task<byte[]> Bytes(int count)
        {
            await stream.ReadExactlyAsync(_small.AsMemory(0, count), ct).ConfigureAwait(false);
            return _small;
        }

        public async Task<uint> U32() => BinaryPrimitives.ReadUInt32LittleEndian(await Bytes(4).ConfigureAwait(false));

        public async Task<ulong> U64() => BinaryPrimitives.ReadUInt64LittleEndian(await Bytes(8).ConfigureAwait(false));

        public async Task<string> String()
        {
            var length = await U64().ConfigureAwait(false);
            if (length > MaxStringBytes) throw new InvalidDataException($"Implausible string length {length}");
            if (length == 0) return "";
            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            return Encoding.UTF8.GetString(buffer);
        }
    }
}

/// <summary>
/// The facts about a model that fit and listing code need, taken from the well-known GGUF keys. <c>HeadCountKv</c> is the
/// average number of key/value heads per layer (grouped-query attention): exact when the file gives one number, a mean when
/// it gives one per layer. The attention layout fields describe models whose layers do not all keep a full-context cache:
/// <c>SlidingPattern</c> (true = windowed layer, as Gemma writes it), <c>RecurrentLayers</c> (true = no KV cache, linear
/// attention), <c>FullAttentionInterval</c> (every Nth layer is full attention, as Qwen's hybrids write it) and
/// <c>SharedKvLayers</c> (trailing layers that reuse earlier layers' cache).
/// </summary>
public sealed record GgufInfo(
    string? Architecture,
    string? Name,
    long? ContextLength,
    int? BlockCount,
    int? EmbeddingLength,
    int? HeadCount,
    double? HeadCountKv,
    int? KeyLength,
    int? ValueLength,
    int? ExpertCount,
    int? ExpertUsedCount,
    int? SlidingWindow,
    long? FileType,
    IReadOnlyList<bool>? SlidingPattern = null,
    IReadOnlyList<bool>? RecurrentLayers = null,
    int? FullAttentionInterval = null,
    int? SharedKvLayers = null,
    int? KeyLengthSwa = null,
    int? ValueLengthSwa = null,
    bool? HasChatTemplate = null)
{
    public static GgufInfo From(GgufHeader h)
    {
        var arch = h.String("general.architecture");
        long? Get(string suffix) => arch is null ? null : h.Int($"{arch}.{suffix}");
        int? GetInt(string suffix) => Get(suffix) is { } v ? (int)v : null;
        IReadOnlyList<bool>? Flags(string suffix) =>
            arch is not null && h.Numbers($"{arch}.{suffix}") is { Count: > 0 } n ? [.. n.Select(x => x != 0)] : null;

        double? kv = null;
        if (arch is not null)
        {
            if (h.Numbers($"{arch}.attention.head_count_kv") is { Count: > 0 } perLayer) kv = perLayer.Average();
            else kv = h.Number($"{arch}.attention.head_count_kv");
        }

        double? heads = null;
        if (arch is not null)
        {
            heads = h.Numbers($"{arch}.attention.head_count") is { Count: > 0 } hl ? hl.Max() : h.Number($"{arch}.attention.head_count");
        }

        // No separate key/value head count means plain multi-head attention: one per head.
        kv ??= heads;

        return new GgufInfo(
            arch,
            h.String("general.name"),
            Get("context_length"),
            GetInt("block_count"),
            GetInt("embedding_length"),
            heads is { } hc ? (int)hc : null,
            kv,
            GetInt("attention.key_length"),
            GetInt("attention.value_length"),
            GetInt("expert_count"),
            GetInt("expert_used_count"),
            GetInt("attention.sliding_window"),
            h.Int("general.file_type"),
            Flags("attention.sliding_window_pattern"),
            Flags("attention.recurrent_layers"),
            GetInt("full_attention_interval"),
            GetInt("attention.shared_kv_layers"),
            GetInt("attention.key_length_swa"),
            GetInt("attention.value_length_swa"),
            h.Metadata.ContainsKey("tokenizer.chat_template"));
    }

    /// <summary>Key (and value) width per head: stated in the file, else the embedding width divided by the head count.</summary>
    public int? HeadDimension => KeyLength ?? (EmbeddingLength is { } e && HeadCount is > 0 and var h ? e / h : null);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Architecture} ctx={ContextLength} layers={BlockCount} kvHeads={HeadCountKv}");
}
