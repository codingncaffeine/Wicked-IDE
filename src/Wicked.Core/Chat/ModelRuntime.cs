using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Wicked.Core.Engines;
using Wicked.Core.Hardware;
using Wicked.Core.Models;

namespace Wicked.Core.Chat;

/// <summary>
/// The installed models and their load state (D3). The router is started on the first need, a load or a chat, never at launch.
/// Its preset is generated from the store, the machine and the user's choices (<see cref="RunPlanner"/>) and rewritten when any
/// of them changes; its status comes from <c>/models/sse</c> after an initial <c>GET /models</c>. A router that dies is restarted
/// by its supervisor and the states are read again; a model that fails to load carries the tail of the router log as its error.
/// </summary>
public sealed class ModelRuntime : IModelRuntime, IAsyncDisposable
{
    private readonly ModelServices _services;
    private readonly WickedPaths _paths;
    private readonly HttpClient _http;
    private readonly RunSettingsStore _settingsStore;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly Channel<bool> _notifications = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task _notifier;
    private readonly CancellationTokenSource _disposed = new();
    private readonly Dictionary<string, Live> _live = new(StringComparer.Ordinal);
    private Dictionary<string, ModelRunSettings> _settings = new(StringComparer.Ordinal);
    private IReadOnlyList<RunningModel> _models = [];
    private RunPlan? _plan;
    private int? _modelsMax;
    private bool _settingsLoaded;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private HardwareInfo? _hardware;
    private IReadOnlyList<Store.StoredModel>? _seenInstalled;
    private string? _seenEngine;
    private int _refreshing;
    private int _disposing;
    private IRouterHandle? _router;
    private RouterOwnership? _ownership;
    private RouterClient? _client;
    private CancellationTokenSource? _watchCts;
    private Task? _watcher;
    private int? _routerModelsMax;
    private DateTime _settingsStamp;

    internal ModelRuntime(ModelServices services, WickedPaths paths, HttpClient http)
    {
        _services = services;
        _paths = paths;
        _http = http;
        _settingsStore = new RunSettingsStore(Path.Combine(paths.Config, "model-run-settings.json"));
        _notifier = Task.Run(NotifyLoopAsync);
        services.Changed += OnServicesChanged;
    }

    /// <summary>How the router is supervised; tests shorten the waits.</summary>
    internal RuntimeTuning Tuning { get; set; } = new();

    public event EventHandler? Changed;

    public IReadOnlyList<RunningModel> Models
    {
        get
        {
            lock (_gate) return _models;
        }
    }

    /// <summary>The router process, when one was started (tests kill it to see the supervisor work).</summary>
    internal IRouterHandle? Router
    {
        get
        {
            lock (_gate) return _router;
        }
    }

    internal string PresetPath => Path.Combine(_paths.State, "router", "models.ini");

    internal string LogPath => Path.Combine(_paths.Logs, "router.log");

    /// <summary>The preset text for the models installed right now, as it would be written (for display and tests).</summary>
    internal RunPlan? Plan
    {
        get
        {
            lock (_gate) return _plan;
        }
    }

    public ModelRunSettings GetSettings(string id)
    {
        lock (_gate) return _settings.TryGetValue(id, out var s) ? s : new ModelRunSettings();
    }

    /// <summary>How many models the router may hold at once: the user's number, or null for automatic (one unless video memory clearly holds more).</summary>
    public int? ModelsMax
    {
        get
        {
            lock (_gate) return _modelsMax;
        }
    }

    /// <summary>Loads the user's saved choices and plans the models installed now. Called by <see cref="ModelServices.InitializeAsync"/>; every other call does it when needed.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await EnsureSettingsAsync(ct).ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task SaveSettingsAsync(string id, ModelRunSettings settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await EnsureSettingsAsync(ct).ConfigureAwait(false);
        Validate(id, settings);
        ModelRunSettings before;
        lock (_gate)
        {
            before = _settings.TryGetValue(id, out var s) ? s : new ModelRunSettings();
            var copy = new Dictionary<string, ModelRunSettings>(_settings, StringComparer.Ordinal);
            if (settings == new ModelRunSettings()) copy.Remove(id);
            else copy[id] = settings;
            _settings = copy;
        }

