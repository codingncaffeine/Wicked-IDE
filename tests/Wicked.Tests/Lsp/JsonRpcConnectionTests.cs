using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Wicked.Core.Lsp;

namespace Wicked.Tests.Lsp;

public sealed class JsonRpcConnectionTests : IAsyncLifetime
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private JsonRpcConnection _client = null!;
    private JsonRpcConnection _server = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        // Two of our own connections back to back: the server end answers what a test registers on it.
        _client = new JsonRpcConnection(_toClient.Reader.AsStream(), _toServer.Writer.AsStream());
        _server = new JsonRpcConnection(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _server.DisposeAsync();
    }

    private void Start()
    {
        _server.Start();
        _client.Start();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task ARequestGetsItsOwnAnswerAmongMany()
    {
        _server.OnRequest("add", async (p, ct) =>
        {
            var a = p.GetProperty("a").GetInt32();
            await Task.Delay(Random.Shared.Next(0, 5), ct);
            return Json((a + p.GetProperty("b").GetInt32()).ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        Start();

        var answers = await Task.WhenAll(Enumerable.Range(0, 200).Select(i => _client.RequestAsync("add", Json($$"""{"a":{{i}},"b":1000}"""), Ct)));

        for (var i = 0; i < answers.Length; i++) Assert.Equal(i + 1000, answers[i].GetInt32());
    }

    [Fact]
    public async Task AnErrorComesBackWithItsCodeAndAnUnknownMethodIsMethodNotFound()
    {
        _server.OnRequest("picky", (_, _) => throw new JsonRpcException(JsonRpcErrors.InvalidParams, "no such file"));
        Start();

        var picky = await Assert.ThrowsAsync<JsonRpcException>(() => _client.RequestAsync("picky", null, Ct));
        Assert.Equal(JsonRpcErrors.InvalidParams, picky.Code);
        Assert.Equal("no such file", picky.Message);
        var unknown = await Assert.ThrowsAsync<JsonRpcException>(() => _client.RequestAsync("nobody/home", null, Ct));
        Assert.Equal(JsonRpcErrors.MethodNotFound, unknown.Code);
    }

    [Fact]
    public async Task ANullResultIsNull()
    {
        _server.OnRequest("shutdown", (_, _) => Task.FromResult(default(JsonElement)));
        Start();

        Assert.Equal(JsonValueKind.Null, (await _client.RequestAsync("shutdown", null, Ct)).ValueKind);
    }

    [Fact]
    public async Task NotificationsArriveInTheOrderSent()
    {
        var seen = new List<int>();
        var all = new TaskCompletionSource();
        _server.OnNotification("tick", p =>
        {
            seen.Add(p.GetProperty("n").GetInt32());
            if (seen.Count == 1000) all.SetResult();
        });
        Start();

        for (var i = 0; i < 1000; i++) _client.Notify("tick", Json($$"""{"n":{{i}}}"""));
        await all.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(Enumerable.Range(0, 1000), seen);
    }

    [Fact]
    public async Task CancellingEndsTheWaitAtOnceAndTellsTheServer()
    {
        var serverSawCancel = new TaskCompletionSource();
        _server.OnRequest("slow", async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                serverSawCancel.SetResult();
                throw;
            }

            return default;
        });
        Start();
        using var cts = new CancellationTokenSource();

        var request = _client.RequestAsync("slow", null, cts.Token);
        await Task.Delay(50, Ct);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(2), Ct));
        await serverSawCancel.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task TheOtherSideClosingFailsWhatIsPendingAndSaysSo()
    {
        var closed = new TaskCompletionSource<Exception?>();
        _client.Closed += (_, ex) => closed.TrySetResult(ex);
        _server.OnRequest("never", (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => default(JsonElement), TaskScheduler.Default));
        Start();

        var pending = _client.RequestAsync("never", null, Ct);
        await Task.Delay(50, Ct);
        await _toClient.Writer.CompleteAsync(); // the server's stdout ends

        var error = await Assert.ThrowsAsync<JsonRpcException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(JsonRpcErrors.ConnectionClosed, error.Code);
        Assert.Null(await closed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.True(_client.IsClosed);
        await Assert.ThrowsAsync<JsonRpcException>(() => _client.RequestAsync("after", null, Ct));
    }

    [Fact]
    public async Task FramesSplitAtEveryByteStillParse()
    {
        var bytes = new MemoryStream();
        foreach (var body in new[] { """{"a":1}""", """{"text":"é ✓ 𝄞"}""", "{}" })
        {
            var json = Encoding.UTF8.GetBytes(body);
            bytes.Write(Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n"));
            bytes.Write(json);
        }

        var reader = new JsonRpcConnection.FrameReader(new Trickle(bytes.ToArray()));

        Assert.Equal("""{"a":1}""", Encoding.UTF8.GetString((await reader.ReadAsync(Ct))!));
        Assert.Equal("""{"text":"é ✓ 𝄞"}""", Encoding.UTF8.GetString((await reader.ReadAsync(Ct))!));
        Assert.Equal("{}", Encoding.UTF8.GetString((await reader.ReadAsync(Ct))!));
        Assert.Null(await reader.ReadAsync(Ct));
    }

    [Fact]
    public async Task HeadersAreReadInAnyCaseAndOthersAreIgnored()
    {
        var text = "content-length: 2\r\nContent-Type: application/vscode-jsonrpc; charset=utf-8\r\n\r\n{}";
        var reader = new JsonRpcConnection.FrameReader(new MemoryStream(Encoding.ASCII.GetBytes(text)));

        Assert.Equal("{}", Encoding.UTF8.GetString((await reader.ReadAsync(Ct))!));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new JsonRpcConnection.FrameReader(new MemoryStream("Content-Length: lots\r\n\r\n"u8.ToArray())).ReadAsync(Ct));
    }

    [Fact]
    public async Task TheServerCanAskTheClientToo()
    {
        _client.OnRequest("workspace/configuration", (p, _) => Task.FromResult(Json($"[{p.GetProperty("items").GetArrayLength()}]")));
        Start();

        var answer = await _server.RequestAsync("workspace/configuration", Json("""{"items":[{},{}]}"""), Ct);

        Assert.Equal(2, answer[0].GetInt32());
    }

    [Fact]
    public async Task AWriterThatFailsEndsTheConnectionInsteadOfLeavingRequestsWaiting()
    {
        await using var broken = new JsonRpcConnection(new MemoryStream(), new Unwritable());
        broken.Start();

        var error = await Assert.ThrowsAsync<JsonRpcException>(() => broken.RequestAsync("initialize", null, Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Equal(JsonRpcErrors.ConnectionClosed, error.Code);
    }

    /// <summary>A stream whose writes fail with something other than an I/O error.</summary>
    private sealed class Unwritable : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => throw new NotSupportedException("no writes");
    }

    /// <summary>A stream that hands out one byte per read, as a slow pipe can.</summary>
    private sealed class Trickle(byte[] bytes) : Stream
    {
        private int _at;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => bytes.Length;

        public override long Position { get => _at; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_at >= bytes.Length || count == 0) return 0;
            buffer[offset] = bytes[_at++];
            return 1;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_at >= bytes.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = bytes[_at++];
            return ValueTask.FromResult(1);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
