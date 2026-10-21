using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Wicked.Contracts;

namespace Wicked.Daemon.Api;

/// <summary>
/// The request log (D12): the last requests the gateway answered, kept in a ring, and a live feed of new ones for the window's
/// log view. Nothing about a request's content is kept, only its method, route, model, status, token counts and time.
/// </summary>
public sealed class ApiLog(int capacity = 1000)
{
    private readonly Lock _gate = new();
    private readonly Queue<ApiLogEntry> _entries = new();
    private readonly List<Channel<ApiLogEntry>> _subscribers = [];
    private long _next;

    public ApiLogEntry Add(DateTimeOffset time, string method, string route, string? model, int status, int? promptTokens, int? completionTokens, long milliseconds, bool streamed, string? client, string? error)
    {
        ApiLogEntry entry;
        Channel<ApiLogEntry>[] listeners;
        lock (_gate)
        {
            entry = new ApiLogEntry(++_next, time, method, route, model, status, promptTokens, completionTokens, milliseconds, streamed, client, error);
            _entries.Enqueue(entry);
            while (_entries.Count > capacity) _entries.Dequeue();
            listeners = [.. _subscribers];
        }

        foreach (var channel in listeners) channel.Writer.TryWrite(entry);
        return entry;
    }

    /// <summary>The entries after <paramref name="afterId"/> (0 for all kept), oldest first, at most <paramref name="limit"/> of the newest.</summary>
    public IReadOnlyList<ApiLogEntry> Since(long afterId, int limit = 500)
    {
        lock (_gate) return [.. _entries.Where(e => e.Id > afterId).TakeLast(limit)];
    }

    /// <summary>New entries as they are added, until the token is cancelled.</summary>
    public async IAsyncEnumerable<ApiLogEntry> FollowAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateBounded<ApiLogEntry>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        lock (_gate) _subscribers.Add(channel);
        try
        {
            await foreach (var entry in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return entry;
        }
        finally
        {
            lock (_gate) _subscribers.Remove(channel);
        }
    }
}
