using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Wicked.Core.Engines;

/// <summary>How to start the router. <see cref="Port"/> null picks a free loopback port; <see cref="ModelsDirectory"/> null starts with presets only.</summary>
public sealed record RouterOptions(
    string ServerPath,
    string PresetPath,
    string LogPath,
    string? ModelsDirectory = null,
    int ModelsMax = 2,
    bool AutoLoad = false,
    int? Port = null,
    IReadOnlyList<string>? ExtraArguments = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    bool Supervise = true,
    int MaxConsecutiveFailures = 8,
    TimeSpan? RestartDelayBase = null);

/// <summary>One end of the router process's life. <see cref="RestartIn"/> is set when the supervisor will start it again.</summary>
public sealed record RouterExit(int? Code, bool Intentional, bool WillRestart, TimeSpan? RestartIn, int ConsecutiveFailures);

/// <summary>
/// The supervised <c>llama-server</c> router (D3): started with the models folder and preset file on a free loopback port,
/// output pumped to a rotating log, health polled until it answers, stopped with SIGTERM (a kill after a timeout), and restarted
/// with growing delays when it dies by itself. Every wait is async and cancellable; the only thread work is the pumps.
/// </summary>
/// <remarks>Thread-safe. <see cref="Exited"/> and <see cref="Started"/> are raised from background threads.</remarks>
public sealed class RouterProcess : IAsyncDisposable
{
    private static readonly TimeSpan HealthyAfter = TimeSpan.FromSeconds(60);

    private readonly RouterOptions _options;
    private readonly HttpClient _http;
    private readonly RotatingLog _log;
    private readonly Lock _gate = new();
    private readonly Queue<string> _tail = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _process;
    private Task? _monitor;
    private int _port;
    private int _failures;
    private bool _stopping;
    private bool _gaveUp;

    public RouterProcess(RouterOptions options, HttpClient http)
    {
        _options = options;
        _http = http;
        _log = new RotatingLog(options.LogPath);
    }

    /// <summary>Raised each time the process ends, whoever ended it.</summary>
    public event EventHandler<RouterExit>? Exited;

    /// <summary>Raised each time a process was started (the first start and every restart); <see cref="BaseAddress"/> may have changed.</summary>
    public event EventHandler? Started;

    public Uri BaseAddress => new($"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/");

    public int? ProcessId
    {
        get { lock (_gate) return _process is { HasExited: false } p ? p.Id : null; }
    }

    public bool IsRunning => ProcessId is not null;

    /// <summary>The last lines the router printed, for an error message when it fails to start.</summary>
    public string RecentOutput
    {
        get { lock (_gate) return string.Join('\n', _tail); }
    }

