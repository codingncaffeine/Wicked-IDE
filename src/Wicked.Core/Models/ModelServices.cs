using System.Net.Http.Headers;
using System.Text.Json;
using Wicked.Core.Catalog;
using Wicked.Core.Downloads;
using Wicked.Core.Engines;
using Wicked.Core.Hardware;
using Wicked.Core.Http;
using Wicked.Core.Hub;
using Wicked.Core.Store;

namespace Wicked.Core.Models;

/// <summary>What the model half knows right now: one immutable picture the window can bind to.</summary>
/// <param name="Hardware">This machine, once detected.</param>
/// <param name="Catalog">The signed catalog in force, and where it came from.</param>
/// <param name="Installed">The models in the store.</param>
/// <param name="Engine">The active llama.cpp build, or null before one is installed.</param>
/// <param name="Picks">The two or three recommendations for this machine (D25).</param>
/// <param name="Busy">What is being installed: model ids and "engine".</param>
/// <param name="Paused">The installs whose download the user paused.</param>
/// <param name="Updates">Installed models with something newer, by model id (from the last check).</param>
public sealed record ModelState(
    HardwareInfo? Hardware,
    CatalogLoad? Catalog,
    IReadOnlyList<StoredModel> Installed,
    EngineInstall? Engine,
    IReadOnlyList<Pick> Picks,
    IReadOnlySet<string> Busy,
    IReadOnlySet<string>? Paused = null,
    IReadOnlyDictionary<string, ModelUpdate>? Updates = null)
{
    public static readonly ModelState Empty = new(null, null, [], null, [], new HashSet<string>());

    public bool IsInstalled(CatalogModel model, QuantTier tier) =>
        Installed.Any(m => m.Id == model.Repo && m.Variants.Any(v => string.Equals(v.Quant, tier.Quant, StringComparison.OrdinalIgnoreCase)));

    public bool IsPaused(string id) => Paused?.Contains(id) == true;

    public ModelUpdate? UpdateFor(string modelId) => Updates is not null && Updates.TryGetValue(modelId, out var u) ? u : null;
}

/// <summary>An install in progress: what is happening and how far along, for the footer and the card.</summary>
public sealed record InstallProgress(string ModelId, string Message, double? Fraction);

/// <summary>
/// The model half in one place: hardware, the catalog, the store, the engine, the recommendations, and installing a
/// pick (the engine first when there is none, then the model's files, verified, then its manifest). Every method runs
/// its work on workers; <see cref="Changed"/> is raised from whichever thread changed the state.
/// </summary>
/// <remarks>
/// Hosted in the window process for now; the daemon (D10, Phase 6) will host this same class behind its local API, so
/// the window and the command line share one store and one engine.
/// </remarks>
public sealed class ModelServices : IAsyncDisposable
{
    private readonly WickedPaths _paths;
    private readonly HttpClient _http;
    private readonly HubClient _hub;
    private readonly CatalogService _catalog;
    private readonly EngineReleases _releases;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Session> _sessions = [];
    private readonly Dictionary<string, ModelStore> _stores = new(StringComparer.Ordinal);
    private readonly HashSet<string> _roots = new(StringComparer.Ordinal);
    private readonly string _defaultRoot;
    private ModelState _state = ModelState.Empty;
    private ModelStore _store;
    private string? _token;
    private EngineBackendKind? _backend;
    private bool _rootsDirty;

    public ModelServices(WickedPaths paths, HttpClient? http = null, Uri? hubBase = null, Uri? catalogBase = null, Uri? githubBase = null, string? catalogKey = null)
    {
        _paths = paths;
        _http = http ?? WickedHttp.CreateClient();
        _hub = new HubClient(_http, () => Token, baseAddress: hubBase);
        _catalog = new CatalogService(_http, Path.Combine(paths.Cache, "catalog"), catalogBase, catalogKey);
        _releases = new EngineReleases(_http, cacheFile: Path.Combine(paths.Cache, "engine-releases.json"), apiBase: githubBase);
        _defaultRoot = Path.GetFullPath(Path.Combine(paths.Data, "models"));
        _store = StoreAt(_defaultRoot);
        Downloads = new DownloadManager(_http, Path.Combine(paths.State, "downloads.json"), new DownloadOptions { TokenProvider = () => Token });
        Engines = new EngineInstaller(Downloads, Path.Combine(paths.Data, "engines"), Path.Combine(paths.Cache, "engine-downloads"));
    }

