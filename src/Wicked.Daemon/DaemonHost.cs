using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wicked.Contracts;
using Wicked.Core;

namespace Wicked.Daemon;

/// <summary>The daemon is already running for this profile; the new one stops.</summary>
public sealed class DaemonAlreadyRunningException(string lockPath)
    : Exception($"Another wickedd holds {lockPath}.");

/// <summary>
/// The daemon: one per profile, serving HTTP on a Unix socket in the profile's runtime folder. A lock file held for the
/// daemon's whole life decides which process is the daemon; a socket left behind by a daemon that died is removed by the
/// next one that takes the lock.
/// </summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly FileStream _lock;

    private DaemonHost(WebApplication app, FileStream heldLock, string socketPath)
    {
        _app = app;
        _lock = heldLock;
        SocketPath = socketPath;
    }

    public string SocketPath { get; }

    public static string LockPath(WickedPaths paths) => Path.Combine(paths.Runtime, "wickedd.lock");

    public static async Task<DaemonHost> StartAsync(WickedPaths paths, CancellationToken ct)
    {
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

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(socketPath));
            var app = builder.Build();

            var started = DateTimeOffset.Now;
            app.MapGet("/v1/health", () => Results.Json(
                new HealthReport("ok", BuildInfo.Version, Environment.ProcessId, started), WickedJson.Default.HealthReport));

            await app.StartAsync(ct).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Log.Info($"wickedd serving on {socketPath}");
            return new DaemonHost(app, heldLock, socketPath);
        }
        catch
        {
            await heldLock.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Completes when the daemon is asked to stop (SIGTERM, Ctrl+C, or <see cref="DisposeAsync"/>).</summary>
    public Task WaitForShutdownAsync(CancellationToken ct) => _app.WaitForShutdownAsync(ct);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The socket could not be removed: {ex.Message}");
        }

        await _lock.DisposeAsync().ConfigureAwait(false);
    }
}
