using Wicked.Core.Store;

namespace Wicked.Tests;

public sealed class GgufReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadsScalarsStringsAndArrays()
    {
        var bytes = new GgufBuilder()
            .Llama(context: 131072, layers: 48, embedding: 5120, heads: 40, kvHeads: 8)
            .Bool("tokenizer.ggml.add_bos_token", true)
            .F32("llama.rope.freq_base", 500000f)
            .U64("general.parameter_count", 8_030_000_000UL)
            .I32Array("llama.attention.head_count_arr", 8, 8, 4)
            .StringArray("tokenizer.ggml.tokens", "<s>", "</s>", "hello")
            .Build(fillerBytes: 1000);

        var header = await GgufReader.ReadHeaderAsync(new MemoryStream(bytes), Ct);

        Assert.Equal(3u, header.Version);
        Assert.Equal("llama", header.String("general.architecture"));
        Assert.Equal(131072, header.Int("llama.context_length"));
        Assert.Equal(8_030_000_000L, header.Int("general.parameter_count"));
        Assert.Equal(500000d, header.Number("llama.rope.freq_base"));
        Assert.Equal(true, header.Metadata["tokenizer.ggml.add_bos_token"].Value);
        Assert.Equal([8d, 8d, 4d], header.Numbers("llama.attention.head_count_arr"));
        var tokens = header.Metadata["tokenizer.ggml.tokens"];
        Assert.Equal(3, tokens.ArrayLength);
        Assert.Null(tokens.Value);
    }

    [Fact]
    public async Task SummarisesTheShapeForFitMath()
    {
        var header = await GgufReader.ReadHeaderAsync(new MemoryStream(new GgufBuilder().Llama(context: 32768, layers: 40, embedding: 5120, heads: 40, kvHeads: 8).Build()), Ct);

        var info = GgufInfo.From(header);

        Assert.Equal("llama", info.Architecture);
        Assert.Equal(32768, info.ContextLength);
        Assert.Equal(40, info.BlockCount);
        Assert.Equal(8, info.HeadCountKv);
        Assert.Equal(128, info.HeadDimension);
        Assert.Equal(15, info.FileType);
    }

    [Fact]
    public async Task PerLayerKvHeadsAreAveraged()
    {
        var builder = new GgufBuilder().Str("general.architecture", "hybrid").U32("hybrid.block_count", 4).I32Array("hybrid.attention.head_count_kv", 8, 0, 8, 0);

        var info = GgufInfo.From(await GgufReader.ReadHeaderAsync(new MemoryStream(builder.Build()), Ct));

        Assert.Equal(4d, info.HeadCountKv);
    }

    [Fact]
    public async Task MissingKvHeadsMeansOnePerHead()
    {
        var builder = new GgufBuilder().Str("general.architecture", "llama").U32("llama.attention.head_count", 6);

        var info = GgufInfo.From(await GgufReader.ReadHeaderAsync(new MemoryStream(builder.Build()), Ct));

        Assert.Equal(6d, info.HeadCountKv);
    }

    [Fact]
    public async Task SaysWhetherTheFileCarriesAChatTemplate()
    {
        var with = new GgufBuilder().Str("general.architecture", "qwen2").Str("tokenizer.chat_template", "{{ messages }}");
        var without = new GgufBuilder().Str("general.architecture", "qwen2");

        Assert.True(GgufInfo.From(await GgufReader.ReadHeaderAsync(new MemoryStream(with.Build()), Ct)).HasChatTemplate);
        Assert.False(GgufInfo.From(await GgufReader.ReadHeaderAsync(new MemoryStream(without.Build()), Ct)).HasChatTemplate);
    }

    [Fact]
    public async Task ReadsOnlyTheHeaderOfAFile()
    {
        using var dir = new TempProfile();
        var path = Path.Combine(dir.Root, "m.gguf");
        await new GgufBuilder().Llama().WriteAsync(path, fillerBytes: 5_000_000);

        var header = await GgufReader.ReadHeaderAsync(path, Ct);

        Assert.Equal("llama", header.String("general.architecture"));
    }

    [Fact]
    public async Task RejectsJunkAndTruncation()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => GgufReader.ReadHeaderAsync(new MemoryStream("not a gguf file at all"u8.ToArray()), Ct));
        var good = new GgufBuilder().Llama().Build();
        await Assert.ThrowsAsync<EndOfStreamException>(() => GgufReader.ReadHeaderAsync(new MemoryStream(good[..40]), Ct));
        await Assert.ThrowsAsync<InvalidDataException>(() => GgufReader.ReadHeaderAsync(new MemoryStream(new GgufBuilder().Llama().Build(version: 1)), Ct));
    }

    [Fact]
    public async Task RejectsAHostileStringLengthWithoutAllocatingIt()
    {
        var bytes = new GgufBuilder().Llama().Build();
        // Overwrite the first key's length (offset 24) with 2^40.
        BitConverter.GetBytes(1UL << 40).CopyTo(bytes, 24);

        await Assert.ThrowsAsync<InvalidDataException>(() => GgufReader.ReadHeaderAsync(new MemoryStream(bytes), Ct));
    }
}
