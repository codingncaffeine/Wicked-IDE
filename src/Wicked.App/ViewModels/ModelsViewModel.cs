using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Models;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Catalog;
using Wicked.Core.Chat;
using Wicked.Core.Hub;
using Wicked.Core.Models;
using Wicked.Core.Settings;
using Wicked.Core.Store;

namespace Wicked.App.ViewModels;

/// <summary>One recommendation card (D25).</summary>
public sealed partial class PickCard : ObservableObject
{
    private readonly ModelsViewModel _owner;

    public PickCard(Pick pick, ModelsViewModel owner)
    {
        Pick = pick;
        _owner = owner;
        Item = owner.Item(pick.Model.Repo);
        Item.Name = pick.Model.Name;
        Item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallItem.Busy)) OnPropertyChanged(nameof(CanInstall));
        };
    }

    public Pick Pick { get; }

    /// <summary>The install this card shows and controls (shared with the Browse page).</summary>
    public InstallItem Item { get; }

    public string Role => (Pick.AlsoFor.Count > 0 ? string.Join(" · ", new[] { Pick.Kind }.Concat(Pick.AlsoFor)) : Pick.Kind.ToString()).ToUpperInvariant();

    public string Name => Pick.Model.Name;

    public string Quant => Pick.Tier.Quant;

    public string Reason => Pick.Reason;

    public string Details => $"{ModelsViewModel.Size(Pick.Tier.Size + (Pick.Companion?.Tier.Size ?? 0))} · {Pick.Model.LicenseId}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installed;

    public bool CanInstall => !Installed && !Item.Busy;

    [RelayCommand]
    private Task Install() => _owner.RequestInstallAsync(new InstallJob(
        Pick.Model.Repo, Pick.Model.Name, Pick.Model.LicenseId, Pick.Model.LicenseUrl, Pick.Model.Repo,
        accepted => _owner.Services.InstallAsync(Pick, default, accepted)));
}

/// <summary>What to install, and how: the licence to check first, then the call that does the work.</summary>
public sealed record InstallJob(string Id, string Name, string? LicenseId, string? LicenseUrl, string? HubRepo, Func<bool, Task> Run);

/// <summary>An installed model in the list, with its menu (show in folder, delete) and the update flag from the last check.</summary>
public sealed partial class InstalledRow(string id, ModelsViewModel owner) : ObservableObject
{
    public string Id { get; } = id;

    public string Name { get; private set; } = "";

    public string Directory { get; private set; } = "";

    [ObservableProperty]
    private string _details = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private string? _updateText;

    public bool HasUpdate => UpdateText is not null;

    // ── Keeping it up to date ──

