using System.Net;
using System.Net.Sockets;

namespace Wicked.Core.Http;

/// <summary>Thrown instead of touching the network when <c>WICKED_OFFLINE=1</c> is set (CI, air-gapped machines, tests).</summary>
public sealed class OfflineException(Uri uri) : IOException($"Offline mode is on (WICKED_OFFLINE=1): refusing {uri.Host}")
{
    public Uri Uri { get; } = uri;
}

/// <summary>
/// The one way Wicked builds an <see cref="HttpClient"/>: pooled connections, automatic decompression, a fixed
/// <c>User-Agent</c>, the system proxy, a 15 s connect timeout and no overall timeout (a multi-gigabyte download must
/// not be cut by a clock; callers pass a <see cref="CancellationToken"/>). Redirects are followed by our own handler so
/// the <c>Authorization</c> header can be dropped when a redirect leaves the host.
/// </summary>
public static class WickedHttp
{
    public const string OfflineVariable = "WICKED_OFFLINE";

    private static readonly Lazy<HttpClient> SharedClient = new(() => CreateClient());

    /// <summary>The process-wide client. Safe to share and never disposed.</summary>
    public static HttpClient Shared => SharedClient.Value;

    /// <summary>True when <c>WICKED_OFFLINE</c> is <c>1</c> or <c>true</c>.</summary>
    public static bool IsOffline =>
        Environment.GetEnvironmentVariable(OfflineVariable) is { } v
        && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A new client. <paramref name="inner"/> replaces the socket layer (tests supply a fake); <paramref name="offline"/>
    /// replaces the environment check. Disposing the client disposes <paramref name="inner"/>.
    /// </summary>
    public static HttpClient CreateClient(HttpMessageHandler? inner = null, Func<bool>? offline = null)
    {
        inner ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 16,
        };
        var client = new HttpClient(new OfflineHandler(offline ?? (() => IsOffline), new RedirectHandler(inner)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Wicked/" + UserAgentVersion());
        return client;
    }

    /// <summary>The build version without the source-revision suffix, which is not a valid product-version token.</summary>
    private static string UserAgentVersion()
    {
        var version = BuildInfo.Version;
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? version[..plus] : version;
    }

    /// <summary>Loopback never leaves the machine, so offline mode lets the local router and test servers through.</summary>
    internal static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback) return true;
        return IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)
               || ip?.AddressFamily == AddressFamily.InterNetworkV6 && ip.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.MapToIPv4());
    }

    private sealed class OfflineHandler(Func<bool> offline, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (offline() && request.RequestUri is { } uri && !IsLoopback(uri)) throw new OfflineException(uri);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
