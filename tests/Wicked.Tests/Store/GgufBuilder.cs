using System.Buffers.Binary;
using System.Text;
using Wicked.Core.Store;

namespace Wicked.Tests;

/// <summary>Writes small GGUF files (header only, plus optional filler standing in for tensors) so the reader and the store can be tested without real models.</summary>
internal sealed class GgufBuilder
{
    private readonly List<(string Key, GgufType Type, object Value, GgufType? Element)> _entries = [];

    public GgufBuilder Str(string key, string value) => Add(key, GgufType.String, value);

    public GgufBuilder U32(string key, uint value) => Add(key, GgufType.UInt32, value);

    public GgufBuilder U64(string key, ulong value) => Add(key, GgufType.UInt64, value);

    public GgufBuilder F32(string key, float value) => Add(key, GgufType.Float32, value);

    public GgufBuilder Bool(string key, bool value) => Add(key, GgufType.Bool, value);

    public GgufBuilder I32Array(string key, params int[] values) => Add(key, GgufType.Array, values, GgufType.Int32);

    public GgufBuilder StringArray(string key, params string[] values) => Add(key, GgufType.Array, values, GgufType.String);

    public GgufBuilder Llama(string arch = "llama", uint context = 8192, uint layers = 32, uint embedding = 4096, uint heads = 32, uint kvHeads = 8) =>
        Str("general.architecture", arch).Str("general.name", "Test " + arch)
            .U32($"{arch}.context_length", context).U32($"{arch}.block_count", layers).U32($"{arch}.embedding_length", embedding)
            .U32($"{arch}.attention.head_count", heads).U32($"{arch}.attention.head_count_kv", kvHeads).U32("general.file_type", 15);

    private GgufBuilder Add(string key, GgufType type, object value, GgufType? element = null)
    {
        _entries.Add((key, type, value, element));
        return this;
    }

    public byte[] Build(uint version = 3, int fillerBytes = 0)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(Encoding.ASCII.GetBytes("GGUF"));
        w.Write(version);
        w.Write(0UL);
        w.Write((ulong)_entries.Count);
        foreach (var (key, type, value, element) in _entries)
        {
            WriteString(w, key);
            w.Write((uint)type);
            WriteValue(w, type, value, element);
        }

        w.Write(new byte[fillerBytes]);
        return ms.ToArray();
    }

    public async Task WriteAsync(string path, int fillerBytes = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, Build(fillerBytes: fillerBytes));
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    private static void WriteValue(BinaryWriter w, GgufType type, object value, GgufType? element)
    {
        switch (type)
        {
            case GgufType.String: WriteString(w, (string)value); break;
            case GgufType.UInt32: w.Write((uint)value); break;
            case GgufType.UInt64: w.Write((ulong)value); break;
            case GgufType.Float32: w.Write((float)value); break;
            case GgufType.Bool: w.Write((byte)((bool)value ? 1 : 0)); break;
            case GgufType.Array:
                w.Write((uint)element!);
                if (value is int[] ints)
                {
                    w.Write((ulong)ints.Length);
                    foreach (var i in ints) w.Write(i);
                }
                else
                {
                    var strings = (string[])value;
                    w.Write((ulong)strings.Length);
                    foreach (var s in strings) WriteString(w, s);
                }

                break;
            default: throw new NotSupportedException(type.ToString());
        }
    }

    public static byte[] BigEndianVersion()
    {
        var bytes = new GgufBuilder().Llama().Build();
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4), 3);
        return bytes;
    }
}
