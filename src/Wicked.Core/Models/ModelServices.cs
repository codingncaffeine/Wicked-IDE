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
public sealed record ModelState(
    HardwareInfo? Hardware,
    CatalogLoad? Catalog,
    IReadOnlyList<StoredModel> Installed,
    EngineInstall? Engine,
    IReadOnlyList<Pick> Picks,
    IReadOnlySet<string> Busy)
{
    public static readonly ModelState Empty = new(null, null, [], null, [], new HashSet<string>());

    public bool IsInstalled(CatalogModel model, QuantTier tier) =>
        Installed.Any(m => m.Id == model.Repo && m.Variants.Any(v => string.Equals(v.Quant, tier.Quant, StringComparison.OrdinalIgnoreCase)));
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
    private ModelState _state = ModelState.Empty;

    public ModelServices(WickedPaths paths, HttpClient? http = null, Uri? hubBase = null, Uri? catalogBase = null, Uri? githubBase = null)
    {
        _paths = paths;
        _http = http ?? WickedHttp.CreateClient();
        _hub = new HubClient(_http, baseAddress: hubBase);
        _catalog = new CatalogService(_http, Path.Combine(paths.Cache, "catalog"), catalogBase);
        _releases = new EngineReleases(_http, cacheFile: Path.Combine(paths.Cache, "engine-releases.json"), apiBase: githubBase);
        Store = new ModelStore(Path.Combine(paths.Data, "models"));
        Downloads = new DownloadManager(_http, Path.Combine(paths.State, "downloads.json"));
        Engines = new EngineInstaller(Downloads, Path.Combine(paths.Data, "engines"), Path.Combine(paths.Cache, "engine-downloads"));
    }

    public ModelStore Store { get; }

    public DownloadManager Downloads { get; }

    public EngineInstaller Engines { get; }

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

    /// <summary>Detects the hardware, loads the catalog (online when allowed, else the built-in copy), scans the store, and recommends.</summary>
    public async Task InitializeAsync(bool allowNetwork, CancellationToken ct = default)
    {
        var hardware = await HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        Update(s => s with { Hardware = hardware });
        await Downloads.LoadAsync(ct).ConfigureAwait(false);

        var catalog = await _catalog.LoadAsync(allowNetwork, ct).ConfigureAwait(false);
        if (catalog.Note is { } note) Log.Info($"Catalog: {note}");
        Update(s => s with { Catalog = catalog, Picks = Recommender.Recommend(hardware, catalog.Catalog) });
        await RescanAsync(ct).ConfigureAwait(false);
        Log.Info($"Models: {hardware.PrimaryGpu?.Name ?? "no GPU"}, catalog {catalog.Origin} ({catalog.Catalog.Models.Count()} models), {State.Picks.Count} picks, {State.Installed.Count} installed, engine {State.Engine?.Id ?? "none"}");
    }

    public async Task RescanAsync(CancellationToken ct = default)
    {
        var installed = await Store.ScanAsync(ct).ConfigureAwait(false);
        var engine = Engines.Current;
        Update(s => s with { Installed = installed, Engine = engine });
    }

    /// <summary>The engine for this machine: the newest llama.cpp build for the first backend that has one, installed and made current.</summary>
    public async Task<EngineInstall> EnsureEngineAsync(CancellationToken ct = default)
    {
        if (Engines.Current is { } current) return current;
        var hardware = State.Hardware ?? await HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        Busy("engine", true);
        try
        {
            var releases = await _releases.ListAsync(10, ct).ConfigureAwait(false);
            foreach (var choice in EngineSelector.Candidates(hardware, EnginePlatform.Current))
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

    /// <summary>Installs a pick: the engine if needed, then the model's files (verified), then its manifest; its companion too.</summary>
    public async Task InstallAsync(Pick pick, CancellationToken ct = default)
    {
        await EnsureEngineAsync(ct).ConfigureAwait(false);
        await InstallModelAsync(pick.Model, pick.Tier, ct).ConfigureAwait(false);
        if (pick.Companion is { } companion) await InstallModelAsync(companion.Model, companion.Tier, ct).ConfigureAwait(false);
    }

    public async Task InstallModelAsync(CatalogModel model, QuantTier tier, CancellationToken ct = default)
    {
        var id = model.Repo;
        Busy(id, true);
        try
        {
            var total = tier.Files.Sum(f => f.Size);
            long before = 0;
            foreach (var file in tier.Files)
            {
                var request = new DownloadRequest(_hub.ResolveUrl(model.Repo, model.Revision, file.Path), Store.PathFor(id, file.Path), file.Size, file.Sha256, Title: $"{model.Name} {tier.Quant}");
                var job = Downloads.Enqueue(request);
                var done = before;
                void OnChanged(object? sender, DownloadSnapshot s)
                {
                    if (s.Id != job) return;
                    var fraction = total > 0 ? (done + s.BytesDone) / (double)total : (double?)null;
                    var rate = s.BytesPerSecond > 0 ? $", {s.BytesPerSecond / 1_048_576:0.0} MB/s" : "";
                    Progress?.Invoke(new InstallProgress(id, $"Downloading {model.Name} {tier.Quant}{rate}", fraction));
                }

                Downloads.Changed += OnChanged;
                try
                {
                    var result = await Downloads.WaitAsync(job, ct).ConfigureAwait(false);
                    if (result.State != DownloadState.Done) throw new IOException(result.Error ?? $"The download {result.State.ToString().ToLowerInvariant()}");
                }
                finally
                {
                    Downloads.Changed -= OnChanged;
                }

                before += file.Size;
            }

            await Store.RegisterAsync(id, model.Repo, model.Revision, tier.Files.Select(f => (f.Path, f.Sha256)), model.LicenseId, DateTimeOffset.Now, model.Id, ct).ConfigureAwait(false);
            Progress?.Invoke(new InstallProgress(id, $"{model.Name} is installed", 1));
            await RescanAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Busy(id, false);
        }
    }

    public async Task DeleteAsync(string modelId, CancellationToken ct = default)
    {
        await Store.DeleteAsync(modelId, ct).ConfigureAwait(false);
        await RescanAsync(ct).ConfigureAwait(false);
    }

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