    /// <summary>Where new installs go: the store the Settings page chose, else the default one.</summary>
    public ModelStore Store
    {
        get
        {
            lock (_gate) return _store;
        }
    }

    public DownloadManager Downloads { get; }

    public EngineInstaller Engines { get; }

    /// <summary>The Hugging Face token in force (settings, else <c>HF_TOKEN</c>), or null.</summary>
    public string? Token
    {
        get
        {
            string? token;
            lock (_gate) token = _token;
            return token is { Length: > 0 } ? token
                : Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } e ? e : null;
        }
    }

    public ModelState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    /// <summary>Raised after the state changed, on the thread that changed it.</summary>
    public event Action<ModelState>? Changed;

    /// <summary>Raised as installs progress, on the thread doing the work.</summary>
    public event Action<InstallProgress>? Progress;

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the Settings page's choices in force: the token, the speed cap, the folder new installs go to, the backend.
    /// Cheap and callable from any thread (no disk work); models already installed stay where they are and stay listed.
    /// </summary>
    public void Configure(ModelOptions options)
    {
        var location = string.IsNullOrWhiteSpace(options.StoreLocation) ? null : Path.GetFullPath(options.StoreLocation);
        bool storeChanged;
        lock (_gate)
        {
            _token = options.HuggingFaceToken?.Trim();
            _backend = options.Backend;
            var root = location ?? _defaultRoot;
            storeChanged = !string.Equals(_store.Root, root, StringComparison.Ordinal);
            if (storeChanged)
            {
                _store = StoreAt(root);
                if (_roots.Add(root)) _rootsDirty = true;
            }
        }

        Downloads.BandwidthLimit = options.BandwidthBytesPerSecond is > 0 ? options.BandwidthBytesPerSecond : null;
        if (storeChanged) Log.Info($"Models: new installs go to {Store.Root}");
    }

    private ModelStore StoreAt(string root)
    {
        lock (_gate)
        {
            if (!_stores.TryGetValue(root, out var store)) _stores[root] = store = new ModelStore(root);
            return store;
        }
    }

    /// <summary>The store that holds <paramref name="model"/> (models stay where they were installed when the location changes).</summary>
    public ModelStore StoreFor(StoredModel model)
    {
        lock (_gate)
        {
            foreach (var store in _stores.Values.OrderByDescending(s => s.Root.Length))
            {
                if (model.Directory.StartsWith(store.Root, StringComparison.Ordinal)) return store;
            }

            return _store;
        }
    }

    // ── Start-up ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Detects the hardware, loads the catalog (online when allowed, else the built-in copy), scans the store, and recommends.</summary>
    public async Task InitializeAsync(bool allowNetwork, CancellationToken ct = default)
    {
        var hardware = await HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        Update(s => s with { Hardware = hardware });
        await Downloads.LoadAsync(ct).ConfigureAwait(false);
        await LoadRootsAsync(ct).ConfigureAwait(false);

        var catalog = await _catalog.LoadAsync(allowNetwork, ct).ConfigureAwait(false);
        if (catalog.Note is { } note) Log.Info($"Catalog: {note}");
        Update(s => s with { Catalog = catalog, Picks = Recommender.Recommend(hardware, catalog.Catalog) });
        await RescanAsync(ct).ConfigureAwait(false);
        Log.Info($"Models: {hardware.PrimaryGpu?.Name ?? "no GPU"}, catalog {catalog.Origin} ({catalog.Catalog.Models.Count()} models), {State.Picks.Count} picks, {State.Installed.Count} installed, engine {State.Engine?.Id ?? "none"}");
    }

    private string RootsFile => Path.Combine(_paths.State, "model-locations.json");

    private async Task LoadRootsAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(RootsFile)) return;
            var roots = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(RootsFile, ct).ConfigureAwait(false)) ?? [];
            lock (_gate)
            {
                foreach (var root in roots.Where(r => Path.IsPathRooted(r))) _roots.Add(Path.GetFullPath(root));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Could not read the list of model folders: {ex.Message}");
        }
    }

    private async Task SaveRootsAsync(CancellationToken ct)
    {
        string[] roots;
        lock (_gate)
        {
            if (!_rootsDirty) return;
            _rootsDirty = false;
            roots = [.. _roots.Order(StringComparer.Ordinal)];
        }

        try
        {
            Directory.CreateDirectory(_paths.State);
            await File.WriteAllTextAsync(RootsFile, JsonSerializer.Serialize(roots), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not save the list of model folders: {ex.Message}");
        }
    }

    /// <summary>Scans the install folder, the default folder and every folder models were ever installed to.</summary>
    public async Task RescanAsync(CancellationToken ct = default)
    {
        await SaveRootsAsync(ct).ConfigureAwait(false);
        List<ModelStore> stores;
        lock (_gate)
        {
            var roots = new List<string> { _store.Root, _defaultRoot };
            roots.AddRange(_roots);
            stores = [.. roots.Distinct(StringComparer.Ordinal).Where(Directory.Exists).Select(StoreAt)];
        }

        var found = new Dictionary<string, StoredModel>(StringComparer.Ordinal);
        foreach (var store in stores)
        {
            foreach (var model in await store.ScanAsync(ct).ConfigureAwait(false)) found.TryAdd(model.Id, model);
        }

        var installed = found.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
        var engine = Engines.Current;
        Update(s => s with { Installed = installed, Engine = engine });
    }

    // ── Engine ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>The engine for this machine: the newest llama.cpp build for the first backend that has one, installed and made current.</summary>
    public async Task<EngineInstall> EnsureEngineAsync(CancellationToken ct = default)
    {
        if (Engines.Current is { } current) return current;
        var hardware = State.Hardware ?? await HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        Busy("engine", true);
        try
        {
            var releases = await _releases.ListAsync(10, ct).ConfigureAwait(false);
            foreach (var choice in EngineSelector.Candidates(hardware, EnginePlatform.Current, _backend))
            {
                if (EngineReleases.Newest(releases, choice) is not { } release) continue;
                Progress?.Invoke(new InstallProgress("engine", $"Installing the {choice.Backend.DisplayName} engine ({release.Tag})", 0));
                var progress = new Relay<EngineInstallProgress>(p => Progress?.Invoke(new InstallProgress("engine", p.Message, p.Fraction)));
                var install = await Engines.InstallAsync(release, choice, progress, ct: ct).ConfigureAwait(false);
                Log.Info($"Engine installed: {install.Id} ({choice.Reason})");
                Update(s => s with { Engine = install });
                return install;
            }

            throw new EngineInstallException("No llama.cpp build in the recent releases fits this machine");
        }
        finally
        {
            Busy("engine", false);
        }
    }

    /// <summary>The engine in use, the one kept for rollback, what the engine says about itself and the builds this machine could run.</summary>
    public async Task<RuntimeStatus> GetRuntimeAsync(CancellationToken ct = default)
    {
        var hardware = State.Hardware ?? await HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        var current = Engines.Current;
        var report = current is null ? null : await EngineInstaller.ProbeAsync(current, ct).ConfigureAwait(false);
        EngineBackendKind? preferred;
        lock (_gate) preferred = _backend;
        return new RuntimeStatus(current, Engines.Previous, report, preferred, EngineSelector.Candidates(hardware, EnginePlatform.Current, preferred));
    }

    /// <summary>
    /// The newest release for the backend in force (the chosen one, else what this machine calls for) when it is not what is
    /// installed: a newer build of the same backend, or the build of a backend that was just chosen. Null when up to date.
    /// </summary>
    public async Task<EngineUpdate?> CheckEngineUpdateAsync(CancellationToken ct = default)
    {
        var hardware = State.Hardware ?? await HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        var current = Engines.Current;
        var releases = await _releases.ListAsync(10, ct).ConfigureAwait(false);
        EngineBackendKind? preferred;
        lock (_gate) preferred = _backend;
        foreach (var choice in EngineSelector.Candidates(hardware, EnginePlatform.Current, preferred))
        {
            if (EngineReleases.Newest(releases, choice) is not { } release) continue;
            var update = new EngineUpdate(current, release, choice);
            if (current is null) return update;
            if (current.Id == update.Id) return null;
            var sameBackend = current.Backend == choice.Backend.Slug;
            var currentBuild = EngineRelease.ParseBuild(current.Tag);
            if (sameBackend && currentBuild is { } have && release.Build <= have) return null;
            return update;
        }

        return null;
    }

    /// <summary>Installs <paramref name="update"/> and makes it current; the build it replaces is kept for <see cref="RollbackEngineAsync"/>.</summary>
    public async Task<EngineInstall> UpdateEngineAsync(EngineUpdate update, CancellationToken ct = default)
    {
        Busy("engine", true);
        try
        {
            var progress = new Relay<EngineInstallProgress>(p => Progress?.Invoke(new InstallProgress("engine", p.Message, p.Fraction)));
            var install = await Engines.InstallAsync(update.Release, update.Choice, progress, ct: ct).ConfigureAwait(false);
            Log.Info($"Engine updated: {update.From?.Id ?? "none"} -> {install.Id}");
            Update(s => s with { Engine = Engines.Current });
            return install;
        }
        finally
        {
            Busy("engine", false);
        }
    }

    public async Task<EngineInstall> RollbackEngineAsync()
    {
        var install = await Engines.RollbackAsync().ConfigureAwait(false);
        Log.Info($"Engine rolled back to {install.Id}");
        Update(s => s with { Engine = Engines.Current });
        return install;
    }

    // ── Installing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Installs a pick: the engine if needed, then the model's files (verified), then its manifest; its companion too.</summary>
    public async Task InstallAsync(Pick pick, CancellationToken ct = default, bool licenceAccepted = false)
    {
        using var session = Begin(pick.Model.Repo, ct);
        await EnsureEngineAsync(session.Token).ConfigureAwait(false);
        await InstallModelAsync(pick.Model, pick.Tier, licenceAccepted, session.Token).ConfigureAwait(false);
        if (pick.Companion is { } companion) await InstallModelAsync(companion.Model, companion.Tier, licenceAccepted, session.Token).ConfigureAwait(false);
    }

    /// <summary>Installs one catalog model at one quant, the engine first when there is none (what the Browse page's Install does).</summary>
    public async Task InstallCatalogAsync(CatalogModel model, QuantTier tier, bool licenceAccepted = false, CancellationToken ct = default)
    {
        using var session = Begin(model.Repo, ct);
        await EnsureEngineAsync(session.Token).ConfigureAwait(false);
        await InstallModelAsync(model, tier, licenceAccepted, session.Token).ConfigureAwait(false);
    }

    /// <summary>Installs one catalog model at one quant (the engine is the caller's business here).</summary>
    public Task InstallModelAsync(CatalogModel model, QuantTier tier, CancellationToken ct = default) =>
        InstallModelAsync(model, tier, licenceAccepted: false, ct);

    public Task InstallModelAsync(CatalogModel model, QuantTier tier, bool licenceAccepted, CancellationToken ct = default) =>
        InstallFilesAsync(
            model.Repo, model.Repo, model.Revision, tier.Files.Select(f => (f.Path, f.Size, f.Sha256)).ToList(), $"{model.Name} {tier.Quant}", model.Name,
            model.LicenseId, model.LicenseUrl, model.Gated, model.Id, licenceAccepted, ct);

    /// <summary>
    /// Installs one quant of a Hugging Face repo that is not in the catalog (the engine first when there is none): the repo's
    /// current commit, the variant's files with the hashes the Hub lists for them.
    /// </summary>
    public async Task InstallFromHubAsync(string repo, GgufVariant variant, HubModel? info = null, bool licenceAccepted = false, CancellationToken ct = default)
    {
        using var session = Begin(repo, ct);
        info ??= await _hub.GetModelAsync(repo, session.Token).ConfigureAwait(false);
        await EnsureEngineAsync(session.Token).ConfigureAwait(false);
        var name = repo[(repo.IndexOf('/') + 1)..]; // not a path: a repo id
        await InstallFilesAsync(
            repo, repo, info.Sha, variant.Files.Select(f => (f.Path, f.Size, f.LfsSha256)).ToList(), $"{name} {variant.Quant}", name,
            info.License, null, info.Gated != Gating.None, null, licenceAccepted, ct).ConfigureAwait(false);
    }

    /// <summary>The licence the user has to accept before this model may be downloaded, or null when it is permissive or was accepted before.</summary>
    public async Task<LicenceRequest?> LicenceNeededAsync(string modelId, string name, string? licenseId, string? licenseUrl, string? hubRepo, CancellationToken ct = default)
    {
        if (Licenses.IsPermissive(licenseId)) return null;
        foreach (var store in AllStores())
        {
            try
            {
                if (await store.ReadManifestAsync(modelId, ct).ConfigureAwait(false) is { AcceptedAt: not null }) return null;
            }
            catch (ArgumentException)
            {
                break;
            }
        }

        return new LicenceRequest(name, Licenses.Display(licenseId), Licenses.LinkFor(licenseId, licenseUrl, hubRepo));
    }

    private List<ModelStore> AllStores()
    {
        lock (_gate) return [_store, .. _stores.Values.Where(s => s != _store)];
    }

    private async Task InstallFilesAsync(
        string id, string repo, string revision, IReadOnlyList<(string Path, long Size, string? Sha256)> files, string title, string name,
        string? licenseId, string? licenseUrl, bool gated, string? catalogId, bool licenceAccepted, CancellationToken ct)
    {
        using var session = Begin(id, ct);
        var token = session.Token;
        var existedBefore = new List<string>();
        var fetched = new List<string>();
        Busy(id, true);
        try
        {
            if (!licenceAccepted && await LicenceNeededAsync(id, name, licenseId, licenseUrl, repo, token).ConfigureAwait(false) is { } needed)
            {
                throw new LicenceNotAcceptedException(needed);
            }

            if (gated && Token is null) throw new AuthRequiredException(repo);
            var store = Store;
            var total = files.Sum(f => f.Size);
            long before = 0;
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                var destination = store.PathFor(id, file.Path);
                if (File.Exists(destination)) existedBefore.Add(destination);
                else fetched.Add(destination);
                var request = new DownloadRequest(_hub.ResolveUrl(repo, revision, file.Path), destination, file.Size, file.Sha256, Authenticated: Token is not null, Title: title);
                var job = Downloads.Enqueue(request);
                session.Job = job;
                var done = before;
                void OnChanged(object? sender, DownloadSnapshot s)
                {
                    if (s.Id != job || s.State is DownloadState.Paused or DownloadState.Cancelled) return;
                    var fraction = total > 0 ? (done + s.BytesDone) / (double)total : (double?)null;
                    var rate = s.BytesPerSecond > 0 ? $", {s.BytesPerSecond / 1_048_576:0.0} MB/s" : "";
                    Progress?.Invoke(new InstallProgress(id, $"Downloading {title}{rate}", fraction));
                }

                Downloads.Changed += OnChanged;
                try
                {
                    var result = await Downloads.WaitAsync(job, token).ConfigureAwait(false);
                    if (result.State == DownloadState.Cancelled) throw new InstallCancelledException(id);
                    if (result.State != DownloadState.Done) throw DownloadFailure(repo, result);
                }
                finally
                {
                    Downloads.Changed -= OnChanged;
                }

                session.Job = null;

                before += file.Size;
            }

            await store.RegisterAsync(id, repo, revision, files.Select(f => (f.Path, f.Sha256)), licenseId, DateTimeOffset.Now, catalogId, token).ConfigureAwait(false);
            Progress?.Invoke(new InstallProgress(id, $"{name} is installed", 1));
            await RescanAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await AbandonAsync(id, session.Job, fetched).ConfigureAwait(false);
            throw new InstallCancelledException(id);
        }
        finally
        {
            Busy(id, false);
        }
    }

    private static Exception DownloadFailure(string repo, DownloadSnapshot result)
    {
        var error = result.Error ?? $"The download {result.State.ToString().ToLowerInvariant()}";
        if (error.Contains("answered 401", StringComparison.Ordinal)) return new AuthRequiredException(repo);
        if (error.Contains("answered 403", StringComparison.Ordinal)) return new GatedRepoException(repo);
        return new IOException(error);
    }

    /// <summary>A cancelled install leaves nothing behind: the running job goes (and its partial file), and so do shards this install had finished.</summary>
    private async Task AbandonAsync(string id, string? job, List<string> fetched)
    {
        if (job is not null) await Downloads.CancelAsync(job).ConfigureAwait(false);
        foreach (var path in fetched)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not remove {path}: {ex.Message}");
            }
        }

        foreach (var path in fetched.Select(Path.GetDirectoryName).Distinct(StringComparer.Ordinal).OfType<string>())
        {
            RemoveEmptyFolders(path, Store.Root);
        }

        Log.Info($"Install of {id} cancelled");
    }

    private static void RemoveEmptyFolders(string folder, string stop)
    {
        try
        {
            while (folder.StartsWith(stop, StringComparison.Ordinal) && folder.Length > stop.Length && Directory.Exists(folder)
                   && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
                folder = Path.GetDirectoryName(folder) ?? stop;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not tidy {folder}: {ex.Message}");
        }
    }

    // ── Pause, resume, cancel ────────────────────────────────────────────────────────────────────

    /// <summary>Pauses the download of an install (its partial file is kept). False when nothing is downloading for <paramref name="modelId"/>.</summary>
    public async Task<bool> PauseInstallAsync(string modelId)
    {
        string? job;
        lock (_gate) job = _sessions.TryGetValue(modelId, out var s) ? s.Job : null;
        if (job is null) return false;
        await Downloads.PauseAsync(job).ConfigureAwait(false);
        Update(s => s with { Paused = With(s.Paused, modelId, true) });
        Progress?.Invoke(new InstallProgress(modelId, "Paused", null));
        return true;
    }

    public bool ResumeInstall(string modelId)
    {
        string? job;
        lock (_gate) job = _sessions.TryGetValue(modelId, out var s) ? s.Job : null;
        if (job is null) return false;
        Update(s => s with { Paused = With(s.Paused, modelId, false) });
        Downloads.Resume(job);
        return true;
    }

    /// <summary>Stops an install, paused or running, and removes what it had downloaded.</summary>
    public async Task<bool> CancelInstallAsync(string modelId)
    {
        Session? session;
        lock (_gate) _sessions.TryGetValue(modelId, out session);
        if (session is null) return false;
        var job = session.Job;
        await session.Cts.CancelAsync().ConfigureAwait(false);
        if (job is not null) await Downloads.CancelAsync(job).ConfigureAwait(false);
        Update(s => s with { Paused = With(s.Paused, modelId, false) });
        return true;
    }

    private static IReadOnlySet<string> With(IReadOnlySet<string>? set, string id, bool present)
    {
        var next = new HashSet<string>(set ?? new HashSet<string>());
        if (present) next.Add(id);
        else next.Remove(id);
        return next;
    }

    private sealed class Session(CancellationTokenSource cts, Action<Session> release) : IDisposable
    {
        public CancellationTokenSource Cts { get; } = cts;

        public CancellationToken Token => Cts.Token;

        public volatile string? Job;
        public int Refs = 1;

        public void Dispose() => release(this);
    }

    /// <summary>The cancellable, pausable unit an install runs in; nested calls for the same model (a pick installing its model) share it.</summary>
    private Session Begin(string id, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var existing))
            {
                existing.Refs++;
                return existing;
            }

            var session = new Session(CancellationTokenSource.CreateLinkedTokenSource(ct), s =>
            {
                lock (_gate)
                {
                    if (--s.Refs > 0) return;
                    _sessions.Remove(id);
                }

                s.Cts.Dispose();
            });
            _sessions[id] = session;
            return session;
        }
    }

    // ── Installed models ─────────────────────────────────────────────────────────────────────────

    public async Task DeleteAsync(string modelId, CancellationToken ct = default)
    {
        var model = State.Installed.FirstOrDefault(m => m.Id == modelId);
        var store = model is null ? Store : StoreFor(model);
        await store.DeleteAsync(modelId, ct).ConfigureAwait(false);
        await RescanAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Asks the Hub whether each installed model's repo has a newer commit than the one installed (D7 channel a), and the catalog
    /// whether a newer model replaces it (channel b). A repo that cannot be reached is skipped and counted; nothing is replaced.
    /// </summary>
    public async Task<UpdateCheck> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var catalog = State.Catalog?.Catalog;
        var installed = State.Installed;
        var found = new List<ModelUpdate>();
        int checkedCount = 0, failed = 0;
        string? problem = null;
        foreach (var model in installed)
        {
            ct.ThrowIfCancellationRequested();
            if (model.Manifest is not { SourceRepo: { Length: > 0 } repo, Revision: { Length: > 0 } revision } manifest) continue;
            checkedCount++;
            try
            {
                var latest = await _hub.GetModelAsync(repo, ct).ConfigureAwait(false);
                if (latest.Sha.Length > 0 && !string.Equals(latest.Sha, revision, StringComparison.Ordinal))
                {
                    var when = latest.LastModified is { } m ? $" ({m.LocalDateTime:d MMM yyyy})" : "";
                    found.Add(new ModelUpdate(model.Id, UpdateKind.NewUpload, $"A newer upload of {repo} is available{when}", latest.Sha));
                    continue;
                }
            }
            catch (RateLimitedException ex)
            {
                problem = ex.Message;
                failed++;
                break;
            }
            catch (Exception ex) when (ex is HubException or HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                failed++;
                Log.Warn($"Update check for {repo}: {ex.Message}");
                continue;
            }

            if (catalog is not null && manifest.CatalogId is { } catalogId
                && catalog.Models.FirstOrDefault(c => c.Supersedes.Contains(catalogId, StringComparer.Ordinal)) is { } newer)
            {
                found.Add(new ModelUpdate(model.Id, UpdateKind.Superseded, $"Superseded by {newer.Name}"));
            }
        }

        Update(s => s with { Updates = found.ToDictionary(u => u.ModelId, StringComparer.Ordinal) });
        return new UpdateCheck(checkedCount, failed, found, problem);
    }

    // ── The Hugging Face browser ─────────────────────────────────────────────────────────────────

    /// <summary>GGUF repos matching <paramref name="query"/>, most downloaded first.</summary>
    public Task<IReadOnlyList<HubSearchResult>> SearchHubAsync(string query, CancellationToken ct = default) =>
        _hub.SearchAsync(query, "gguf", "downloads", 30, ct);

    /// <summary>
    /// A repo's quant variants and the shape its fit is judged from: the catalog's when it lists the repo, else the GGUF header read
    /// over an HTTP range request from the smallest variant (the first few MB, never the weights). Fit is unknown when neither works.
    /// </summary>
    public async Task<HubRepoDetail> GetHubRepoAsync(string repo, CancellationToken ct = default)
    {
        var model = await _hub.GetModelAsync(repo, ct).ConfigureAwait(false);
        var files = await _hub.ListFilesAsync(repo, model.Sha.Length > 0 ? model.Sha : "main", ct).ConfigureAwait(false);
        var variants = GgufNames.Variants(files);

        var known = State.Catalog?.Catalog.Models.FirstOrDefault(m => string.Equals(m.Repo, repo, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return new HubRepoDetail(model, variants, known.Shape, known.ContextLength, "catalog");
        if (variants.Count == 0) return new HubRepoDetail(model, variants, null, 0, "no GGUF files");

        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(25));
            var info = await ReadRemoteHeaderAsync(repo, model.Sha.Length > 0 ? model.Sha : "main", variants[0].Files[0].Path, limit.Token).ConfigureAwait(false);
            var shape = ModelShape.From(info);
            var context = (int)Math.Min(int.MaxValue, info.ContextLength ?? model.Gguf?.ContextLength ?? 0);
            return shape is null
                ? new HubRepoDetail(model, variants, null, context, "the file header does not say how big its cache is")
                : new HubRepoDetail(model, variants, shape, context > 0 ? context : 8192, "GGUF header");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or InvalidDataException or OperationCanceledException)
        {
            Log.Warn($"Could not read the GGUF header of {repo}: {ex.Message}");
            return new HubRepoDetail(model, variants, null, (int)Math.Min(int.MaxValue, model.Gguf?.ContextLength ?? 0), "the file header could not be read");
        }
    }

    private async Task<GgufInfo> ReadRemoteHeaderAsync(string repo, string revision, string file, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _hub.ResolveUrl(repo, revision, file));
        request.Headers.Range = new RangeHeaderValue(0, 32L * 1024 * 1024 - 1);
        if (Token is { } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return GgufInfo.From(await GgufReader.ReadHeaderAsync(stream, ct).ConfigureAwait(false));
        }
    }

    // ── State ────────────────────────────────────────────────────────────────────────────────────

    private void Busy(string id, bool busy) => Update(s =>
    {
        var set = new HashSet<string>(s.Busy);
        if (busy) set.Add(id);
        else set.Remove(id);
        return s with { Busy = set };
    });

    private void Update(Func<ModelState, ModelState> change)
    {
        ModelState next;
        lock (_gate) next = _state = change(_state);
        Changed?.Invoke(next);
    }

    public async ValueTask DisposeAsync() => await Downloads.DisposeAsync().ConfigureAwait(false);

    /// <summary>Progress delivered on the reporting thread (Progress&lt;T&gt; would post it to the pool, out of order).</summary>
    private sealed class Relay<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
