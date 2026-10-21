using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Wicked.Core.Debug;

/// <summary>An open line to one adapter: where to read, where to write, and what ends the adapter when the session does.</summary>
public sealed record AdapterChannel(Stream Input, Stream Output, IAsyncDisposable? Owner = null);

/// <summary>
/// Where an adapter is and how to reach it. A session opens a channel; so does a child session the adapter asks for
/// (js-debug's workers connect to the same server again, a stdio adapter is started again).
/// </summary>
public abstract class AdapterEndpoint : IAsyncDisposable
{
    public abstract Task<AdapterChannel> OpenAsync(CancellationToken ct);

    public virtual ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Finds the adapter for a configuration's type (the manager asks; tests answer with their own).</summary>
public interface IDebugAdapterProvider
{
    Task<AdapterEndpoint> ResolveAsync(string type, JsonElement configuration, CancellationToken ct);
}

/// <summary>An adapter already listening somewhere (a configuration's <c>debugServer</c> port).</summary>
public sealed class TcpAdapterEndpoint(string host, int port) : AdapterEndpoint
{
    public override async Task<AdapterChannel> OpenAsync(CancellationToken ct)
    {
        var client = await ProcessAdapterEndpoint.ConnectAsync(host, port, null, ct).ConfigureAwait(false);
        var stream = client.GetStream();
        return new AdapterChannel(stream, stream, new Closer(client));
    }

    private sealed class Closer(TcpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// An adapter Wicked starts: over its standard streams (one process per session), or as a server on a port picked here that the
/// client connects to (one process shared by the session and its children, ended with the last of them).
/// </summary>
public sealed class ProcessAdapterEndpoint(AdapterCommand command, string workingDirectory) : AdapterEndpoint
{
    private readonly object _gate = new();
    private Process? _server;
    private int _port;
    private int _leases;
    private readonly Queue<string> _tail = new();

    public override async Task<AdapterChannel> OpenAsync(CancellationToken ct)
    {
        if (command.Transport == AdapterTransport.Stdio)
        {
            var process = Start(command.Arguments);
            return new AdapterChannel(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, new Killer(process));
        }

        Process server;
        int port;
        lock (_gate)
        {
            if (_server is null || _server.HasExited)
            {
                _port = FreePort();
                _server = Start([.. command.Arguments.Select(a => a.Replace("{port}", _port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))]);
            }

            server = _server;
            port = _port;
            _leases++;
        }

        try
        {
            var client = await ConnectAsync("127.0.0.1", port, server, ct, Tail).ConfigureAwait(false);
            var stream = client.GetStream();
            return new AdapterChannel(stream, stream, new Lease(this, client));
        }
        catch
        {
            Release();
            throw;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        Process? server;
        lock (_gate)
        {
            server = _server;
            _server = null;
        }

        if (server is not null) await new Killer(server).DisposeAsync().ConfigureAwait(false);
    }

    private string Tail()
    {
        lock (_tail) return string.Join('\n', _tail);
    }

    private void Release()
    {
        Process? done = null;
        lock (_gate)
        {
            if (--_leases > 0) return;
            done = _server;
            _server = null;
        }

        if (done is not null) _ = new Killer(done).DisposeAsync().AsTask();
    }

    private Process Start(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(command.Program)
        {
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : Environment.CurrentDirectory,
            RedirectStandardInput = command.Transport == AdapterTransport.Stdio,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        foreach (var (key, value) in command.Environment) info.Environment[key] = value;
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new IOException($"{command.Program} did not start");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"The debug adapter {command.Program} could not be started: {ex.Message}", ex);
        }

        ProcessRegistry.Track(process);
        // The adapter's own messages (not the program's) go to a log tail for the error shown when it fails to start.
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.BeginErrorReadLine();
        if (command.Transport == AdapterTransport.Tcp)
        {
            process.OutputDataReceived += (_, e) => Remember(e.Data);
            process.BeginOutputReadLine();
        }

        return process;
    }

    private void Remember(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_tail)
        {
            _tail.Enqueue(line);
            while (_tail.Count > 12) _tail.Dequeue();
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Connects to an adapter that is still starting: tries again until it listens, it exits, or fifteen seconds pass.</summary>
    internal static async Task<TcpClient> ConnectAsync(string host, int port, Process? process, CancellationToken ct, Func<string>? tail = null)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var client = new TcpClient { NoDelay = true };
            try
            {
                await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
                return client;
            }
            catch (SocketException)
            {
                client.Dispose();
                if (process is { HasExited: true }) throw new IOException($"The debug adapter exited before it listened (code {process.ExitCode}){Last(tail)}");
                if (DateTime.UtcNow > until) throw new IOException($"The debug adapter did not listen on port {port} within 15 seconds{Last(tail)}");
                await Task.Delay(40, ct).ConfigureAwait(false);
            }
        }
    }

    private static string Last(Func<string>? tail) => tail?.Invoke() is { Length: > 0 } text ? ":\n" + text : "";

    private sealed class Lease(ProcessAdapterEndpoint owner, TcpClient client) : IAsyncDisposable
    {
        private int _done;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _done, 1) != 0) return ValueTask.CompletedTask;
            client.Dispose();
            owner.Release();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Ends a process and what it started.</summary>
    private sealed class Killer(Process process) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    try
                    {
                        process.StandardInput.Close();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    {
                    }

                    using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                    try
                    {
                        await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                        await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                // Already gone, or not ours to wait for any longer.
            }
            finally
            {
                ProcessRegistry.Untrack(process);
                process.Dispose();
            }
        }
    }
}

/// <summary>
/// Finds the adapter for a configuration's <c>type</c> in the catalog and the machine: a <c>debugServer</c> port in the
/// configuration wins (a server the user runs themselves), then an installed adapter; otherwise the error says which adapter
/// would serve the type and how to get it.
/// </summary>
public sealed class DebugAdapterProvider(DebugAdapterLocator locator) : IDebugAdapterProvider
{
    public async Task<AdapterEndpoint> ResolveAsync(string type, JsonElement configuration, CancellationToken ct)
    {
        if (configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty("debugServer", out var port) && port.TryGetInt32(out var number)) return new TcpAdapterEndpoint("127.0.0.1", number);
        DebugAdapterSpec? first = null;
        foreach (var spec in DebugAdapterCatalog.ForType(type))
        {
            first ??= spec;
            if (await locator.FindAsync(spec, ct).ConfigureAwait(false) is { } command)
            {
                var cwd = configuration.ValueKind == JsonValueKind.Object && configuration.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : Environment.CurrentDirectory;
                return new ProcessAdapterEndpoint(command, cwd);
            }
        }

        throw new DebugAdapterMissingException(type, first, first is null
            ? $"No debug adapter is known for the configuration type \"{type}\""
            : $"{first.Name} is not installed ({DebugAdapterInstaller.Unavailable(first) ?? "it can be installed from the Run and Debug panel"})");
    }
}
