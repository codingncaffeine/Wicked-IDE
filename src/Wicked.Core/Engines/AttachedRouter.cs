using System.Globalization;

namespace Wicked.Core.Engines;

/// <summary>What the runtime needs from a router, whether it started the process or found it running.</summary>
public interface IRouterHandle : IAsyncDisposable
{
    /// <summary>Raised each time the router ends. For an attached router that is once, when it is gone for good.</summary>
    event EventHandler<RouterExit>? Exited;

    Uri BaseAddress { get; }

    /// <summary>The router process (not the process that supervises it).</summary>
    int? ProcessId { get; }

    bool IsRunning { get; }

    /// <summary>The last lines the router printed, for an error message.</summary>
    string RecentOutput { get; }

    Task WaitHealthyAsync(TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// A router that another Wicked process owns, found through the runtime file. Nothing here starts, restarts or stops it: it
/// follows the file (the owner rewrites it when the router restarts on another port), and reports the router gone once its
/// process has died and the owner either released the lock or has been silent past the grace period. Disposing it only
/// lets go; when it holds the ownership (an adopted router whose owner had died), that is released with it.
/// </summary>
public sealed class AttachedRouter : IRouterHandle
{
    private readonly WickedPaths _paths;
    private readonly HttpClient _http;
    private readonly RouterOwnership? _ownership;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _grace;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _monitor;
    private volatile RouterRuntimeInfo _info;
    private volatile bool _gone;

    public AttachedRouter(WickedPaths paths, HttpClient http, RouterRuntimeInfo info, TimeSpan poll, TimeSpan grace, RouterOwnership? ownership = null)
    {
        _paths = paths;
        _http = http;
        _info = info;
        _poll = poll;
        _grace = grace;
        _ownership = ownership;
        _monitor = Task.Run(MonitorAsync);
    }

    public event EventHandler<RouterExit>? Exited;

    public Uri BaseAddress => new($"http://127.0.0.1:{_info.Port.ToString(CultureInfo.InvariantCulture)}/");

    public int? ProcessId => _info.RouterPid;

    /// <summary>How many models the router holds at once, as its owner started it.</summary>
    public int ModelsMax => _info.ModelsMax;

    public bool IsRunning => !_gone && RouterOwnership.IsAlive(_info.RouterPid);

    public string RecentOutput => Tail(Path.Combine(_paths.Logs, "router.log"));

    public async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (_gone) throw new RouterException($"The router is gone.\n{RecentOutput}");
                if (RouterOwnership.Read(_paths) is { } fresh) _info = fresh;
                try
                {
                    using var response = await _http.GetAsync(new Uri(BaseAddress, "health"), cts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && IsRunning) return;
                }
                catch (HttpRequestException)
                {
                    // Not listening yet (the owner is restarting it).
                }

                await Task.Delay(150, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The router did not become healthy within {timeout.TotalSeconds:0} s.\n{RecentOutput}");
        }
    }

    /// <summary>Whether the router at <paramref name="info"/> answers its health check now.</summary>
    public static async Task<bool> IsHealthyAsync(HttpClient http, RouterRuntimeInfo info, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{info.Port.ToString(CultureInfo.InvariantCulture)}/health"), ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose.
        }

        _stop.Dispose();
        _ownership?.Dispose();
    }

    private async Task MonitorAsync()
    {
        DateTimeOffset? deadSince = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(_poll, _stop.Token).ConfigureAwait(false);
                var latest = RouterOwnership.Read(_paths);
                if (latest is not null && latest != _info) _info = latest;
                if (RouterOwnership.IsAlive(_info.RouterPid))
                {
                    deadSince = null;
                    continue;
                }

                // The owner restarts a router that died; it has gone for good once the owner is gone too, or has given up silently.
                deadSince ??= DateTimeOffset.UtcNow;
                var ownerGone = _ownership is not null || !RouterOwnership.IsHeld(_paths);
                if (ownerGone || DateTimeOffset.UtcNow - deadSince > _grace)
                {
                    _gone = true;
                    Exited?.Invoke(this, new RouterExit(null, false, false, null, 0));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Detached.
        }
        catch (Exception ex)
        {
            Log.Warn($"Watching the attached router stopped: {ex.Message}");
        }
    }

    private static string Tail(string path)
    {
        try
        {
            if (!File.Exists(path)) return "";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Max(0, stream.Length - 8192), SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            return string.Join('\n', reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(40));
        }
        catch (IOException)
        {
            return "";
        }
    }
}