        await SaveFileAsync(ct).ConfigureAwait(false);
        if (before == settings) return;
        var state = StateOf(id);
        await RefreshAsync(ct).ConfigureAwait(false);
        if (state is RunState.Loaded or RunState.Loading or RunState.Sleeping)
        {
            await UnloadAsync(id, ct).ConfigureAwait(false);
            await LoadAsync(id, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sets how many models may be loaded at once (null = automatic). The router has to restart for it, so it is applied now if one is running.</summary>
    public async Task SetModelsMaxAsync(int? max, CancellationToken ct = default)
    {
        if (max is < 1) throw new ArgumentOutOfRangeException(nameof(max));
        await EnsureSettingsAsync(ct).ConfigureAwait(false);
        lock (_gate) _modelsMax = max;
        await SaveFileAsync(ct).ConfigureAwait(false);
        bool running;
        lock (_gate) running = _router is { IsRunning: true };
        if (running)
        {
            await StopRouterAsync(ct).ConfigureAwait(false);
        }

        await RefreshAsync(ct).ConfigureAwait(false);
    }

    public async Task LoadAsync(string id, CancellationToken ct = default)
    {
        await EnsureSettingsAsync(ct).ConfigureAwait(false);
        await RefreshAsync(ct).ConfigureAwait(false);
        if (!Known(id)) throw new ChatException($"{id} is not an installed model.");
        var state = StateOf(id);
        // A model the router reports loaded is only loaded while that router is alive.
        if (state is RunState.Loaded or RunState.Sleeping && Router is { IsRunning: true }) return;

        var client = await EnsureRouterAsync(ct).ConfigureAwait(false);
        if (StateOf(id) is RunState.Loaded or RunState.Sleeping && Router is { IsRunning: true }) return;
        SetLive(id, new Live(RouterModelStatus.Loading, 0, null, null));
        try
        {
            await client.LoadAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RouterException or HttpRequestException)
        {
            // A connection that broke mid-request is most often the router dying: say so, as a death a moment later would.
            var router = Router;
            for (var i = 0; i < 20 && router is { IsRunning: true }; i++) await Task.Delay(50, ct).ConfigureAwait(false);
            if (router is { IsRunning: false })
            {
                var stopped = $"The engine stopped while loading this model.\n{TailOf(router)}";
                SetLive(id, new Live(RouterModelStatus.Unloaded, null, stopped, null));
                throw new ChatException(stopped, ex);
            }

            await SyncQuietlyAsync(ct).ConfigureAwait(false);
            throw new ChatException($"The engine would not load {NameOf(id)}: {ex.Message}", ex);
        }

        await WaitForLoadAsync(id, ct).ConfigureAwait(false);
        var model = Models.First(m => m.Id == id);
        if (model.State == RunState.Failed) throw new ChatException(model.Error ?? $"{model.DisplayName} did not load.");
    }

    public async Task UnloadAsync(string id, CancellationToken ct = default)
    {
        RouterClient? client;
        lock (_gate) client = _router is { IsRunning: true } ? _client : null;
        if (client is null || StateOf(id) is RunState.Unloaded or RunState.Failed) return;
        try
        {
            await client.UnloadAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RouterException or HttpRequestException)
        {
            throw new ChatException($"The engine would not unload {NameOf(id)}: {ex.Message}", ex);
        }

        await WaitForAsync(id, s => s is RunState.Unloaded or RunState.Failed, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Attaches to the router another process runs, if there is one, so this process sees what is loaded without starting anything.
    /// False when no router is running (or this process already has one).
    /// </summary>
    public async Task<bool> AttachExistingAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_router is { IsRunning: true }) return true;
            }

            if (RouterOwnership.Read(_paths) is not { } info || !RouterOwnership.IsAlive(info.RouterPid)
                || !await AttachedRouter.IsHealthyAsync(_http, info, ct).ConfigureAwait(false))
            {
                return false;
            }

            await StopRouterLockedAsync(ct).ConfigureAwait(false);
            await RefreshLockedAsync(ct).ConfigureAwait(false);
            await WatchRouterAsync(new AttachedRouter(_paths, _http, info, Tuning.AttachedPoll, Tuning.OwnerGrace), ownership: null, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Loads <paramref name="id"/> if it is not, and returns the router's address for a request.</summary>
    public async Task<Uri> EnsureLoadedAsync(string id, CancellationToken ct)
    {
        await LoadAsync(id, ct).ConfigureAwait(false);
        RouterClient? client;
        lock (_gate) client = _client;
        if (client is not null && ContextSizeOf(id) is null) await LearnContextAsync(client, id).ConfigureAwait(false);
        lock (_gate) return _router?.BaseAddress ?? throw new ChatException("The model engine is not running.");
    }

    /// <summary>The context the model was started with, for the usage meter.</summary>
    internal int? ContextSizeOf(string id)
    {
        lock (_gate) return _models.FirstOrDefault(m => m.Id == id)?.ContextSize;
    }

    /// <summary>A sentence for an error: whether the engine is down, and its last output.</summary>
    internal string RouterHint()
    {
        IRouterHandle? router;
        lock (_gate) router = _router;
        if (router is null) return "The engine is not running.";
        var lines = router.RecentOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var tail = string.Join('\n', lines.TakeLast(6));
        return (router.IsRunning ? "" : "The engine is not running. ") + (tail.Length > 0 ? $"Its last output:\n{tail}" : "");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposing, 1) == 1) return;
        _services.Changed -= OnServicesChanged;
        await _disposed.CancelAsync().ConfigureAwait(false);
        await StopRouterAsync(CancellationToken.None).ConfigureAwait(false);
        _notifications.Writer.TryComplete();
        await _notifier.ConfigureAwait(false);
        _lifecycle.Dispose();
        _disposed.Dispose();
    }

    // ── Planning ─────────────────────────────────────────────────────────────────────────────────

    private void OnServicesChanged(ModelState state)
    {
        bool different;
        lock (_gate)
        {
            different = !ReferenceEquals(state.Installed, _seenInstalled) || state.Engine?.Id != _seenEngine;
        }

        if (different && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Changes that arrive while this runs are picked up by the pass itself or by the next event.
                    await RefreshAsync(_disposed.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException)
                {
                    Log.Debug($"Model plan not refreshed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _refreshing, 0);
                }
            });
        }
    }