    /// <summary>The command line the router is started with (the same on every restart).</summary>
    public IReadOnlyList<string> BuildArguments(int port)
    {
        var args = new List<string>();
        if (_options.ModelsDirectory is not null) args.AddRange(["--models-dir", _options.ModelsDirectory]);
        args.AddRange(["--models-preset", _options.PresetPath, "--host", "127.0.0.1", "--port", port.ToString(CultureInfo.InvariantCulture),
            "--models-max", _options.ModelsMax.ToString(CultureInfo.InvariantCulture)]);
        if (!_options.AutoLoad) args.Add("--no-models-autoload");
        if (_options.ExtraArguments is not null) args.AddRange(_options.ExtraArguments);
        return args;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_process is { HasExited: false }) throw new InvalidOperationException("The router is already running");
            _stopping = false;
            _gaveUp = false;
        }

        Launch();
        return Task.CompletedTask;
    }

    /// <summary>Polls <c>/health</c> (503 while models load) until it answers 200, the supervisor gives up, or the timeout passes.</summary>
    public async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var gaveUp = false;
        void OnExit(object? s, RouterExit e) => gaveUp = !e.WillRestart && !e.Intentional;
        Exited += OnExit;
        try
        {
            while (true)
            {
                if (gaveUp || _gaveUp) throw new RouterException($"The router exited and will not be restarted.\n{RecentOutput}");
                if (!IsRunning && _stopping) throw new RouterException("The router was stopped");
                try
                {
                    using var response = await _http.GetAsync(new Uri(BaseAddress, "health"), cts.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode && IsRunning) return;
                }
                catch (HttpRequestException)
                {
                    // Not listening yet.
                }

                await Task.Delay(150, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The router did not become healthy within {timeout.TotalSeconds:0} s.\n{RecentOutput}");
        }
        finally
        {
            Exited -= OnExit;
        }
    }

    /// <summary>Asks the router to stop (SIGTERM; on Windows the console is closed) and kills the whole process tree if it has not ended after <paramref name="timeout"/>.</summary>
    public async Task StopAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        Process? process;
        Task? monitor;
        lock (_gate)
        {
            _stopping = true;
            process = _process;
            monitor = _monitor;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        if (process is { HasExited: false })
        {
            Terminate(process);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log.Warn("The router ignored the stop request; killing it");
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It ended between the check and the kill.
                }

                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
        }

        if (monitor is not null) await monitor.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        _log.Dispose();
    }

    private void Launch()
    {
        var port = _options.Port ?? FreePort();
        _port = port;
        var info = new ProcessStartInfo(_options.ServerPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_options.ServerPath) ?? ".",
        };
        foreach (var a in BuildArguments(port)) info.ArgumentList.Add(a);
        EngineCli.AddLibraryPath(info, Path.GetDirectoryName(_options.ServerPath));
        if (_options.Environment is not null)
        {
            foreach (var (k, v) in _options.Environment) info.Environment[k] = v;
        }

        var process = Process.Start(info) ?? throw new IOException($"Could not start {_options.ServerPath}");
        process.StandardInput.Close();
        var started = Stopwatch.StartNew();
        bool stoppedMeanwhile;
        lock (_gate)
        {
            _process = process;
            _monitor = Task.Run(() => MonitorAsync(process, started));
            stoppedMeanwhile = _stopping;
        }

        // StopAsync ran between the restart delay and this start: nobody is left to stop it, so end it here.
        if (stoppedMeanwhile) process.Kill(entireProcessTree: true);

        _log.Append($"--- started pid {process.Id} on port {port}: {string.Join(' ', info.ArgumentList)}");
        Log.Info($"Router started (pid {process.Id}, port {port})");
        Started?.Invoke(this, EventArgs.Empty);
    }

    private async Task MonitorAsync(Process process, Stopwatch uptime)
    {
        var pumps = Task.WhenAll(Pump(process.StandardOutput, "out"), Pump(process.StandardError, "err"));
        await process.WaitForExitAsync().ConfigureAwait(false);
        // A child that inherited the pipes (a model instance outliving its router) would hold them open; do not wait for it.
        await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(1.5))).ConfigureAwait(false);
        var code = process.ExitCode;
        bool intentional;
        lock (_gate) intentional = _stopping;
        _log.Append($"--- exited with code {code.ToString(CultureInfo.InvariantCulture)}");

        if (intentional || !_options.Supervise)
        {
            RaiseExit(new RouterExit(code, intentional, false, null, _failures));
            return;
        }

        _failures = uptime.Elapsed > HealthyAfter ? 1 : _failures + 1;
        if (_failures > _options.MaxConsecutiveFailures)
        {
            lock (_gate) _gaveUp = true;
            Log.Error($"The router keeps dying (exit {code}); giving up");
            RaiseExit(new RouterExit(code, false, false, null, _failures));
            return;
        }

        var delay = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, _failures - 1)) * (_options.RestartDelayBase ?? TimeSpan.FromSeconds(1)).TotalSeconds);
        Log.Warn($"The router exited with code {code}; restarting in {delay.TotalSeconds:0.#} s");
        RaiseExit(new RouterExit(code, false, true, delay, _failures));
        try
        {
            await Task.Delay(delay, _lifetime.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_stopping) return;
            }

            Launch();
        }
        catch (OperationCanceledException)
        {
            // Stopped while waiting to restart.
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Log.Error("Could not restart the router", ex);
            RaiseExit(new RouterExit(null, false, false, null, _failures));
        }
    }

    private async Task Pump(StreamReader reader, string label)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _log.Append($"[{label}] {line}");
                lock (_gate)
                {
                    _tail.Enqueue(line);
                    while (_tail.Count > 40) _tail.Dequeue();
                }
            }
        }
        catch (IOException)
        {
            // The pipe closed with the process.
        }
    }

    private void RaiseExit(RouterExit exit)
    {
        try
        {
            Exited?.Invoke(this, exit);
        }
        catch (Exception ex)
        {
            Log.Warn($"A router listener threw: {ex.Message}");
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static void Terminate(Process process)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // A console server has no window; CloseMainWindow is a no-op there and the timeout path kills it.
                process.CloseMainWindow();
            }
            else
            {
                kill(process.Id, 15);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
