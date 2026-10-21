using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wicked.Contracts;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Core.Http;
using Wicked.Core.Models;
using Wicked.Core.Server;
using Wicked.Core.Settings;
using Wicked.Daemon.Api;

namespace Wicked.Daemon;

/// <summary>The daemon is already running for this profile; the new one stops.</summary>
public sealed class DaemonAlreadyRunningException(string lockPath)
    : Exception($"Another wickedd holds {lockPath}.");

/// <summary>How a daemon starts. The defaults are what <c>wickedd</c> does; tests and <c>wicked-cli serve</c> change a few.</summary>
public sealed record DaemonOptions
{
    /// <summary>Builds the model services (tests point them at loopback stand-ins); null makes the real ones.</summary>
    public Func<WickedPaths, ModelServices>? ServicesFactory { get; init; }

    /// <summary>Whether the model half and the update checks may use the network.</summary>
    public bool AllowNetwork { get; init; } = !WickedHttp.IsOffline;

    /// <summary>A port that replaces the one in settings.json for this run.</summary>
    public int? Port { get; init; }

    /// <summary>Serve the gateway whatever settings.json says (<c>wicked-cli serve</c>).</summary>
    public bool ForceGateway { get; init; }

    /// <summary>Run the update scheduler (the daemon is the one that updates models when no window is open).</summary>
    public bool RunUpdates { get; init; } = true;

    /// <summary>How many requests the gateway serves at once; the rest are told to come back.</summary>
    public int MaxConcurrent { get; init; } = GatewayContext.DefaultConcurrent;

    /// <summary>How patient the router handling is; tests shorten it.</summary>
    internal RuntimeTuning? Tuning { get; init; }
}