    /// <summary>Plans the installed models again and, when a router runs and the preset text changed, writes it and tells the router.</summary>
    private async Task RefreshAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RefreshLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task RefreshLockedAsync(CancellationToken ct)
    {
        await EnsureSettingsAsync(ct).ConfigureAwait(false);
        await ReloadSettingsIfChangedAsync(ct).ConfigureAwait(false);
        var state = _services.State;
        var hardware = _hardware ??= state.Hardware ?? await HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        var backend = BackendOf(state.Engine ?? _services.Engines.Current);
        Dictionary<string, ModelRunSettings> settings;
        int? max;
        IRouterHandle? router;
        int? inForce;
        lock (_gate)
        {
            settings = _settings;
            max = _modelsMax;
            router = _router;
            inForce = router is { IsRunning: true } ? _routerModelsMax : null;
        }

        var plan = await Task.Run(() => RunPlanner.Plan(state.Installed, state.Catalog?.Catalog, hardware, backend, id => settings.TryGetValue(id, out var s) ? s : new ModelRunSettings(), max, inForce), ct).ConfigureAwait(false);
        RouterClient? client = null;
        lock (_gate)
        {
            _plan = plan;
            _seenInstalled = state.Installed;
            _seenEngine = state.Engine?.Id;
            if (router is { IsRunning: true }) client = _client;
            foreach (var gone in _live.Keys.Where(k => plan.Models.All(m => m.Id != k)).ToList()) _live.Remove(gone);
        }

        Publish();
        if (client is not null && await PresetWriter.WriteAsync(PresetPath, plan.Globals, plan.Models.Select(m => m.Section), ct).ConfigureAwait(false))
        {
            try
            {
                var models = await client.GetModelsAsync(reload: true, ct: ct).ConfigureAwait(false);
                Apply(models);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                Log.Warn($"The router did not take the new preset: {ex.Message}");
            }
        }
    }

    private static EngineBackendKind? BackendOf(EngineInstall? engine)
    {
        if (engine is null) return null;
        var slug = engine.Backend;
        if (slug.StartsWith("cuda", StringComparison.Ordinal)) return EngineBackendKind.Cuda;
        if (slug.StartsWith("rocm", StringComparison.Ordinal)) return EngineBackendKind.Rocm;
        return slug switch
        {
            "vulkan" => EngineBackendKind.Vulkan,
            "metal" => EngineBackendKind.Metal,
            "cpu" => EngineBackendKind.Cpu,
            _ => null,
        };
    }

