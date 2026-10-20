using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Models;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Catalog;
using Wicked.Core.Engines;
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

    public void Set(StoredModel model, ModelUpdate? update)
    {
        Name = model.Id.Contains('/') ? model.Id[(model.Id.IndexOf('/') + 1)..] : model.Id; // not a path: a model id
        Directory = model.Directory;
        OnPropertyChanged(nameof(Name));
        var quants = string.Join(", ", model.Variants.Select(v => v.Quant));
        Details = $"{quants} · {ModelsViewModel.Size(model.SizeOnDisk)} on disk";
        UpdateText = update?.Detail;
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
        Services.Changed += state => Dispatcher.UIThread.Post(() => Apply(state));
        Services.Progress += p => Dispatcher.UIThread.Post(() => OnProgress(p));
    }

    public ModelServices Services { get; }

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
        }
        catch (Exception ex)
        {
            Log.Error("The model services could not start", ex);
            Dispatcher.UIThread.Post(() => _notices.Post(new Notice("models", $"Models are unavailable: {ex.Message}", NoticeKind.Error, ShellRoutes.Models)));
        }
    });

    /// <summary>Puts the Settings page's model choices in force (token, speed cap, install folder, backend).</summary>
    public void Configure(WickedSettings s) => Services.Configure(new ModelOptions(
        s.HuggingFaceToken,
        s.DownloadLimit > 0 ? (long)(s.DownloadLimit * 1_048_576) : null,
        s.StoreLocation,
        Backend(s.RuntimeBackend)));

    public static EngineBackendKind? Backend(string? value) => value?.ToLowerInvariant() switch
    {
        "cuda" => EngineBackendKind.Cuda,
        "vulkan" => EngineBackendKind.Vulkan,
        "rocm" => EngineBackendKind.Rocm,
        "cpu" => EngineBackendKind.Cpu,
        _ => null,
    };

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
        _running.Add(job.Id);
        item.Busy = true;
        item.Paused = false;
        item.Progress = 0;
        item.Status = "Starting…";
        _notices.Post(new Notice($"install:{job.Id}", $"Starting {job.Name}", NoticeKind.Progress, ShellRoutes.Models, 0));
        try
        {
            await Task.Run(() => job.Run(accepted));
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
        }
        catch (Exception ex)
        {
            Log.Error($"Installing {job.Name} failed", ex);
            item.Status = $"Failed: {ex.Message}";
            _notices.Clear($"install:{job.Id}");
            _notices.Post(new Notice($"install-failed:{job.Id}", $"{job.Name} could not be installed: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
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

    /// <summary>Asks the Hub (and the catalog) whether anything installed has something newer.</summary>
    [RelayCommand]
    private async Task CheckUpdates()
    {
        CheckingUpdates = true;
        UpdateStatus = "Checking for updates…";
        try
        {
            var result = await Task.Run(() => Services.CheckForUpdatesAsync());
            var note = result.Failed > 0 ? $" · could not reach {result.Failed}" : "";
            UpdateStatus = result.Problem is { } problem ? problem
                : Services.State.Installed.Count == 0 ? "Nothing installed to check."
                : result.Found.Count == 0 ? $"Everything is up to date{note}"
                : $"{result.Found.Count} update{(result.Found.Count == 1 ? "" : "s")} available{note}";
            if (result.Found.Count > 0)
            {
                _notices.Post(new Notice("model-updates", UpdateStatus, NoticeKind.Action, ShellRoutes.Models));
            }
            else
            {
                _notices.Clear("model-updates");
            }
        }
        catch (Exception ex)
        {
            Log.Error("The update check failed", ex);
            UpdateStatus = $"Could not check: {ex.Message}";
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
