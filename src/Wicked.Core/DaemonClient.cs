using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization.Metadata;
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

    /// <summary>The client itself, for the callers that speak the daemon's OpenAI and Anthropic routes (the command line's chat).</summary>
    public HttpClient Http => _http;

    /// <summary>The daemon's health, or null when no daemon answers on the socket.</summary>
    public async Task<HealthReport?> GetHealthAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync("v1/health", WickedJson.Default.HealthReport, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return null;
        }
    }

    /// <summary>The daemon, its gateway and the model half; null when no daemon answers.</summary>
    public async Task<DaemonStatus?> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            return await _http.GetFromJsonAsync("v1/wicked/status", WickedJson.Default.DaemonStatus, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return null;
        }
    }

    /// <summary>Tells the daemon to read settings.json and the keys again and put them in force (port, on/off, CORS, keys). Returns the gateway's state, or null when no daemon answers.</summary>
    public async Task<DaemonStatus?> ApplyAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsync("v1/wicked/apply", null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync(WickedJson.Default.DaemonStatus, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNoAnswer(ex, ct))
        {
            return null;
        }
    }

    public async Task<T?> GetAsync<T>(string path, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(type, ct).ConfigureAwait(false);
    }

    public async Task<TResponse?> PostAsync<TRequest, TResponse>(string path, TRequest body, JsonTypeInfo<TRequest> requestType, JsonTypeInfo<TResponse> responseType, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, requestType, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync(responseType, ct).ConfigureAwait(false);
    }

    public async Task PostAsync<TRequest>(string path, TRequest body, JsonTypeInfo<TRequest> requestType, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(path, body, requestType, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>The <c>data:</c> payloads of a server-sent-events response, as they arrive. <paramref name="body"/> (JSON) makes it a POST.</summary>
    public async IAsyncEnumerable<string> StreamAsync(string path, string? body, [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureAsync(response, ct).ConfigureAwait(false);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.StartsWith("data:", StringComparison.Ordinal)) yield return line[5..].TrimStart();
        }
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"The daemon answered {(int)response.StatusCode}: {(text.Length > 300 ? text[..300] : text)}", null, response.StatusCode);
    }

    private static bool IsNoAnswer(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or SocketException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    public void Dispose() => _http.Dispose();
}
