using System.Text;
using Wicked.Core.Chat;

namespace Wicked.Tests.Chat;

public sealed class SseParserTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A stream that hands out its bytes in the pieces it was given, one piece per read.</summary>
    private sealed class Pieces(IEnumerable<byte[]> pieces) : Stream
    {
        private readonly List<byte[]> _list = [.. pieces];
        private int _index;
        private int _offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_index < _list.Count && _offset >= _list[_index].Length)
            {
                _index++;
                _offset = 0;
            }

            if (_index >= _list.Count) return 0;
            var piece = _list[_index];
            var n = Math.Min(buffer.Length, piece.Length - _offset);
            piece.AsSpan(_offset, n).CopyTo(buffer);
            _offset += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ReadAsync(params byte[][] pieces)
    {
        var events = new List<string>();
        await foreach (var data in SseParser.ReadDataAsync(new Pieces(pieces), Ct)) events.Add(data);
        return events;
    }

    private const string Reply =
        ": keep-alive\n\n"
        + "data: {\"a\":1}\n\n"
        + "event: ignored\nid: 7\nretry: 100\ndata: line one\ndata: line two é中\U0001F600\n\n"
        + "data:no-space\n\n"
        + "data\n\n"
        + "data: [DONE]\n\n";

    private static readonly string[] Expected = ["{\"a\":1}", "line one\nline two é中\U0001F600", "no-space", "[DONE]"];

    [Fact]
    public async Task EventsComeOutWhateverTheSplitOfTheBytes()
    {
        var bytes = Encoding.UTF8.GetBytes(Reply);
        for (var cut = 0; cut <= bytes.Length; cut++)
        {
            Assert.Equal(Expected, await ReadAsync(bytes[..cut], bytes[cut..]));
        }
    }

    [Fact]
    public async Task EventsComeOutWhenTheBytesArriveOneAtATime()
    {
        var bytes = Encoding.UTF8.GetBytes(Reply);

        Assert.Equal(Expected, await ReadAsync([.. bytes.Select(b => new[] { b })]));
    }

    [Fact]
    public async Task CarriageReturnLineEndingsAreTheSameAsNewlines()
    {
        var bytes = Encoding.UTF8.GetBytes(Reply.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal(Expected, await ReadAsync(bytes));
    }

    [Fact]
    public async Task TheEventBeingWrittenWhenTheServerHangsUpStillCounts()
    {
        Assert.Equal(["first", "cut off"], await ReadAsync("data: first\n\ndata: cut off"u8.ToArray()));
        Assert.Empty(await ReadAsync(": only a comment\n"u8.ToArray()));
    }

    [Fact]
    public async Task EventsAreYieldedAsTheyArriveNotWhenTheStreamEnds()
    {
        // The first event is complete; the stream then stalls. A reader that buffered the stream would never get here.
        using var gate = new SemaphoreSlim(0);
        var stream = new Stalling("data: first\n\n"u8.ToArray(), gate);
        await using var events = SseParser.ReadDataAsync(stream, Ct).GetAsyncEnumerator(Ct);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal("first", events.Current);
        gate.Release();
    }

    private sealed class Stalling(byte[] first, SemaphoreSlim gate) : Stream
    {
        private bool _given;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_given)
            {
                await gate.WaitAsync(cancellationToken);
                return 0;
            }

            _given = true;
            first.CopyTo(buffer);
            return first.Length;
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