    private static void Validate(string id, ModelRunSettings s)
    {
        if (s.ContextSize is <= 0) throw new ArgumentException("The context size must be positive.", nameof(s));
        if (s.KvCacheType is { } k && !RunPlanner.CacheTypes.Contains(k)) throw new ArgumentException($"Unknown KV cache type '{k}'.", nameof(s));
        if (s.GpuLayers is { } g && g is not ("auto" or "all") && !int.TryParse(g, NumberStyles.None, CultureInfo.InvariantCulture, out _)) throw new ArgumentException($"GPU layers must be a number, 'auto' or 'all', not '{g}'.", nameof(s));
        if (s.DraftModelId == id) throw new ArgumentException("A model cannot be its own draft model.", nameof(s));
    }

    private async Task EnsureSettingsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_settingsLoaded) return;
        }

        var file = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (_settingsLoaded) return;
            _settings = file.Models;
            _modelsMax = file.ModelsMax;
            _settingsLoaded = true;
            _settingsStamp = StampOf(_settingsStore.Path);
        }
    }

    private async Task SaveFileAsync(CancellationToken ct)
    {
        RunSettingsFile file;
        lock (_gate) file = new RunSettingsFile(_modelsMax, new Dictionary<string, ModelRunSettings>(_settings, StringComparer.Ordinal));
        await _settingsStore.SaveAsync(file, ct).ConfigureAwait(false);
        lock (_gate) _settingsStamp = StampOf(_settingsStore.Path);
    }

    private static DateTime StampOf(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : default;

    /// <summary>Another process on this profile (the window, the daemon) may have saved the run settings; they are read again when the file changed.</summary>
    private async Task ReloadSettingsIfChangedAsync(CancellationToken ct)
    {
        var stamp = StampOf(_settingsStore.Path);
        lock (_gate)
        {
            if (!_settingsLoaded || stamp == _settingsStamp) return;
        }

        var file = await _settingsStore.LoadAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _settings = file.Models;
            _modelsMax = file.ModelsMax;
            _settingsStamp = stamp;
        }
    }

    // ── The router ───────────────────────────────────────────────────────────────────────────────

    private async Task<RouterClient> EnsureRouterAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            IRouterHandle? alive = null;
            RouterClient? aliveClient = null;
            lock (_gate)
            {
                if (_router is { IsRunning: true } r && _client is { } running && !(_watcher?.IsCompleted ?? true))
                {
                    alive = r;
                    aliveClient = running;
                }
            }

            if (alive is not null)
            {
                // A router the supervisor just restarted is running before it listens.
                await alive.WaitHealthyAsync(Tuning.HealthTimeout, ct).ConfigureAwait(false);
                return aliveClient!;
            }

            await StopRouterLockedAsync(ct).ConfigureAwait(false);
            var (ownership, attached) = await AcquireAsync(ct).ConfigureAwait(false);
            if (attached is not null) return await WatchRouterAsync(attached, ownership: null, ct).ConfigureAwait(false);

            try
            {
                var engine = await _services.EnsureEngineAsync(ct).ConfigureAwait(false);
                await RefreshLockedAsync(ct).ConfigureAwait(false);
                var plan = Plan ?? throw new InvalidOperationException("No plan");
                await PresetWriter.WriteAsync(PresetPath, plan.Globals, plan.Models.Select(m => m.Section), ct).ConfigureAwait(false);

                // A router its owner left running when it died is taken over rather than started twice.
                if (await TryAdoptAsync(ownership!, engine, plan.ModelsMax, ct).ConfigureAwait(false) is { } adopted)
                {
                    var adoptedClient = await WatchRouterAsync(adopted, ownership, ct).ConfigureAwait(false);
                    lock (_gate) _routerModelsMax = plan.ModelsMax;
                    return adoptedClient;
                }

                var options = new RouterOptions(engine.ServerPath, PresetPath, LogPath, ModelsMax: plan.ModelsMax, ExtraArguments: ["--no-webui"],
                    MaxConsecutiveFailures: Tuning.MaxConsecutiveFailures, RestartDelayBase: Tuning.RestartDelayBase);
                var router = new RouterProcess(options, _http);
                router.Exited += OnRouterExited;
                // Each start, the first and every restart, says where the router listens now.
                router.Started += (_, _) => ownership!.Publish(new RouterRuntimeInfo(router.BaseAddress.Port, router.ProcessId ?? 0, Environment.ProcessId, engine.Id, plan.ModelsMax, DateTimeOffset.UtcNow));
                try
                {
                    await router.StartAsync(ct).ConfigureAwait(false);
                    await router.WaitHealthyAsync(Tuning.HealthTimeout, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or RouterException or IOException)
                {
                    router.Exited -= OnRouterExited;
                    await router.DisposeAsync().ConfigureAwait(false);
                    throw new ChatException($"The model engine did not start. {ex.Message}", ex);
                }
                catch
                {
                    router.Exited -= OnRouterExited;
                    await router.DisposeAsync().ConfigureAwait(false);
                    throw;
                }

                var client = await WatchRouterAsync(router, ownership, ct).ConfigureAwait(false);
                lock (_gate) _routerModelsMax = plan.ModelsMax;
                return client;
            }
            catch
            {
                // Not handed to a router: let go of the profile's lock so the next try (or another process) can have it.
                bool kept;
                lock (_gate) kept = ReferenceEquals(_ownership, ownership);
                if (!kept) ownership?.Dispose();
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Makes <paramref name="router"/> the one in use: its client, its event watcher, and a first read of the model list.</summary>
    private async Task<RouterClient> WatchRouterAsync(IRouterHandle router, RouterOwnership? ownership, CancellationToken ct)
    {
        router.Exited -= OnRouterExited;
        router.Exited += OnRouterExited;
        var client = new RouterClient(_http, () => router.BaseAddress);
        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _router = router;
            _client = client;
            _ownership = ownership;
            _routerModelsMax = router is AttachedRouter a ? a.ModelsMax : null;
            _watchCts = cts;
            _watcher = Task.Run(() => WatchAsync(router, client, cts.Token));
        }

        await SyncAsync(client, ct).ConfigureAwait(false);
        return client;
    }

    /// <summary>
    /// Takes the profile's router lock, or, when another process holds it, attaches to the router that process runs (waiting for it
    /// to come up when it is mid-start). A lock that frees while waiting is taken: the owner died.
    /// </summary>
    private async Task<(RouterOwnership? Ownership, AttachedRouter? Attached)> AcquireAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(Tuning.AttachTimeout);
        try
        {
            while (true)
            {
                if (RouterOwnership.TryAcquire(_paths) is { } taken) return (taken, null);
                if (RouterOwnership.Read(_paths) is { } info && RouterOwnership.IsAlive(info.RouterPid)
                    && await AttachedRouter.IsHealthyAsync(_http, info, limit.Token).ConfigureAwait(false))
                {
                    Log.Info($"Attached to the router owned by process {info.OwnerPid} (port {info.Port})");
                    return (null, new AttachedRouter(_paths, _http, info, Tuning.AttachedPoll, Tuning.OwnerGrace));
                }

                await Task.Delay(100, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatException("Another Wicked process is starting the model engine and it did not answer in time.");
        }
    }

    /// <summary>A router left running by an owner that died, started from this engine: attached to with the ownership. One from another engine is ended.</summary>
    private async Task<AttachedRouter?> TryAdoptAsync(RouterOwnership ownership, EngineInstall engine, int modelsMax, CancellationToken ct)
    {
        if (RouterOwnership.Read(_paths) is not { } left) return null;
        if (!RouterOwnership.IsAlive(left.RouterPid)) return null;
        if (left.EngineTag == engine.Id && left.ModelsMax == modelsMax && await AttachedRouter.IsHealthyAsync(_http, left, ct).ConfigureAwait(false))
        {
            Log.Info($"Took over the router left by process {left.OwnerPid} (port {left.Port})");
            var mine = left with { OwnerPid = Environment.ProcessId };
            ownership.Publish(mine);
            return new AttachedRouter(_paths, _http, mine, Tuning.AttachedPoll, Tuning.OwnerGrace, ownership);
        }

        if (RouterOwnership.LooksLikeARouter(left.RouterPid))
        {
            Log.Info($"Ending the router left by process {left.OwnerPid}; it was not started the way this one would be");
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(left.RouterPid);
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // It ended on its own meanwhile.
            }
        }

        return null;
    }

    private async Task StopRouterAsync(CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopRouterLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopRouterLockedAsync(CancellationToken ct)
    {
        IRouterHandle? router;
        RouterOwnership? ownership;
        CancellationTokenSource? cts;
        Task? watcher;
        lock (_gate)
        {
            router = _router;
            ownership = _ownership;
            _ownership = null;
            cts = _watchCts;
            watcher = _watcher;
            _router = null;
            _client = null;
            _watchCts = null;
            _watcher = null;
            _routerModelsMax = null;
        }

        if (cts is not null) await cts.CancelAsync().ConfigureAwait(false);
        if (router is not null)
        {
            router.Exited -= OnRouterExited;
            await router.DisposeAsync().ConfigureAwait(false);
        }

        if (watcher is not null)
        {
            try
            {
                await watcher.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped on purpose.
            }
        }

        // The router is down; the profile may have another owner now.
        ownership?.Dispose();
        cts?.Dispose();
        lock (_gate)
        {
            foreach (var key in _live.Keys.ToList()) _live[key] = new Live(RouterModelStatus.Unloaded, null, null, null);
        }

        Publish();
    }

    private void OnRouterExited(object? sender, RouterExit exit)
    {
        if (exit.Intentional) return;
        var router = (IRouterHandle)sender!;
        var tail = TailOf(router);
        lock (_gate)
        {
            foreach (var (id, live) in _live.ToList())
            {
                _live[id] = live.Status == RouterModelStatus.Loading
                    ? new Live(RouterModelStatus.Unloaded, null, $"The engine stopped while loading this model (exit code {Code(exit.Code)}).\n{tail}", null)
                    : new Live(RouterModelStatus.Unloaded, null, null, null);
            }
        }

        Publish();
    }

    private static string Code(int? code) => code?.ToString(CultureInfo.InvariantCulture) ?? "unknown";

    /// <summary>What to show of the router's output after a failure: its error lines (llama.cpp marks them <c> E </c>) when there are any, else the last few lines.</summary>
    private static string TailOf(IRouterHandle router)
    {
        var lines = router.RecentOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var errors = lines.Where(l => l.Contains(" E ", StringComparison.Ordinal)).TakeLast(8).ToList();
        return string.Join('\n', errors.Count > 0 ? errors : lines.TakeLast(10));
    }

    /// <summary>Follows the router's events for as long as it lives: connect, read the list, then read events until the stream ends (a restart), and again.</summary>
    private async Task WatchAsync(IRouterHandle router, RouterClient client, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await router.WaitHealthyAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                await SyncAsync(client, ct).ConfigureAwait(false);
                // What another process does to the router reaches this one by events only; the events a connection missed
                // while it was being made are caught by reading the list again now and then.
                using var reconcile = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var reconciling = router is AttachedRouter ? Task.Run(() => ReconcileAsync(reconcile.Token), CancellationToken.None) : Task.CompletedTask;
                try
                {
                    await foreach (var ev in client.EventsAsync(ct).ConfigureAwait(false))
                    {
                        await ApplyEventAsync(client, ev, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await reconcile.CancelAsync().ConfigureAwait(false);
                    await reconciling.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (RouterException ex)
            {
                // The supervisor gave up: nothing more will come.
                Log.Error($"The router is gone: {ex.Message}");
                lock (_gate)
                {
                    foreach (var key in _live.Keys.ToList())
                    {
                        _live[key] = new Live(RouterModelStatus.Unloaded, null, $"The model engine keeps stopping.\n{TailOf(router)}", null);
                    }
                }

                Publish();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException)
            {
                Log.Debug($"Router events interrupted: {ex.Message}");
            }

            try
            {
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(Tuning.PollInterval, ct).ConfigureAwait(false);
                await SyncQuietlyAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The watcher moved on.
        }
    }

    private async Task ApplyEventAsync(RouterClient client, RouterEvent ev, CancellationToken ct)
    {
        if (ev.Kind is "models_reload" or "model_remove")
        {
            await SyncAsync(client, ct).ConfigureAwait(false);
            return;
        }

        if (ev.Kind != "model_status" || ev.Status is not { } status) return;
        bool wasLoading;
        lock (_gate)
        {
            if (!_live.TryGetValue(ev.Model, out var previous))
            {
                if (_plan?.Models.Any(m => m.Id == ev.Model) != true) return;
                previous = new Live(RouterModelStatus.Unloaded, null, null, null);
            }

            wasLoading = previous.Status == RouterModelStatus.Loading;
            _live[ev.Model] = new Live(status, status == RouterModelStatus.Loading ? ev.LoadProgress ?? previous.Progress : null, null, status == RouterModelStatus.Loaded ? previous.Context : null);
        }

        Publish();
        // A load that ended unloaded failed: the list says so, with the exit code.
        if (status == RouterModelStatus.Unloaded && wasLoading) await SyncAsync(client, ct).ConfigureAwait(false);
        if (status == RouterModelStatus.Loaded) _ = Task.Run(() => LearnContextAsync(client, ev.Model), CancellationToken.None);
    }

    private async Task SyncAsync(RouterClient client, CancellationToken ct)
    {
        var models = await client.GetModelsAsync(ct: ct).ConfigureAwait(false);
        Apply(models);
        foreach (var m in models.Where(m => m.Status == RouterModelStatus.Loaded)) _ = Task.Run(() => LearnContextAsync(client, m.Id), CancellationToken.None);
    }

    private async Task SyncQuietlyAsync(CancellationToken ct)
    {
        RouterClient? client;
        lock (_gate) client = _client;
        if (client is null) return;
        try
        {
            await SyncAsync(client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            Log.Debug($"Could not read the router's model list: {ex.Message}");
        }
    }

    private void Apply(IReadOnlyList<RouterModel> models)
    {
        IRouterHandle? router;
        lock (_gate)
        {
            router = _router;
            var known = _plan?.Models.Select(m => m.Id).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var m in models.Where(m => known.Contains(m.Id)))
            {
                _live.TryGetValue(m.Id, out var previous);
                var failed = m.Failed && m.Status == RouterModelStatus.Unloaded;
                // A failure stays on the model until the next try, even across a router restart.
                var error = failed ? previous?.Error ?? $"{NameOf(m.Id)} failed to load (engine exit code {Code(m.ExitCode)}).\n{(router is null ? "" : TailOf(router))}"
                    : m.Status == RouterModelStatus.Unloaded ? previous?.Error : null;
                var loading = m.Status == RouterModelStatus.Loading;
                // The list has no progress; an event's number is newer than the list's silence.
                _live[m.Id] = new Live(m.Status, loading ? previous?.Progress ?? 0 : null, error, m.Status is RouterModelStatus.Loaded or RouterModelStatus.Sleeping ? previous?.Context ?? ContextFromArgs(m.Args) : null);
            }
        }

        Publish();
    }

    /// <summary>The context a loaded model really has: the engine's own answer when the preset left it automatic.</summary>
    private async Task LearnContextAsync(RouterClient client, string id)
    {
        try
        {
            lock (_gate)
            {
                if (_live.TryGetValue(id, out var l) && l.Context is not null) return;
            }

            var n = await client.GetContextSizeAsync(id, _disposed.Token).ConfigureAwait(false);
            if (n is null) return;
            lock (_gate)
            {
                if (_live.TryGetValue(id, out var l) && l.Status is RouterModelStatus.Loaded or RouterModelStatus.Sleeping) _live[id] = l with { Context = n };
            }

            Publish();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            Log.Debug($"Could not learn the context of {id}: {ex.Message}");
        }
    }

    private static int? ContextFromArgs(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (args[i] is "-c" or "--ctx-size" or "-ctx" && int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) return n;
        }

        return null;
    }

    // ── Snapshots ────────────────────────────────────────────────────────────────────────────────

    private sealed record Live(RouterModelStatus Status, double? Progress, string? Error, int? Context);

    /// <summary>Builds the snapshot from the plan and the live states, swaps it in, and wakes waiters and listeners.</summary>
    private void Publish()
    {
        TaskCompletionSource signal;
        lock (_gate)
        {
            var list = new List<RunningModel>();
            foreach (var m in _plan?.Models ?? [])
            {
                _live.TryGetValue(m.Id, out var live);
                var state = live is null ? RunState.Unloaded : live.Status switch
                {
                    RouterModelStatus.Loading => RunState.Loading,
                    RouterModelStatus.Loaded => RunState.Loaded,
                    RouterModelStatus.Sleeping => RunState.Sleeping,
                    _ => live.Error is not null ? RunState.Failed : RunState.Unloaded,
                };
                list.Add(new RunningModel(m.Id, m.DisplayName, state, state == RunState.Loading ? live?.Progress : null, m.MmprojPath is not null, m.Reasons,
                    state is RunState.Loaded or RunState.Sleeping ? m.Context ?? live?.Context : m.Context, state == RunState.Failed ? live?.Error : null, m.Use.Chats, m.Use.Completes));
            }

            _models = list;
            signal = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        signal.TrySetResult();
        _notifications.Writer.TryWrite(true);
    }

    private void SetLive(string id, Live live)
    {
        lock (_gate) _live[id] = live;
        Publish();
    }

    private async Task NotifyLoopAsync()
    {
        await foreach (var _ in _notifications.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                Log.Warn($"A model listener threw: {ex.Message}");
            }
        }
    }

    private bool Known(string id)
    {
        lock (_gate) return _plan?.Models.Any(m => m.Id == id) == true;
    }

    private string NameOf(string id)
    {
        lock (_gate) return _plan?.Models.FirstOrDefault(m => m.Id == id)?.DisplayName ?? id;
    }

    private RunState StateOf(string id)
    {
        lock (_gate) return _models.FirstOrDefault(m => m.Id == id)?.State ?? RunState.Unloaded;
    }

    /// <summary>
    /// Waits for a load to end. A router that dies meanwhile ends it at once with that failure kept on the model: the supervisor
    /// restarts the router with the model unloaded, and a wait for "loaded or failed" would otherwise outlast the restart.
    /// </summary>
    private async Task WaitForLoadAsync(string id, CancellationToken ct)
    {
        var router = Router;
        var died = new TaskCompletionSource<RouterExit>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExit(object? sender, RouterExit exit)
        {
            if (!exit.Intentional) died.TrySetResult(exit);
        }

        if (router is not null) router.Exited += OnExit;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var wait = WaitForAsync(id, s => s is RunState.Loaded or RunState.Sleeping or RunState.Failed, TimeSpan.FromMinutes(30), stop.Token);
        try
        {
            if (router is not null && !router.IsRunning) died.TrySetResult(new RouterExit(null, false, false, null, 0));
            if (await Task.WhenAny(wait, died.Task).ConfigureAwait(false) == wait)
            {
                await wait.ConfigureAwait(false);
                return;
            }

            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await wait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ended above.
            }

            var exit = died.Task.Result;
            lock (_gate)
            {
                _live.TryGetValue(id, out var previous);
                if (previous?.Error is null)
                {
                    _live[id] = new Live(RouterModelStatus.Unloaded, null, $"The engine stopped while loading this model (exit code {Code(exit.Code)}).\n{TailOf(router!)}", null);
                }
            }

            Publish();
        }
        finally
        {
            if (router is not null) router.Exited -= OnExit;
        }
    }

    /// <summary>Waits until the model's state satisfies <paramref name="done"/>; events wake it, and every few seconds it reads the list itself in case one was missed.</summary>
    private async Task WaitForAsync(string id, Func<RunState, bool> done, TimeSpan timeout, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        try
        {
            while (true)
            {
                Task signal;
                lock (_gate) signal = _changed.Task;
                if (done(StateOf(id))) return;
                await Task.WhenAny(signal, Task.Delay(Tuning.PollInterval, limit.Token)).ConfigureAwait(false);
                limit.Token.ThrowIfCancellationRequested();
                if (!signal.IsCompleted) await SyncQuietlyAsync(limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChatException($"{NameOf(id)} did not finish in {timeout.TotalSeconds:0} s.\n{RouterHint()}");
        }
    }
}

/// <summary>How patient the runtime is. The defaults suit a person; tests shorten them.</summary>
internal sealed record RuntimeTuning
{
    public TimeSpan HealthTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public TimeSpan? RestartDelayBase { get; init; }

    public int MaxConsecutiveFailures { get; init; } = 8;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How long a process waits for the router another process is starting before it gives up.</summary>
    public TimeSpan AttachTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How often an attached router checks that its router and owner are still there.</summary>
    public TimeSpan AttachedPoll { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a dead router is given to be restarted by an owner that still holds the lock (the supervisor waits up to 30 s).</summary>
    public TimeSpan OwnerGrace { get; init; } = TimeSpan.FromSeconds(45);
}
