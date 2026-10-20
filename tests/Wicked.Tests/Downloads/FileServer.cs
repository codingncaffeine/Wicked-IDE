using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Wicked.Tests;

/// <summary>
/// Serves one generated file at <c>/file</c> with the behaviours a download manager has to survive: Range and If-Range
/// support (switchable off), a connection cut after N bytes, transient error statuses, and a slow mode for pause tests.
/// Every request is recorded.
/// </summary>
internal sealed class FileServer : IAsyncDisposable
{
    private readonly LocalServer _server;
    private int _concurrent;
    private int _peak;
    private int _failuresLeft;

    private FileServer(LocalServer server, byte[] content)
    {
        _server = server;
        Content = content;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
    }

    public byte[] Content { get; }

    public string Sha256 { get; }

    public string ETag { get; set; } = "\"v1\"";

    public bool HonourRange { get; set; } = true;

    /// <summary>Cut the connection after this many body bytes, once; null serves normally.</summary>
    public long? CutAfter { get; set; }

    /// <summary>Answer this status for the next <see cref="FailTimes"/> requests.</summary>
    public int FailStatus { get; set; }

    public int FailTimes { get => _failuresLeft; set => _failuresLeft = value; }

    /// <summary>Apply <see cref="FailStatus"/> only to requests that carry a Range header (resumes).</summary>
    public bool FailOnlyResumes { get; set; }

    /// <summary>Pause this long after every 64 KB chunk.</summary>
    public TimeSpan ChunkDelay { get; set; }

    public int Peak => _peak;

    public List<(string? Range, string? IfRange, string? Auth)> Requests { get; } = [];

    public Uri Url => _server.Url("/file");

    public static async Task<FileServer> StartAsync(int size = 8 * 1024 * 1024)
    {
        var content = new byte[size];
        for (var i = 0; i < size; i++) content[i] = (byte)((i * 31) ^ (i >> 8));
        FileServer? self = null;
        var server = await LocalServer.StartAsync(app => app.MapGet("/file", ctx => self!.ServeAsync(ctx)));
        self = new FileServer(server, content);
        return self;
    }

    private async Task ServeAsync(HttpContext ctx)
    {
        var range = ctx.Request.Headers.Range.ToString();
        var ifRange = ctx.Request.Headers.IfRange.ToString();
        lock (Requests) Requests.Add((range.Length > 0 ? range : null, ifRange.Length > 0 ? ifRange : null, ctx.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null));
        var now = Interlocked.Increment(ref _concurrent);
        int seen;
        do { seen = _peak; } while (now > seen && Interlocked.CompareExchange(ref _peak, now, seen) != seen);
        try
        {
            if (FailStatus != 0 && (!FailOnlyResumes || range.Length > 0) && Interlocked.Decrement(ref _failuresLeft) >= 0)
            {
                ctx.Response.StatusCode = FailStatus;
                return;
            }

            long start = 0;
            var partial = false;
            if (HonourRange && range.Length > 0 && RangeHeaderValue.TryParse(range, out var parsed) && parsed.Ranges.FirstOrDefault() is { From: { } from }
                && (ifRange.Length == 0 || ifRange == ETag))
            {
                start = from;
                partial = true;
            }

            ctx.Response.Headers.ETag = ETag;
            ctx.Response.Headers.AcceptRanges = "bytes";
            if (partial)
            {
                ctx.Response.StatusCode = 206;
                ctx.Response.Headers.ContentRange = $"bytes {start}-{Content.Length - 1}/{Content.Length}";
            }

            ctx.Response.ContentLength = Content.Length - start;
            var cut = CutAfter;
            if (cut is not null) CutAfter = null;
            long sent = 0;
            while (start + sent < Content.Length)
            {
                var n = (int)Math.Min(64 * 1024, Content.Length - start - sent);
                if (cut is { } limit && sent + n > limit) n = (int)(limit - sent);
                await ctx.Response.Body.WriteAsync(Content.AsMemory((int)(start + sent), n), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                sent += n;
                if (cut is { } l2 && sent >= l2)
                {
                    ctx.Abort();
                    return;
                }

                if (ChunkDelay > TimeSpan.Zero) await Task.Delay(ChunkDelay, ctx.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _concurrent);
        }
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
