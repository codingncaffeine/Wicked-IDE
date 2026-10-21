using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wicked.Contracts;
using Wicked.Core;

namespace Wicked.Daemon.Api;

/// <summary>
/// Owns the gateway's listener: starts it, moves it when the port or its reach changes, stops it when it is turned off, and
/// says why when it cannot (the port is taken). Everything else in <see cref="GatewayConfig"/> (keys, origins, loading on first
/// request) changes under a running listener without a rebind.
/// </summary>
public sealed class GatewayManager(GatewayContextFactory contexts) : IAsyncDisposable
{
    private readonly SemaphoreSlim _change = new(1, 1);
    private volatile GatewayConfig? _config;
    private WebApplication? _app;
    private string? _error;
    private int _port;

    /// <summary>The configuration in force; what a request is checked against.</summary>
    public GatewayConfig? Config => _config;

    /// <summary>What a request is checked against before settings have been read: nothing is loaded for it, and it is served as on this machine only.</summary>
    public static GatewayConfig Starting { get; } = new(false, 0, false, true, [], 64L * 1024 * 1024, new Wicked.Core.Server.ApiKeyCheck([]));

    public async Task ApplyAsync(GatewayConfig next, CancellationToken ct = default)
    {
        await _change.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_app is not null && _config is { } current && current.SameListener(next))
            {
                _config = next;
                return;
            }

            if (_app is not null)
            {
                await StopCoreAsync().ConfigureAwait(false);
            }

            _config = next;
            _error = null;
            if (!next.Enabled) return;
            try
            {
                _app = await StartCoreAsync(next, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException)
            {
                _app = null;
                _error = ex is IOException { InnerException: SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse } } or SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }
                    || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
                    ? $"Port {next.Port} is already in use. Pick another port in Settings, or stop whatever holds it."
                    : $"The server could not start: {ex.Message}";
                Log.Warn(_error);
            }
        }
        finally
        {
            _change.Release();
        }
    }

    public GatewayStatus Status()
    {
        var config = _config;
        var running = _app is not null;
        var host = config?.Lan == true ? "0.0.0.0" : "127.0.0.1";
        return new GatewayStatus(config?.Enabled ?? false, running, running ? $"http://{host}:{_port}" : null, running ? _port : config?.Port ?? 0, config?.Lan ?? false, _error);
    }

    private async Task<WebApplication> StartCoreAsync(GatewayConfig config, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = config.MaxBodyBytes;
            k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            if (config.Lan)
            {
                k.Listen(Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any, config.Port);
            }
            else if (config.Port == 0)
            {
                k.Listen(IPAddress.Loopback, 0);
            }
            else
            {
                k.ListenLocalhost(config.Port);
            }
        });
        var app = builder.Build();
        var context = contexts.Create(() => _config ?? config, control: false);
        Gateway.Guard(app, context);
        Gateway.Map(app, context);
        app.MapFallback(http => Gateway.WriteErrorAsync(http, Dialect.OpenAi, 404, $"There is no route {http.Request.Method} {http.Request.Path}.", "not_found"));
        await app.StartAsync(ct).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _port = new Uri(address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal).Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)).Port;
        Log.Info($"The API gateway is listening on port {_port}{(config.Lan ? " (all interfaces)" : "")}");
        return app;
    }

    private async Task StopCoreAsync()
    {
        var app = _app;
        _app = null;
        if (app is null) return;
        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _change.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _change.Release();
        }
    }
}

/// <summary>Builds the gateway's request context from the daemon's parts; one per listener (TCP and socket).</summary>
public sealed class GatewayContextFactory(Wicked.Core.Models.ModelServices services, ApiLog log, int maxConcurrent = GatewayContext.DefaultConcurrent)
{
    internal GatewayContext Create(Func<GatewayConfig> config, bool control) => new(services, log, config, control, maxConcurrent);
}