    /// <summary>What happens when a newer upload appears: ask (the default), update by itself, or never look.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolicyText), nameof(IsNotify), nameof(IsAuto), nameof(IsPinned))]
    private UpdatePolicy _policy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate))]
    private bool _updating;

    private bool _updatable;

    public bool IsNotify => Policy == UpdatePolicy.Notify;

    public bool IsAuto => Policy == UpdatePolicy.Auto;

    public bool IsPinned => Policy == UpdatePolicy.Pin;

    public string PolicyText => Policy switch { UpdatePolicy.Auto => "Updates itself", UpdatePolicy.Pin => "Pinned: never checked", _ => "Asks before updating" };

    /// <summary>The newer upload can be applied by Wicked (its files are the same names).</summary>
    public bool CanUpdate => _updatable && !Updating;

    [RelayCommand]
    private Task Update() => owner.UpdateAsync(this);

    [RelayCommand]
    private Task SetPolicy(string value) => owner.SetPolicyAsync(this, UpdatePolicies.Parse(value));

    /// <summary>Opens "What's new in your models" on this model.</summary>
    [RelayCommand]
    private void WhatsNew() => owner.ShowWhatsNew();

    // ── Running it (the runtime's word on this model) ──

    /// <summary>The runtime's view of this model, or null when it does not list it (nothing to load it with).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(CanLoad), nameof(CanUnload), nameof(IsLoading), nameof(IsLoaded), nameof(IsFailed), nameof(StateText), nameof(HasState))]
    private RunningModel? _run;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsFailed))]
    private string? _loadError;

    public bool CanRun => Run is not null;

    public bool HasState => Run is not null;

    public bool IsLoading => Run?.State == RunState.Loading;

    public bool IsLoaded => Run?.State is RunState.Loaded or RunState.Sleeping;

    public bool IsFailed => LoadError is not null || Run?.State == RunState.Failed;

    public bool CanLoad => Run is { State: RunState.Unloaded or RunState.Failed };

    public bool CanUnload => Run is { State: RunState.Loaded or RunState.Sleeping };

    public double LoadProgress => Run?.LoadProgress ?? 0;

    public bool LoadProgressKnown => Run?.LoadProgress is not null;

    public string StateText => Run is null ? "" : IsFailed
        ? "Failed: " + (LoadError ?? Run.Error ?? "the model did not load")
        : Run.State switch
        {
            RunState.Loaded => "Loaded",
            RunState.Sleeping => "Loaded, asleep",
            RunState.Loading => Run.LoadProgress is { } p ? $"Loading {p.ToString("0%", CultureInfo.InvariantCulture)}" : "Loading…",
            _ => "Not loaded",
        };

    /// <summary>The id the runtime knows this model by: its variant (<c>repo:Q4_K_M</c>) once matched, else the stored id.</summary>
    public string RunId => Run?.Id ?? Id;

    public void SetRun(RunningModel? run)
    {
        Run = run;
        if (run?.State is RunState.Loading or RunState.Loaded) LoadError = null;
        OnPropertyChanged(nameof(LoadProgress));
        OnPropertyChanged(nameof(LoadProgressKnown));
    }

    [RelayCommand]
    private Task Load() => owner.LoadAsync(this);

    [RelayCommand]
    private Task Unload() => owner.UnloadAsync(this);

    [RelayCommand]
    private Task RunSettings() => owner.OpenRunSettingsAsync(this);

    public void Set(StoredModel model, ModelUpdate? update)
    {
        Name = model.Id.Contains('/') ? model.Id[(model.Id.IndexOf('/') + 1)..] : model.Id; // not a path: a model id
        Directory = model.Directory;
        OnPropertyChanged(nameof(Name));
        var quants = string.Join(", ", model.Variants.Select(v => v.Quant));
        Details = $"{quants} · {ModelsViewModel.Size(model.SizeOnDisk)} on disk";
        UpdateText = update?.Detail;
        _updatable = update?.CanApply == true;
        OnPropertyChanged(nameof(CanUpdate));
        Policy = owner.Services.PolicyOf(model);
    }

    [RelayCommand]
    private void ShowInFolder() => owner.ShowInFolder(Directory);

    [RelayCommand]
    private void Delete() => owner.AskDelete(this);
}