/// <summary>
/// The daemon: one per profile, serving HTTP on a Unix socket in the profile's runtime folder. A lock file held for the
/// daemon's whole life decides which process is the daemon; a socket left behind by a daemon that died is removed by the
/// next one that takes the lock. Behind the socket it hosts the model half and, when settings say so, the API gateway (D10, D12).
/// The router belongs to whichever process started it first: a window that already runs one is attached to, not duplicated.
/// </summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly FileStream _lock;
    private readonly WickedPaths _paths;
    private readonly DaemonOptions _options;
    private readonly DateTimeOffset _started = DateTimeOffset.Now;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _apply = new(1, 1);
    private readonly GatewayManager _gateway;

    private DaemonHost(WebApplication app, FileStream heldLock, string socketPath, WickedPaths paths, DaemonOptions options, ModelServices services, ApiLog log, GatewayManager gateway)
    {
        _app = app;
        _lock = heldLock;
        _paths = paths;
        _options = options;
        SocketPath = socketPath;
        Services = services;
        Log = log;
        _gateway = gateway;
        Keys = new ApiKeyStore(paths.ApiKeysFile);
        Ready = Task.Run(() => InitializeAsync(_stop.Token));
    }

    public string SocketPath { get; }

    public ModelServices Services { get; }

    public ApiLog Log { get; }

    public ApiKeyStore Keys { get; }

    /// <summary>Completes when the model services have started and the gateway is configured; faults when they could not.</summary>
    public Task Ready { get; }

    /// <summary>Where the gateway listens now, or null when it is off.</summary>
    public GatewayStatus Gateway => _gateway.Status();

    public static string LockPath(WickedPaths paths) => Path.Combine(paths.Runtime, "wickedd.lock");

    /// <summary>A daemon that only answers on its socket: no network, no update checks, and the gateway on a free port instead of the usual one.</summary>
    public static Task<DaemonHost> StartAsync(WickedPaths paths, CancellationToken ct) =>
        StartAsync(paths, ct, new DaemonOptions { AllowNetwork = false, Port = 0, RunUpdates = false });

    public static async Task<DaemonHost> StartAsync(WickedPaths paths, CancellationToken ct, DaemonOptions? options)
    {
        options ??= new DaemonOptions();
        paths.EnsureCreated();
        var socketPath = paths.DaemonSocket;
        if (System.Text.Encoding.UTF8.GetByteCount(socketPath) > WickedPaths.MaxSocketPath)
        {
            throw new IOException($"The socket path is too long for a Unix socket ({socketPath.Length} characters): {socketPath}");
        }

        var lockPath = LockPath(paths);
        FileStream heldLock;
        try
        {
            // FileShare.None is an exclusive lock (flock on Unix) that the system drops when the process dies.
            heldLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new DaemonAlreadyRunningException(lockPath);
        }

        try
        {
            if (File.Exists(socketPath)) File.Delete(socketPath);

            var services = options.ServicesFactory?.Invoke(paths) ?? new ModelServices(paths);
            if (options.Tuning is { } tuning) services.Runtime.Tuning = tuning;
            var log = new ApiLog();
            var gateway = new GatewayManager(new GatewayContextFactory(services, log, options.MaxConcurrent));

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 256L * 1024 * 1024; // the socket is the user's own; the gateway has its own limit
                k.ListenUnixSocket(socketPath);
            });
            var app = builder.Build();

            var started = DateTimeOffset.Now;
            app.MapGet("/v1/health", () => Results.Json(
                new HealthReport("ok", BuildInfo.Version, Environment.ProcessId, started), WickedJson.Default.HealthReport));

            var host = new DaemonHost(app, heldLock, socketPath, paths, options, services, log, gateway);

            // The same OpenAI and Anthropic routes the gateway serves, on the socket and without keys, for the command line's chat.
            var control = new GatewayContext(services, log, () => host._gateway.Config ?? GatewayManager.Starting, control: true);
            Api.Gateway.Guard(app, control);
            ControlApi.Map(app, host);
            Api.Gateway.Map(app, control);
            app.MapFallback(http => Api.Gateway.WriteErrorAsync(http, Dialect.OpenAi, 404, $"There is no route {http.Request.Method} {http.Request.Path}.", "not_found"));

            await app.StartAsync(ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Core.Log.Info($"wickedd serving on {socketPath}");
            return host;
        }
        catch
        {
            await heldLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task InitializeAsync(CancellationToken ct)
    {
        try
        {
            var (settings, _) = await SettingsFile.ReadAsync(_paths.SettingsFile, ct).ConfigureAwait(false);
            Services.Configure(ModelOptionsFile.From(settings));
            await Services.InitializeAsync(_options.AllowNetwork, ct).ConfigureAwait(false);
            await ApplyAsync(ct).ConfigureAwait(false);
            if (_options.RunUpdates && _options.AllowNetwork) Services.Updates.Start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Core.Log.Error("The daemon's model services could not start", ex);
            throw;
        }
    }

    /// <summary>Reads settings.json and the key file again and puts them in force: the model options, and the gateway (moved when its port or reach changed).</summary>
    public async Task ApplyAsync(CancellationToken ct = default)
    {
        await _apply.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var (settings, _) = await SettingsFile.ReadAsync(_paths.SettingsFile, ct).ConfigureAwait(false);
            Services.Configure(ModelOptionsFile.From(settings));
            var keys = await Keys.LoadCheckAsync(ct).ConfigureAwait(false);
            var config = GatewayConfig.From(settings, keys, _options.Port);
            if (_options.ForceGateway) config = config with { Enabled = true };
            await _gateway.ApplyAsync(config, ct).ConfigureAwait(false);
        }
        finally
        {
            _apply.Release();
        }
    }

    public async Task<DaemonStatus> StatusAsync()
    {
        var ready = Ready.IsCompletedSuccessfully;
        var runtime = Services.Runtime;
        var owner = runtime.Router switch
        {
            RouterProcess => "this",
            AttachedRouter a => a.IsRunning ? "other" : null,
            _ => null,
        };
        var keys = (await Keys.ListAsync().ConfigureAwait(false)).Count;
        return new DaemonStatus(ready, BuildInfo.Version, Environment.ProcessId, _started, Gateway, keys, Services.State.Installed.Count,
            runtime.Models.Count(m => m.State is RunState.Loaded or RunState.Sleeping), owner);
    }

    /// <summary>Asks the daemon to stop (what <c>wicked-cli serve --stop</c> does): <see cref="WaitForShutdownAsync"/> then completes.</summary>
    public void RequestShutdown() => _ = Task.Run(() => _app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication());

    /// <summary>Completes when the daemon is asked to stop (SIGTERM, Ctrl+C, a shutdown request, or <see cref="DisposeAsync"/>).</summary>
    public Task WaitForShutdownAsync(CancellationToken ct) => _app.WaitForShutdownAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await Ready.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or HttpRequestException)
        {
            // It never got going; there is nothing more to stop.
        }

        await _gateway.DisposeAsync().ConfigureAwait(false);
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        await Services.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.Log.Warn($"The socket could not be removed: {ex.Message}");
        }

        _stop.Dispose();
        await _lock.DisposeAsync().ConfigureAwait(false);
    }
}
