using Wicked.Core.Models;
using Wicked.Core.Server;
using Wicked.Core.Settings;

namespace Wicked.Daemon.Api;

/// <summary>What the gateway runs with: read from settings.json and the key file, and replaced whole when either changes.</summary>
/// <param name="Enabled">Whether the gateway listens at all.</param>
/// <param name="Port">The TCP port; 0 picks a free one (tests).</param>
/// <param name="Lan">Listen on the local network too.</param>
/// <param name="AutoLoad">Load a model on the first request that names it.</param>
/// <param name="Cors">Web origins that may call from a browser page; <c>*</c> allows any.</param>
/// <param name="MaxBodyBytes">The largest request body read.</param>
/// <param name="Keys">The keys to check. With none, only callers on this machine are served.</param>
public sealed record GatewayConfig(bool Enabled, int Port, bool Lan, bool AutoLoad, IReadOnlyList<string> Cors, long MaxBodyBytes, ApiKeyCheck Keys)
{
    public static GatewayConfig From(WickedSettings s, ApiKeyCheck keys, int? portOverride = null) => new(
        s.ServerEnabled,
        portOverride ?? Math.Clamp(s.ServerPort, 0, 65535),
        s.ServerListenOnLan,
        s.ServerAutoLoad,
        [.. s.ServerCors.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0)], // not a path: a web origin
        Math.Clamp(s.ServerMaxRequestMB, 1, 1024) * 1024L * 1024L,
        keys);

    /// <summary>The same listener (port and reach), so a change to anything else can be put in force without rebinding.</summary>
    public bool SameListener(GatewayConfig other) => Port == other.Port && Lan == other.Lan && Enabled == other.Enabled && MaxBodyBytes == other.MaxBodyBytes;
}

/// <summary>Everything a request handler needs from the daemon.</summary>
public sealed class GatewayContext(ModelServices services, ApiLog log, Func<GatewayConfig> config, bool control, int maxConcurrent = GatewayContext.DefaultConcurrent)
{
    public ModelServices Services { get; } = services;

    public ApiLog Log { get; } = log;

    public GatewayConfig Config => config();

    /// <summary>Mounted on the daemon's own socket: only this user can reach it, so keys, origins and hosts are not checked.</summary>
    public bool Control { get; } = control;

    /// <summary>Forwards to the router on loopback: no proxy, no redirects, no overall timeout (a reply can stream for minutes).</summary>
    public HttpClient Upstream { get; } = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>How many requests are served at once; the rest are told to come back.</summary>
    public SemaphoreSlim Slots { get; } = new(maxConcurrent, maxConcurrent);

    public const int DefaultConcurrent = 64;
}