/// <summary>
/// The Models panel: this machine, the recommendations, what is installed and the engine. The services do their work on
/// workers; their state arrives here posted to the UI thread. Installs report to the footer as well, so they stay
/// visible with the panel closed, and a click there comes back here.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly NoticeBoard _notices;
    private readonly Dictionary<string, InstallItem> _items = new(StringComparer.Ordinal);
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// <summary>Lets a test supply services wired to local stand-ins; the app leaves it null.</summary>
    public static Func<WickedPaths, ModelServices>? ServicesFactory { get; set; }

    public ModelsViewModel(WickedPaths paths, NoticeBoard notices)
    {
        _notices = notices;
        Services = ServicesFactory?.Invoke(paths) ?? new ModelServices(paths);
        Services.Updates.Notices = notices;
        Services.Changed += state => Dispatcher.UIThread.Post(() => Apply(state));
        Services.Progress += p => Dispatcher.UIThread.Post(() => OnProgress(p));
        Browse = new BrowseViewModel(this);
    }

    /// <summary>The Browse page: the whole catalog with fit badges, and Hugging Face search.</summary>
    public BrowseViewModel Browse { get; }

    [RelayCommand]
    private void OpenBrowse() => Browse.Open();

    public ModelServices Services { get; }

    // ── Running models ───────────────────────────────────────────────────────────────────────────

    private IModelRuntime? _runtime;

    /// <summary>The runtime that loads and unloads models. Its changes arrive on a worker and are applied here, on the UI thread.</summary>
    public void AttachRuntime(IModelRuntime runtime)
    {
        _runtime = runtime;
        runtime.Changed += (_, _) => Dispatcher.UIThread.Post(ApplyRuntime);
        ApplyRuntime();
    }

    /// <summary>The loaded model(s) and their state, for the status bar.</summary>
    [ObservableProperty]
    private string _runtimeStatus = "";

    [ObservableProperty]
    private bool _hasRuntimeStatus;

    [ObservableProperty]
    private bool _runtimeLoaded;

    [ObservableProperty]
    private bool _runtimeLoading;

    [ObservableProperty]
    private bool _runtimeFailed;

    /// <summary>The Run settings sheet for one model, or null.</summary>
    [ObservableProperty]
    private RunSettingsViewModel? _runSettings;

    private void ApplyRuntime()
    {
        if (_runtime is null) return;
        var snapshot = _runtime.Models;
        foreach (var row in Installed) row.SetRun(RunFor(snapshot, row.Id));
        var loaded = snapshot.Where(m => m.State is RunState.Loaded or RunState.Sleeping).ToList();
        var loading = snapshot.FirstOrDefault(m => m.State == RunState.Loading);
        var failed = snapshot.FirstOrDefault(m => m.State == RunState.Failed);
        HasRuntimeStatus = snapshot.Count > 0;
        RuntimeLoading = loading is not null;
        RuntimeFailed = loading is null && failed is not null && loaded.Count == 0;
        RuntimeLoaded = loaded.Count > 0 && loading is null;
        RuntimeStatus = loading is not null
            ? $"Loading {loading.DisplayName}{(loading.LoadProgress is { } p ? " " + p.ToString("0%", CultureInfo.InvariantCulture) : "…")}"
            : loaded.Count > 0 ? loaded.Count == 1 ? loaded[0].DisplayName : $"{loaded[0].DisplayName} +{loaded.Count - 1}"
            : failed is not null ? $"{failed.DisplayName} failed to load"
            : "No model loaded";
    }

    /// <summary>
    /// The runtime's entry for a stored model: the runtime names a model by its variant (<c>repo:Q4_K_M</c>) when the file has a
    /// quant, a row by the stored model; a loaded variant wins when a model has several.
    /// </summary>
    internal static RunningModel? RunFor(IReadOnlyList<RunningModel> snapshot, string modelId) =>
        snapshot.Where(m => m.Id == modelId || m.Id.StartsWith(modelId + ":", StringComparison.Ordinal))
            .OrderByDescending(m => m.State is RunState.Loaded or RunState.Sleeping or RunState.Loading)
            .FirstOrDefault();

    internal async Task LoadAsync(InstalledRow row)
    {
        if (_runtime is not { } runtime) return;
        row.LoadError = null;
        try
        {
            await Task.Run(() => runtime.LoadAsync(row.RunId));
        }
        catch (Exception ex)
        {
            Log.Warn($"Loading {row.Id} failed: {ex.Message}");
            row.LoadError = ex.Message;
            _notices.Post(new Notice($"load-failed:{row.Id}", $"{row.Name} could not be loaded: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
        }
    }

    internal async Task UnloadAsync(InstalledRow row)
    {
        if (_runtime is not { } runtime) return;
        try
        {
            await Task.Run(() => runtime.UnloadAsync(row.RunId));
        }
        catch (Exception ex)
        {
            Log.Warn($"Unloading {row.Id} failed: {ex.Message}");
            _notices.Post(new Notice($"unload-failed:{row.Id}", $"{row.Name} could not be unloaded: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
        }
    }

    internal async Task OpenRunSettingsAsync(InstalledRow row)
    {
        if (_runtime is not { } runtime) return;
        var current = await Task.Run(() => runtime.GetSettings(row.RunId));
        RunSettings = new RunSettingsViewModel(this, row, current, [.. Installed.Where(r => r.Id != row.Id).Select(r => new SettingChoice(r.RunId, r.Name))]);
    }

    internal async Task SaveRunSettingsAsync(string id, ModelRunSettings settings)
    {
        if (_runtime is not { } runtime) return;
        await Task.Run(() => runtime.SaveSettingsAsync(id, settings));
        RunSettings = null;
    }

    public ObservableCollection<PickCard> Picks { get; } = [];

    public ObservableCollection<InstalledRow> Installed { get; } = [];

    /// <summary>Opens a folder in the desktop's file manager (replaced in tests, which must not open windows).</summary>
    public Action<string> OpenPath { get; set; } = Opener.Open;

    [ObservableProperty]
    private string _hardwareText = "Looking at this PC…";

    [ObservableProperty]
    private string _engineText = "";

    [ObservableProperty]
    private bool _ready;

    /// <summary>The question being asked (a licence to accept, a delete to confirm), or null.</summary>
    [ObservableProperty]
    private ModelPrompt? _prompt;

    /// <summary>The last install problem, shown on the Browse page (its dim layer covers the footer notice), with where it is dealt with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAlert), nameof(AlertHasAction))]
    private string? _alert;

    private string? _alertRoute;

    public bool HasAlert => Alert is not null;

    public bool AlertHasAction => Alert is not null && _alertRoute is not null;

    /// <summary>Raised when something here wants the window to go to a place (see <c>ShellRoutes</c>).</summary>
    public event Action<string>? RouteRequested;

    [RelayCommand]
    private void OpenAlert()
    {
        if (_alertRoute is { } route) RouteRequested?.Invoke(route);
        Alert = null;
    }

    private void RaiseAlert(string text, string? route)
    {
        _alertRoute = route;
        Alert = text;
        OnPropertyChanged(nameof(AlertHasAction));
    }

    [ObservableProperty]
    private string _updateStatus = "";

    [ObservableProperty]
    private bool _checkingUpdates;

    /// <summary>The installs everything shows, by model id.</summary>
    public InstallItem Item(string id)
    {
        if (!_items.TryGetValue(id, out var item)) _items[id] = item = new InstallItem(id, this);
        return item;
    }

    /// <summary>Starts looking at the machine and the catalog (online unless offline mode is on).</summary>
    public Task StartAsync(bool allowNetwork) => Task.Run(async () =>
    {
        try
        {
            await Services.InitializeAsync(allowNetwork);
            if (allowNetwork) Services.Updates.Start();
        }
        catch (Exception ex)
        {
            Log.Error("The model services could not start", ex);
            Dispatcher.UIThread.Post(() => _notices.Post(new Notice("models", $"Models are unavailable: {ex.Message}", NoticeKind.Error, ShellRoutes.Models)));
        }
    });

    /// <summary>Puts the Settings page's model choices in force (token, speed cap, install folder, backend).</summary>
    public void Configure(WickedSettings s) => Services.Configure(ModelOptionsFile.From(s));

    private void Apply(ModelState state)
    {
        if (state.Hardware is { } hw)
        {
            var gpu = hw.PrimaryGpu;
            HardwareText = gpu is null
                ? $"No GPU found · {Size(hw.RamTotal)} RAM · models run on the CPU"
                : $"{gpu.Name} · {Size(gpu.VramTotal ?? 0)} · {Size(hw.RamTotal)} RAM";
        }

        EngineText = state.Engine is { } engine ? $"llama.cpp {engine.Id}" : "Installs itself with your first model";

        // Cards are kept while the same picks stand, so a card's progress survives a rescan.
        var keys = state.Picks.Select(p => $"{p.Model.Id}/{p.Tier.Quant}").ToList();
        if (!keys.SequenceEqual(Picks.Select(c => $"{c.Pick.Model.Id}/{c.Pick.Tier.Quant}")))
        {
            Picks.Clear();
            foreach (var pick in state.Picks) Picks.Add(new PickCard(pick, this));
        }

        foreach (var card in Picks) card.Installed = state.IsInstalled(card.Pick.Model, card.Pick.Tier);
        foreach (var item in _items.Values)
        {
            item.Busy = _running.Contains(item.Id) || state.Busy.Contains(item.Id);
            item.Paused = item.Busy && state.IsPaused(item.Id);
        }

        // Rows are kept by id, so a menu that is open survives the rescan.
        var wanted = state.Installed.Select(m => m.Id).ToList();
        for (var i = Installed.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(Installed[i].Id)) Installed.RemoveAt(i);
        }

        for (var i = 0; i < state.Installed.Count; i++)
        {
            var model = state.Installed[i];
            var row = Installed.FirstOrDefault(r => r.Id == model.Id);
            if (row is null) Installed.Insert(Math.Min(i, Installed.Count), row = new InstalledRow(model.Id, this));
            row.Set(model, state.UpdateFor(model.Id));
        }

        Ready = state.Catalog is not null;
        ApplyRuntime();
        Applied?.Invoke(state);
    }

    /// <summary>Raised on the UI thread after each state is applied (the Browse page follows it).</summary>
    public event Action<ModelState>? Applied;

    // ── Installing ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Asks for the licence first when the model's is not a permissive one, then installs.</summary>
    public async Task RequestInstallAsync(InstallJob job)
    {
        var item = Item(job.Id);
        if (_running.Contains(job.Id) || item.Busy) return;
        item.Name = job.Name;
        LicenceRequest? needed;
        try
        {
            needed = await Task.Run(() => Services.LicenceNeededAsync(job.Id, job.Name, job.LicenseId, job.LicenseUrl, job.HubRepo));
        }
        catch (ArgumentException ex)
        {
            item.Status = $"Failed: {ex.Message}";
            _notices.Post(new Notice($"install-failed:{job.Id}", $"{job.Name} could not be installed: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
            return;
        }

        if (needed is not null)
        {
            Prompt = LicencePrompt(job, needed);
            return;
        }

        await RunInstallAsync(job, accepted: false);
    }

    private ModelPrompt LicencePrompt(InstallJob job, LicenceRequest needed) => new(
        this,
        $"Accept the {needed.LicenseId} licence?",
        $"{needed.ModelName} is released under the {needed.LicenseId} licence. It is not one of the permissive licences (MIT, Apache-2.0, BSD), so it comes with terms of use. "
        + "Read them first; Wicked keeps a note that you accepted.",
        "Accept and install",
        () => RunInstallAsync(job, accepted: true),
        $"Read the {needed.LicenseId} licence",
        needed.Url);

    private async Task RunInstallAsync(InstallJob job, bool accepted)
    {
        var item = Item(job.Id);
        Alert = null;
        _running.Add(job.Id);
        item.Busy = true;
        item.Paused = false;
        item.Progress = 0;
        item.Status = "Starting…";
        _notices.Post(new Notice($"install:{job.Id}", $"Starting {job.Name}", NoticeKind.Progress, ShellRoutes.Models, 0));
        try
        {
            await Task.Run(() => job.Run(accepted));
            item.Status = "";
            _notices.Clear($"install:{job.Id}");
            _notices.Post(new Notice($"installed:{job.Id}", $"{job.Name} is installed and ready", NoticeKind.Success, ShellRoutes.Models));
        }
        catch (OperationCanceledException)
        {
            item.Status = "Cancelled";
            _notices.Clear($"install:{job.Id}");
            _notices.Post(new Notice($"installed:{job.Id}", $"Cancelled the install of {job.Name}", NoticeKind.Info, ShellRoutes.Models));
        }
        catch (LicenceNotAcceptedException ex)
        {
            _notices.Clear($"install:{job.Id}");
            item.Status = "Waiting for the licence";
            Prompt = LicencePrompt(job, ex.Request);
        }
        catch (Exception ex) when (ex is AuthRequiredException or GatedRepoException)
        {
            _notices.Clear($"install:{job.Id}");
            item.Status = ex is AuthRequiredException ? "Needs a Hugging Face token" : "Access not granted";
            var text = ex is AuthRequiredException
                ? $"{job.Name} is gated: add your Hugging Face token in Settings, then install it again"
                : $"Hugging Face has not given your account access to {job.Name}: accept its terms on the model's page, then install it again";
            _notices.Post(new Notice($"gated:{job.Id}", text, NoticeKind.Warning, ShellRoutes.SettingsModels));
            RaiseAlert(text, ShellRoutes.SettingsModels);
        }
        catch (Exception ex)
        {
            Log.Error($"Installing {job.Name} failed", ex);
            item.Status = $"Failed: {ex.Message}";
            _notices.Clear($"install:{job.Id}");
            _notices.Post(new Notice($"install-failed:{job.Id}", $"{job.Name} could not be installed: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
            RaiseAlert($"{job.Name} could not be installed: {ex.Message}", null);
        }
        finally
        {
            _running.Remove(job.Id);
            Apply(Services.State);
        }
    }

    private void OnProgress(InstallProgress p)
    {
        if (p.ModelId == "engine")
        {
            // The engine is fetched for whichever install is waiting on it.
            foreach (var id in _running)
            {
                if (Services.State.Busy.Contains(id)) continue;
                var waiting = Item(id);
                waiting.Status = p.Message;
                waiting.Progress = p.Fraction ?? 0;
            }

            _notices.Post(new Notice("install:engine", p.Message, NoticeKind.Progress, ShellRoutes.Models, p.Fraction));
            if (p.Fraction >= 1) _notices.Clear("install:engine");
            return;
        }

        var item = Item(p.ModelId);
        if (!_running.Contains(p.ModelId)) return; // a late word from an install that has finished or been cancelled
        if (p.Message == "Paused")
        {
            item.Status = "Paused";
            _notices.Post(new Notice($"install:{p.ModelId}", $"Paused · {item.Name}", NoticeKind.Progress, ShellRoutes.Models, item.Progress));
            return;
        }

        item.Status = p.Message;
        if (p.Fraction is { } f) item.Progress = f;
        _notices.Post(new Notice($"install:{p.ModelId}", p.Message, NoticeKind.Progress, ShellRoutes.Models, p.Fraction));
    }

    public async Task PauseAsync(InstallItem item)
    {
        if (!await Task.Run(() => Services.PauseInstallAsync(item.Id))) item.Status = "Not downloading yet";
    }

    public void Resume(InstallItem item)
    {
        Services.ResumeInstall(item.Id);
        item.Status = "Resuming…";
    }

    public async Task CancelAsync(InstallItem item)
    {
        item.Status = "Cancelling…";
        await Task.Run(() => Services.CancelInstallAsync(item.Id));
    }

    // ── Installed models ─────────────────────────────────────────────────────────────────────────

    public void ShowInFolder(string folder) => Task.Run(() => OpenPath(folder));

    public void OpenExternal(string url) => Task.Run(() => OpenPath(url));

    public void AskDelete(InstalledRow row)
    {
        Prompt = new ModelPrompt(
            this,
            $"Delete {row.Name}?",
            $"Its files ({row.Details}) are removed from {row.Directory}. This cannot be undone; installing it again downloads it again.",
            "Delete",
            () => DeleteAsync(row),
            danger: true);
    }

    private async Task DeleteAsync(InstalledRow row)
    {
        try
        {
            await Task.Run(() => Services.DeleteAsync(row.Id));
            _notices.Post(new Notice($"deleted:{row.Id}", $"Deleted {row.Name}", NoticeKind.Success, ShellRoutes.Models));
        }
        catch (Exception ex)
        {
            Log.Error($"Deleting {row.Id} failed", ex);
            _notices.Post(new Notice($"delete-failed:{row.Id}", $"{row.Name} could not be deleted: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
        }
    }

    /// <summary>Looks at everything that can have an update, now: the models' repos, the catalog, the engine and the app. The status line says what was found about the models.</summary>
    [RelayCommand]
    private async Task CheckUpdates()
    {
        CheckingUpdates = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            UpdateStatus = await CheckNowAsync();
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    /// <summary>The scheduler's look, run now (on a worker); returns the sentence that says how it went.</summary>
    public async Task<string> CheckNowAsync()
    {
        try
        {
            var result = await Task.Run(() => Services.Updates.CheckNowAsync());
            if (result.Skipped is { } why)
            {
                return why switch
                {
                    SweepSkip.Offline => "Offline mode is on, so nothing was asked.",
                    SweepSkip.NoNetwork => "There is no network right now.",
                    SweepSkip.Busy => "Another Wicked process is looking right now.",
                    _ => "Update checks are off.",
                };
            }

            var models = result.Models!;
            var note = models.Failed > 0 ? $" · could not reach {models.Failed}" : "";
            return models.Problem is { } problem ? problem
                : Services.State.Installed.Count == 0 ? "Nothing installed to check."
                : models.Found.Count == 0 ? $"Everything is up to date{note}"
                : $"{models.Found.Count} update{(models.Found.Count == 1 ? "" : "s")} available{note}";
        }
        catch (Exception ex)
        {
            Log.Error("The update check failed", ex);
            return $"Could not check: {ex.Message}";
        }
    }

    internal async Task UpdateAsync(InstalledRow row)
    {
        row.Updating = true;
        try
        {
            await Task.Run(() => Services.Updates.PerformAsync("model-update:" + row.Id, "update"));
        }
        catch (Exception ex)
        {
            Log.Warn($"Updating {row.Id} failed: {ex.Message}");
            _notices.Post(new Notice("model-update:" + row.Id, $"{row.Name} could not be updated: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
        }
        finally
        {
            row.Updating = false;
        }
    }

    internal async Task SetPolicyAsync(InstalledRow row, UpdatePolicy? policy)
    {
        try
        {
            await Task.Run(() => Services.SetUpdatePolicyAsync(row.Id, policy));
        }
        catch (Exception ex) when (ex is IOException or FileNotFoundException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not set the update policy of {row.Id}: {ex.Message}");
            _notices.Post(new Notice("policy:" + row.Id, $"{row.Name}: the update setting could not be saved ({ex.Message})", NoticeKind.Error, ShellRoutes.Models));
        }
    }

    /// <summary>Raised when a row asks for "What's new in your models".</summary>
    public event Action? WhatsNewRequested;

    internal void ShowWhatsNew() => WhatsNewRequested?.Invoke();

    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : bytes >= 1L << 20
            ? (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB"
            : (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
