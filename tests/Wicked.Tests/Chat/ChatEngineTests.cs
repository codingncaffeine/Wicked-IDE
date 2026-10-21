using System.Diagnostics;
using System.Text.Json;
using Wicked.Core.Chat;

namespace Wicked.Tests.Chat;

public sealed class ChatEngineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ChatRequest Ask(string text = "hi", string model = RuntimeRig.ChatId, ChatSampling? sampling = null) =>
        new(model, [new ChatMessage(ChatRole.User, text)], sampling);

    private static async Task<List<ChatChunk>> CollectAsync(IAsyncEnumerable<ChatChunk> stream)
    {
        var chunks = new List<ChatChunk>();
        await foreach (var chunk in stream) chunks.Add(chunk);
        return chunks;
    }

    private static string Chunk(string? content = null, string? reasoning = null, string? finish = null)
    {
        var delta = new Dictionary<string, object?>();
        if (content is not null) delta["content"] = content;
        if (reasoning is not null) delta["reasoning_content"] = reasoning;
        return JsonSerializer.Serialize(new { choices = new[] { new { index = 0, delta, finish_reason = finish } } });
    }

    private const string UsageChunk = """{"choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15},"timings":{"predicted_per_second":42.5,"prompt_n":12}}""";

    [Fact]
    public async Task ARepliesDeltasUsageAndFinishComeInTheContractsOrder()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new
        {
            chat = new[]
            {
                Chunk(reasoning: "Let me think. "), Chunk(reasoning: "Done."), Chunk(content: "Hel"), Chunk(content: "lo é中"), Chunk(content: ""),
                Chunk(finish: "stop"), UsageChunk, "[DONE]",
            },
        });

        var chunks = await CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct));

        Assert.Equal(
            [new ReasoningDelta("Let me think. "), new ReasoningDelta("Done."), new ContentDelta("Hel"), new ContentDelta("lo é中")],
            chunks.Take(4));
        Assert.Equal(new ChatUsage(12, 3, 42.5, 4096), chunks[4]);
        Assert.Equal(new ChatFinished("stop"), chunks[5]);
        Assert.Equal(6, chunks.Count);
    }

    [Fact]
    public async Task AReplyWithoutUsageOrSpeedStillFinishes()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chat = new[] { Chunk(content: "a"), Chunk(finish: "length"), "[DONE]" } });

        var chunks = await CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct));

        Assert.Equal<ChatChunk>([new ContentDelta("a"), new ChatFinished("length")], chunks);
    }

    [Fact]
    public async Task TheRequestIsTheOpenAiShapeWithImagesAsDataUrisAndTheModelIsLoadedFirst()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chat = new[] { Chunk(content: "ok"), Chunk(finish: "stop"), "[DONE]" } });
        var request = new ChatRequest(
            RuntimeRig.ChatId,
            [
                new ChatMessage(ChatRole.System, "Be brief."),
                new ChatMessage(ChatRole.User, "What is this?", [new ChatImage("image/png", [1, 2, 3]), new ChatImage("image/jpeg", [255])]),
                new ChatMessage(ChatRole.Assistant, "A thing.", Reasoning: "never sent back"),
                new ChatMessage(ChatRole.User, "Thanks"),
            ],
            new ChatSampling(Temperature: 0.2, TopP: 0.9, MaxTokens: 64));

        await CollectAsync(rig.Services.Chat.StreamAsync(request, Ct));

        var log = rig.FakeLog();
        Assert.True(log.FindIndex(e => e.GetProperty("t").GetString() == "load") < log.FindIndex(e => e.GetProperty("t").GetString() == "chat"));
        var body = Assert.Single(rig.Logged("chat")).GetProperty("body");
        Assert.Equal(RuntimeRig.ChatId, body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.True(body.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal(0.2, body.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, body.GetProperty("top_p").GetDouble());
        Assert.Equal(64, body.GetProperty("max_tokens").GetInt32());
        var messages = body.GetProperty("messages");
        Assert.Equal(["system", "user", "assistant", "user"], messages.EnumerateArray().Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("Be brief.", messages[0].GetProperty("content").GetString());
        var parts = messages[1].GetProperty("content");
        Assert.Equal("text", parts[0].GetProperty("type").GetString());
        Assert.Equal("What is this?", parts[0].GetProperty("text").GetString());
        Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("data:image/jpeg;base64,/w==", parts[2].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("A thing.", messages[2].GetProperty("content").GetString());
        Assert.DoesNotContain("never sent back", body.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutSamplingTheRequestLeavesItToTheModel()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chat = new[] { Chunk(finish: "stop"), "[DONE]" } });

        await CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct));

        var body = Assert.Single(rig.Logged("chat")).GetProperty("body");
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("max_tokens", out _));
    }

    [Fact]
    public async Task CancellingClosesTheConnectionSoTheEngineStopsAndEndsWithCancelled()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chatDelayMs = 30, chat = Enumerable.Range(0, 400).Select(i => Chunk(content: $"w{i} ")).Append(Chunk(finish: "stop")).Append("[DONE]").ToArray() });
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var chunks = new List<ChatChunk>();

        await foreach (var chunk in rig.Services.Chat.StreamAsync(Ask(), cts.Token))
        {
            chunks.Add(chunk);
            if (chunks.Count == 3) await cts.CancelAsync();
        }

        Assert.IsType<ContentDelta>(chunks[0]);
        Assert.Equal(new ChatFinished("cancelled"), chunks[^1]);
        Assert.Single(chunks, c => c is ChatFinished);
        Assert.True(chunks.Count < 50, "the stream kept going after the cancel");
        await RuntimeRig.WaitUntilAsync(() => rig.Logged("chat-aborted").Count == 1); // the engine saw the connection close
    }

    [Fact]
    public async Task CancellingBeforeTheModelHasLoadedEndsCleanlyToo()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { loadMs = 3000 });
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var chunks = await CollectAsync(rig.Services.Chat.StreamAsync(Ask(), cts.Token));

        Assert.Equal<ChatChunk>([new ChatFinished("cancelled")], chunks);
    }

    [Fact]
    public async Task AnHttpErrorBecomesAChatExceptionWithTheEnginesMessage()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chatStatus = 500, chatBody = new { error = new { code = 500, message = "the context is full", type = "server_error" } } });

        var ex = await Assert.ThrowsAsync<ChatException>(() => CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct)));

        Assert.Equal("the context is full", ex.Message);
    }

    [Fact]
    public async Task AnErrorEventInTheStreamBecomesAChatException()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chat = new[] { Chunk(content: "par"), """{"error":{"message":"decode failed","code":500}}""" } });
        var seen = new List<ChatChunk>();

        var ex = await Assert.ThrowsAsync<ChatException>(async () =>
        {
            await foreach (var c in rig.Services.Chat.StreamAsync(Ask(), Ct)) seen.Add(c);
        });

        Assert.Equal("decode failed", ex.Message);
        Assert.Equal<ChatChunk>([new ContentDelta("par")], seen);
    }

    [Fact]
    public async Task AModelThatFailsToLoadIsAChatExceptionWithTheRouterLogTail()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { failLoad = new[] { RuntimeRig.ChatId } });

        var ex = await Assert.ThrowsAsync<ChatException>(() => CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct)));

        Assert.Contains("fake failure", ex.Message, StringComparison.Ordinal);
        Assert.Empty(rig.Logged("chat"));
    }

    [Fact]
    public async Task AnUnknownModelIsAChatException()
    {
        await using var rig = await RuntimeRig.StartAsync();

        var ex = await Assert.ThrowsAsync<ChatException>(() => CollectAsync(rig.Services.Chat.StreamAsync(Ask(model: "nobody:Q4"), Ct)));

        Assert.Contains("not an installed model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineThatDiesInTheMiddleOfAReplyIsAChatExceptionAndTheRuntimeRecovers()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chat = new[] { Chunk(content: "start"), "@crash" } });
        var seen = new List<ChatChunk>();

        var ex = await Assert.ThrowsAsync<ChatException>(async () =>
        {
            await foreach (var c in rig.Services.Chat.StreamAsync(Ask(), Ct)) seen.Add(c);
        });

        Assert.Contains("model engine", ex.Message, StringComparison.Ordinal); // "stopped in the middle" or "closed the connection", by how the socket went
        Assert.Equal<ChatChunk>([new ContentDelta("start")], seen);

        // The supervisor brings the router back, and the next chat loads the model again and works.
        await RuntimeRig.WaitUntilAsync(() => rig.Logged("start").Count == 2);
        rig.Script(new { chat = new[] { Chunk(content: "again"), Chunk(finish: "stop"), "[DONE]" } });
        var chunks = await CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct));
        Assert.Equal(new ContentDelta("again"), chunks[0]);
        Assert.Equal(2, rig.Logged("start").Count);
    }

    [Fact]
    public async Task AStreamThatEndsWithoutAFinishReasonIsAChatException()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chat = new[] { Chunk(content: "half"), "@hangup" } });

        var ex = await Assert.ThrowsAsync<ChatException>(() => CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct)));

        Assert.Contains("model engine", ex.Message, StringComparison.Ordinal); // closed early, or the socket broke: both say the engine stopped
    }

    [Fact]
    public async Task ADeadRouterIsAChatExceptionNotAHang()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { chat = new[] { Chunk(finish: "stop"), "[DONE]" } });
        await CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct)); // started and working
        await rig.Runtime.SetModelsMaxAsync(1, Ct); // stops it; the next chat starts a new one

        var watch = Stopwatch.StartNew();
        var chunks = await CollectAsync(rig.Services.Chat.StreamAsync(Ask(), Ct));

        Assert.IsType<ChatFinished>(chunks[^1]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        Assert.Equal(2, rig.Logged("start").Count);
    }

    [Fact]
    public void ParseReadsTheFieldsOfOneEventAndRefusesGarbage()
    {
        var parsed = ChatEngine.Parse("""{"choices":[{"delta":{"role":"assistant","content":"x","reasoning_content":"y"},"finish_reason":"stop"}]}""", 100);
        Assert.Equal(new ContentDelta("x"), parsed.Delta);
        Assert.Equal(new ReasoningDelta("y"), parsed.Reasoning);
        Assert.Equal("stop", parsed.FinishReason);
        Assert.Null(parsed.Usage);

        Assert.Equal(new ChatUsage(1, 2, null, 100), ChatEngine.Parse("""{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":2}}""", 100).Usage);
        Assert.Null(ChatEngine.Parse("[1,2]", null).Delta);
        Assert.Throws<ChatException>(() => ChatEngine.Parse("{not json", null));
        Assert.Equal("nope", Assert.Throws<ChatException>(() => ChatEngine.Parse("""{"error":{"message":"nope"}}""", null)).Message);
        Assert.Equal("plain", Assert.Throws<ChatException>(() => ChatEngine.Parse("""{"error":"plain"}""", null)).Message);
    }
}
