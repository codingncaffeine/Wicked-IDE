using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Wicked.Core.Debug;

namespace Wicked.Tests.Debug;

/// <summary>The Debug Adapter Protocol's wire: framing, matching answers to requests, events, requests from the adapter.</summary>
public sealed class DapConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stream that hands back exactly the chunks it was given, one per read, then ends.</summary>
    private sealed class ChunkedStream(IEnumerable<byte[]> chunks) : Stream
    {
        private readonly Queue<byte[]> _chunks = new(chunks);
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_current is null || _offset >= _current.Length)
            {
                if (!_chunks.TryDequeue(out _current)) return 0;
                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        return [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
    }

    private static string Event(string name, string body) => $$"""{"seq":1,"type":"event","event":"{{name}}","body":{{body}}}""";

    /// <summary>Everything the connection hears as events, until the stream ends.</summary>
    private static async Task<List<string>> HearAsync(IEnumerable<byte[]> chunks)
    {
        var heard = new List<string>();
        await using var connection = new DapConnection(new ChunkedStream(chunks), Stream.Null);
        var ended = new TaskCompletionSource();
        connection.OnEvent("output", body => heard.Add(body.GetProperty("output").GetString()!));
        connection.Closed += (_, _) => ended.TrySetResult();
        connection.Start();
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        return heard;
    }

    [Fact]
    public async Task AMessageSplitAtEveryByteIsStillOneMessage()
    {
        var wire = new[] { Frame(Event("output", """{"output":"héllo ✓ wörld\n"}""")), Frame(Event("output", """{"output":"second"}""")) }.SelectMany(f => f).ToArray();
        for (var cut = 1; cut < wire.Length; cut++)
        {
            var heard = await HearAsync([wire[..cut], wire[cut..]]);
            Assert.Equal(["héllo ✓ wörld\n", "second"], heard);
        }
    }

    [Fact]
    public async Task OneByteAtATimeAndSeveralMessagesInOneChunkBothWork()
    {
        var frames = Enumerable.Range(0, 5).Select(i => Frame(Event("output", $$"""{"output":"line {{i}}"}"""))).ToArray();
        var wire = frames.SelectMany(f => f).ToArray();

        var oneAtATime = await HearAsync(wire.Select(b => new[] { b }));
        var allAtOnce = await HearAsync([wire]);

        string[] expected = ["line 0", "line 1", "line 2", "line 3", "line 4"];
        Assert.Equal(expected, oneAtATime);
        Assert.Equal(expected, allAtOnce);
    }

    [Fact]
    public async Task AHeaderWithOtherFieldsAndALargeBodyAreRead()
    {
        var big = new string('x', 300_000);
        var json = Event("output", $$"""{"output":"{{big}}"}""");
        var body = Encoding.UTF8.GetBytes(json);
        var frame = Encoding.ASCII.GetBytes($"Content-Type: application/vscode-jsonrpc; charset=utf-8\r\nContent-Length: {body.Length}\r\n\r\n").Concat(body).ToArray();

        var heard = await HearAsync([frame[..70_000], frame[70_000..]]);

        Assert.Equal(big, Assert.Single(heard));
    }

    [Fact]
    public async Task AFrameThatIsNotJsonIsDroppedAndTheNextOneStillArrives()
    {
        var heard = await HearAsync([Frame("not json at all"), Frame(Event("output", """{"output":"after"}"""))]);

        Assert.Equal(["after"], heard);
    }

    [Fact]
    public async Task ARequestGetsItsOwnAnswerAmongManyOutOfOrder()
    {
        var toAdapter = new Pipe();
        var toClient = new Pipe();
        await using var client = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        await using var adapter = new DapConnection(toAdapter.Reader.AsStream(), toClient.Writer.AsStream());
        adapter.OnRequest("add", async (args, ct) =>
        {
            await Task.Delay(Random.Shared.Next(0, 6), ct);
            return JsonSerializer.SerializeToElement(new { sum = args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32() });
        });
        adapter.Start();
        client.Start();

        var answers = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => client.RequestAsync("add", new { a = i, b = 1000 }, Ct)));

        for (var i = 0; i < answers.Length; i++) Assert.Equal(i + 1000, answers[i].GetProperty("sum").GetInt32());
    }

    [Fact]
    public async Task AFailedRequestCarriesTheAdaptersWordsWithItsVariablesFilledIn()
    {
        var toAdapter = new Pipe();
        var toClient = new Pipe();
        await using var client = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        await using var fake = new FakeWire(toAdapter.Reader.AsStream(), toClient.Writer.AsStream());
        client.Start();
        var pending = client.RequestAsync("evaluate", new { expression = "x" }, Ct);
        var request = await fake.ReadRequestAsync();

        await fake.WriteAsync("""{"seq":9,"type":"response","request_seq":SEQ,"success":false,"command":"evaluate","message":"failed","body":{"error":{"id":2002,"format":"Cannot evaluate {name}: {reason}","variables":{"name":"x","reason":"no such variable"}}}}""".Replace("SEQ", request.GetProperty("seq").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));

        var ex = await Assert.ThrowsAsync<DapException>(() => pending);
        Assert.Equal("Cannot evaluate x: no such variable", ex.Message);
        Assert.Equal(2002, ex.ErrorId);
        Assert.Equal("evaluate", ex.Command);
    }

    [Fact]
    public async Task CancellingARequestEndsTheWaitAndAsksTheAdapterToCancelWhenItSupportsIt()
    {
        var toAdapter = new Pipe();
        var toClient = new Pipe();
        await using var client = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream()) { SupportsCancel = true };
        await using var fake = new FakeWire(toAdapter.Reader.AsStream(), toClient.Writer.AsStream());
        client.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pending = client.RequestAsync("variables", new { variablesReference = 5 }, cts.Token);
        var request = await fake.ReadRequestAsync();

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var cancel = await fake.ReadRequestAsync();
        Assert.Equal("cancel", cancel.GetProperty("command").GetString());
        Assert.Equal(request.GetProperty("seq").GetInt32(), cancel.GetProperty("arguments").GetProperty("requestId").GetInt32());
    }

    [Fact]
    public async Task ARequestFromTheAdapterIsAnsweredAndAnUnknownOneIsAnError()
    {
        var toAdapter = new Pipe();
        var toClient = new Pipe();
        await using var client = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        await using var fake = new FakeWire(toAdapter.Reader.AsStream(), toClient.Writer.AsStream());
        client.OnRequest("runInTerminal", (args, _) => Task.FromResult(JsonSerializer.SerializeToElement(new { processId = 4242, echoed = args.GetProperty("title").GetString() })));
        client.Start();

        await fake.WriteAsync("""{"seq":3,"type":"request","command":"runInTerminal","arguments":{"title":"t"}}""");
        var known = await fake.ReadResponseAsync();
        await fake.WriteAsync("""{"seq":4,"type":"request","command":"mystery","arguments":{}}""");
        var unknown = await fake.ReadResponseAsync();

        Assert.True(known.GetProperty("success").GetBoolean());
        Assert.Equal(3, known.GetProperty("request_seq").GetInt32());
        Assert.Equal(4242, known.GetProperty("body").GetProperty("processId").GetInt32());
        Assert.Equal("t", known.GetProperty("body").GetProperty("echoed").GetString());
        Assert.False(unknown.GetProperty("success").GetBoolean());
        Assert.Contains("mystery", unknown.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PendingRequestsFailWhenTheAdapterGoesAway()
    {
        var toAdapter = new Pipe();
        var toClient = new Pipe();
        await using var client = new DapConnection(toClient.Reader.AsStream(), toAdapter.Writer.AsStream());
        client.Start();
        var pending = client.RequestAsync("threads", null, Ct);

        await toClient.Writer.CompleteAsync();

        var ex = await Assert.ThrowsAsync<DapException>(() => pending);
        Assert.Contains("closed", ex.Message);
        await Assert.ThrowsAsync<DapException>(() => client.RequestAsync("threads", null, Ct));
    }

    /// <summary>The adapter's side of a connection written by hand, to see exactly what the client sends.</summary>
    private sealed class FakeWire(Stream input, Stream output) : IAsyncDisposable
    {
        private readonly Wicked.Core.Lsp.JsonRpcConnection.FrameReader _reader = new(input);

        public async Task<JsonElement> ReadRequestAsync() => await ReadAsync();

        public async Task<JsonElement> ReadResponseAsync() => await ReadAsync();

        private async Task<JsonElement> ReadAsync()
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            var body = await _reader.ReadAsync(limit.Token) ?? throw new IOException("the client closed");
            return JsonDocument.Parse(body).RootElement.Clone();
        }

        public async Task WriteAsync(string json)
        {
            await output.WriteAsync(Frame(json), Ct);
            await output.FlushAsync(Ct);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
