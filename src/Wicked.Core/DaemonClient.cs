using System.Net.Http.Json;
using System.Net.Sockets;
using Wicked.Contracts;

namespace Wicked.Core;

/// <summary>
/// Talks to <c>wickedd</c> over its Unix socket (Linux, macOS, and Windows 10 1803 and later). Every call is asynchronous
/// and takes a cancellation token; nothing here may be awaited synchronously on the UI thread.
/// </summary>
public sealed class DaemonClient : IDisposable
{
    private readonly HttpClient _http;

    public DaemonClient(string socketPath, TimeSpan? timeout = null)
    {
        SocketPath = socketPath;
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };

        // The host name is never resolved: every connection goes to the socket.
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://wickedd/"), Timeout = timeout ?? TimeSpan.FromSeconds(10) };
    }

    public string SocketPath { get; }

    /// <summary>The daemon's health, or null when no daemon answers on the socket.</summary>
    public async Task<HealthReport?> GetHealthAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync("v1/health", WickedJson.Default.HealthReport, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
