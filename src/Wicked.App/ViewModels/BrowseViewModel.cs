using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core;
using Wicked.Core.Catalog;
using Wicked.Core.Files;
using Wicked.Core.Hardware;
using Wicked.Core.Hub;
using Wicked.Core.Models;

namespace Wicked.App.ViewModels;

/// <summary>What the Install controls of a row need: the install it follows and whether Install is on offer.</summary>
public interface IInstallable
{
    InstallItem Item { get; }

    bool CanInstall { get; }

    bool Installed { get; }

    System.Windows.Input.ICommand InstallCommand { get; }
}

/// <summary>A quant in a model's chooser: the file set and the size it costs on disk.</summary>
public sealed record TierChoice(QuantTier Tier)
{
    public string Label => $"{Tier.Quant} · {ModelsViewModel.Size(Tier.Size)}";

    public override string ToString() => Label;
}

/// <summary>A fit badge's words and colour for one file on this machine: judged by the same arithmetic the recommender uses.</summary>
public sealed record FitBadge(string Text, bool Ok, bool Offload, bool No, string Tip)
{
    public static FitBadge Of(FitResult? fit, string unknownTip = "") => fit is null
        ? new FitBadge("Fit unknown", false, false, false, unknownTip)
        : fit.Level switch
        {
            FitLevel.FitsInVram => new FitBadge("Fits in VRAM", true, false, false, $"About {ModelsViewModel.Size(fit.RequiredBytes)} with {fit.Context / 1024}k of context"),
            FitLevel.FitsWithOffload => new FitBadge("CPU offload", false, true, false, $"About {ModelsViewModel.Size(fit.RequiredBytes)}; {fit.GpuLayers} layers on the GPU, the rest on the CPU"),
            _ => new FitBadge("Won't fit", false, false, true, $"Needs about {ModelsViewModel.Size(fit.RequiredBytes)}, more than this PC has"),
        };
}

/// <summary>One model of the catalog on the Browse page: facts, a quant chooser, the fit for the chosen quant, and Install.</summary>
public sealed partial class CatalogRow : ObservableObject, IInstallable
{
    private readonly ModelsViewModel _owner;
    private HardwareInfo? _hardware;

    public CatalogRow(CatalogModel model, string familyName, ModelsViewModel owner, HardwareInfo? hardware)
    {
        Model = model;
        FamilyName = familyName;
        _owner = owner;
        _hardware = hardware;
        // Only the quants this machine can use (the recommender's rule); a row with none is not shown at all.
        Tiers = [.. model.Tiers.Where(t => ModelFit.Usable(model, t, hardware)).OrderByDescending(t => t.Quality).Select(t => new TierChoice(t))];
        Item = owner.Item(model.Repo);
        Item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallItem.Busy)) OnPropertyChanged(nameof(CanInstall));
        };
        _selectedTier = Tiers.FirstOrDefault(t => t.Tier == ModelFit.DefaultTier(model, hardware)) ?? Tiers.FirstOrDefault();
        UpdateFit();
    }

    public CatalogModel Model { get; }

    public string FamilyName { get; }

    public string Name => Model.Name;

    public InstallItem Item { get; }

    public IReadOnlyList<TierChoice> Tiers { get; }

    /// <summary>This machine can run at least one of its quants.</summary>
    public bool Usable => Tiers.Count > 0;

    public ModelUse Use => ModelUse.Of(Model);

    /// <summary>What the model is for, said plainly where it is not just a chat model.</summary>
    public string UseNote => Use switch
    {
        { CompletionOnly: true } => "Code completion only: fills in code as you type, cannot chat",
        { Chats: true, Completes: true } => "Chats, and completes code as you type",
        _ => "",
    };

    public bool HasUseNote => UseNote.Length > 0;

    public string Roles => string.Join(" · ", RoleNames());

    public bool HasRoles => RoleNames().Any();

    private IEnumerable<string> RoleNames()
    {
        if (Model.Scores.Everyday is not null) yield return "EVERYDAY";
        if (Model.Scores.Coding is not null) yield return "CODING";
        if (Model.Scores.Fast is not null) yield return "FAST";
        if (Model.Scores.Fim is not null) yield return "COMPLETION";
    }

    public string Facts
    {
        get
        {
            var p = Model.Parameters;
            var size = p.Active is { } a && p.IsMoe
                ? $"{p.Total.ToString("0.#", CultureInfo.InvariantCulture)}B ({a.ToString("0.#", CultureInfo.InvariantCulture)}B active)"
                : $"{p.Total.ToString("0.#", CultureInfo.InvariantCulture)}B";
            var capabilities = Model.Capabilities.Where(c => c is not Capability.Chat).Select(c => c.ToString().ToLowerInvariant());
            return string.Join(" · ", new[] { size, $"{Model.ContextLength / 1024}k context", Model.LicenseId }.Concat(capabilities));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(InstalledText))]
    private bool _installed;

    [ObservableProperty]
    private TierChoice? _selectedTier;

    [ObservableProperty]
    private FitBadge _fit = FitBadge.Of(null);

    public bool CanInstall => !Installed && !Item.Busy;

    public string InstalledText => "Installed";

    System.Windows.Input.ICommand IInstallable.InstallCommand => InstallCommand;

    partial void OnSelectedTierChanged(TierChoice? value)
    {
        UpdateFit();
        Installed = _owner.Services.State.Installed.Any(m => m.Id == Model.Repo && value is not null && m.Variants.Any(v => string.Equals(v.Quant, value.Tier.Quant, StringComparison.OrdinalIgnoreCase)));
    }

    private void UpdateFit() => Fit = SelectedTier is { } t ? FitBadge.Of(ModelFit.For(Model, t.Tier, _hardware)) : FitBadge.Of(null);

    public void Refresh(ModelState state)
    {
        if (!ReferenceEquals(_hardware, state.Hardware))
        {
            _hardware = state.Hardware;
            UpdateFit();
        }

        Installed = SelectedTier is { } t && state.IsInstalled(Model, t.Tier);
    }

    [RelayCommand]
    private Task Install()
    {
        if (SelectedTier is not { } choice) return Task.CompletedTask;
        var model = Model;
        return _owner.RequestInstallAsync(new InstallJob(
            model.Repo, model.Name, model.LicenseId, model.LicenseUrl, model.Repo,
            accepted => _owner.Services.InstallCatalogAsync(model, choice.Tier, accepted)));
    }
}

public sealed record FamilyGroup(string Name, string Publisher, ObservableCollection<CatalogRow> Models)
{
    public string Heading => $"{Name} · {Publisher}".ToUpperInvariant();
}

/// <summary>A role filter chip.</summary>
public sealed partial class RoleChip(string label, Func<CatalogModel, bool> matches, BrowseViewModel owner) : ObservableObject
{
    public string Label { get; } = label;

    public bool Matches(CatalogModel m) => matches(m);

    [ObservableProperty]
    private bool _selected;

    partial void OnSelectedChanged(bool value) => owner.Refilter();
}

/// <summary>One search hit on the Hugging Face tab.</summary>
public sealed record HubResultRow(HubSearchResult Result)
{
    public string Id => Result.Id;

    public string Name => Result.Id[(Result.Id.IndexOf('/') + 1)..]; // not a path: a repo id

    public string Publisher => Result.Id.Contains('/') ? Result.Id[..Result.Id.IndexOf('/')] : ""; // not a path: a Hugging Face repo id is owner/name

    public bool Gated => Result.Gated != Gating.None;

    public string Facts => $"{Publisher} · {Count(Result.Downloads)} downloads · {Count(Result.Likes)} likes";

    private static string Count(long n) => n >= 1_000_000 ? (n / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M"
        : n >= 1000 ? (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k"
        : n.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One quant of a Hugging Face repo, with its fit and an Install.</summary>
public sealed partial class HubVariantRow : ObservableObject, IInstallable
{
    private readonly ModelsViewModel _owner;
    private readonly HubRepoDetail _detail;

    public HubVariantRow(GgufVariant variant, HubRepoDetail detail, ModelsViewModel owner)
    {
        Variant = variant;
        _detail = detail;
        _owner = owner;
        Item = owner.Item(detail.Model.Id);
        Item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallItem.Busy)) OnPropertyChanged(nameof(CanInstall));
        };
        var fit = detail.Shape is { } shape ? ModelFit.For(shape, variant.TotalSize, detail.NativeContext > 0 ? detail.NativeContext : 8192, owner.Services.State.Hardware) : null;
        // A repo says nothing of mixture-of-experts or its parameter count, so it is judged as a dense model of unknown size.
        var hardware = owner.Services.State.Hardware;
        Usable = fit is null || hardware is null || ModelFit.Usable(fit, detail.Shape!.Layers, false, null, Recommender.BudgetFor(hardware));
        Fit = FitBadge.Of(fit, $"The fit is unknown: {detail.ShapeSource}");
        Refresh(owner.Services.State);
    }

    public GgufVariant Variant { get; }

    /// <summary>This machine can run the quant usefully (or its fit is unknown); the others are not listed.</summary>
    public bool Usable { get; }

    public InstallItem Item { get; }

    public string Quant => Variant.Quant + (Variant.Dynamic ? " · UD" : "");

    public string Size => ModelsViewModel.Size(Variant.TotalSize) + (Variant.IsSharded ? $" · {Variant.Files.Count} files" : "");

    public FitBadge Fit { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installed;

    System.Windows.Input.ICommand IInstallable.InstallCommand => InstallCommand;

    public bool CanInstall => !Installed && !Item.Busy;

    public void Refresh(ModelState state) =>
        Installed = state.Installed.Any(m => m.Id == _detail.Model.Id && m.Variants.Any(v => string.Equals(v.Quant, Variant.Quant, StringComparison.OrdinalIgnoreCase)));

    [RelayCommand]
    private Task Install()
    {
        var detail = _detail;
        var variant = Variant;
        var name = $"{detail.Model.Id[(detail.Model.Id.IndexOf('/') + 1)..]} {variant.Quant}"; // not a path: a repo id
        return _owner.RequestInstallAsync(new InstallJob(
            detail.Model.Id, name, detail.Model.License, null, detail.Model.Id,
            accepted => _owner.Services.InstallFromHubAsync(detail.Model.Id, variant, detail.Model, accepted)));
    }
}

/// <summary>The right-hand side of the Hugging Face tab: a repo, what it is, and its quants.</summary>
public sealed partial class HubDetailView(string repo) : ObservableObject
{
    public string Repo { get; } = repo;

    public string Title => Repo[(Repo.IndexOf('/') + 1)..]; // not a path: a repo id

    public ObservableCollection<HubVariantRow> Variants { get; } = [];

    [ObservableProperty]
    private bool _loading = true;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _fitNote = "";

    [ObservableProperty]
    private string _message = "";

    [ObservableProperty]
    private bool _gated;
}

/// <summary>
/// The Browse page: every model of the catalog with its fit for this machine, and a search of Hugging Face's GGUF repos. The
/// catalog tab is data already in memory; the Hub tab does its network work on workers (debounced as you type) and posts what it finds.
/// </summary>
public sealed partial class BrowseViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;
    private readonly Dictionary<string, CatalogRow> _rows = new(StringComparer.Ordinal);
    private CatalogLoad? _catalog;
    private HardwareInfo? _hardware;
    private CancellationTokenSource? _search;
    private CancellationTokenSource? _detailLoad;

    public BrowseViewModel(ModelsViewModel owner)
    {
        _owner = owner;
        Chips =
        [
            new("Everyday", m => m.Scores.Everyday is not null, this),
            new("Coding", m => m.Scores.Coding is not null, this),
            new("Fast", m => m.Scores.Fast is not null, this),
            new("Vision", m => m.Has(Capability.Vision), this),
        ];
        owner.Applied += OnApplied;
    }

    /// <summary>The Models panel's view model (the page shows its install problems).</summary>
    public ModelsViewModel Owner => _owner;

    public ObservableCollection<RoleChip> Chips { get; }

    public ObservableCollection<FamilyGroup> Families { get; } = [];

    public ObservableCollection<HubResultRow> HubResults { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCatalog), nameof(ShowChatModels), nameof(ShowCompletionModels))]
    private bool _showHub;

    public bool ShowCatalog => !ShowHub;

    /// <summary>The catalog tab lists the code completion models instead of the chat models.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChatModels), nameof(ShowCompletionModels))]
    private bool _showCompletion;

    public bool ShowChatModels => !ShowHub && !ShowCompletion;

    public bool ShowCompletionModels => !ShowHub && ShowCompletion;

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _catalogNote = "";

    [ObservableProperty]
    private string _hubQuery = "";

    [ObservableProperty]
    private string _hubStatus = "Search Hugging Face for GGUF models";

    [ObservableProperty]
    private bool _hubBusy;

    [ObservableProperty]
    private HubResultRow? _selectedResult;

    [ObservableProperty]
    private HubDetailView? _detail;

    public void Open()
    {
        IsOpen = true;
        Rebuild(_owner.Services.State);
        if (ShowHub && HubResults.Count == 0) ScheduleSearch(immediate: true);
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private void SelectCatalog()
    {
        ShowHub = false;
        ShowCompletion = false;
    }

    [RelayCommand]
    private void SelectCompletion()
    {
        ShowHub = false;
        ShowCompletion = true;
    }

    [RelayCommand]
    private void SelectHub()
    {
        ShowHub = true;
        if (HubResults.Count == 0 && !HubBusy) ScheduleSearch(immediate: true);
    }

    // ── Catalog tab ──────────────────────────────────────────────────────────────────────────────

    partial void OnFilterChanged(string value) => Refilter();

    partial void OnShowCompletionChanged(bool value) => Refilter();

    private void OnApplied(ModelState state)
    {
        // A new hardware report changes which quants this machine can use, so the rows are built again.
        if (!ReferenceEquals(state.Catalog, _catalog) || !ReferenceEquals(state.Hardware, _hardware) || _rows.Count == 0 && state.Catalog is not null)
        {
            Rebuild(state);
            return;
        }

        foreach (var row in _rows.Values) row.Refresh(state);
        foreach (var variant in Detail?.Variants ?? []) variant.Refresh(state);
    }

    private void Rebuild(ModelState state)
    {
        _catalog = state.Catalog;
        _hardware = state.Hardware;
        _rows.Clear();
        if (state.Catalog is { } load)
        {
            foreach (var family in load.Catalog.Families)
            {
                foreach (var model in family.Models) _rows[model.Id] = new CatalogRow(model, family.Name, _owner, state.Hardware);
            }

            foreach (var row in _rows.Values) row.Refresh(state);
        }

        Refilter();
    }

    /// <summary>
    /// The chat models or the code completion models, by the filter box (fuzzy by name) and the role chips: families by their
    /// newest model, each family newest first. A model this machine cannot run usefully is not listed; the note counts them.
    /// </summary>
    public void Refilter()
    {
        var roles = Chips.Where(c => c.Selected).ToList();
        var query = Filter.Trim();
        var completion = ShowCompletion;
        bool OfKind(CatalogRow r) => completion ? r.Use.Completes : r.Use.Chats && !r.Model.Has(Capability.Embeddings);
        bool Keep(CatalogRow r) =>
            OfKind(r) && r.Usable
            && (completion || roles.Count == 0 || roles.Any(c => c.Matches(r.Model)))
            && (query.Length == 0 || FuzzyMatch.Score(query, $"{r.FamilyName} {r.Name}") >= 0);

        var groups = _catalog?.Catalog.Families
            .Select(f => (Family: f, Rows: f.Models.Select(m => _rows[m.Id]).Where(Keep).OrderByDescending(r => r.Model.Released).ThenBy(r => r.Name, StringComparer.Ordinal).ToList()))
            .Where(g => g.Rows.Count > 0)
            .OrderByDescending(g => g.Rows[0].Model.Released)
            .ToList() ?? [];

        Families.Clear();
        foreach (var (family, rows) in groups) Families.Add(new FamilyGroup(family.Name, family.Publisher, new ObservableCollection<CatalogRow>(rows)));
        var total = groups.Sum(g => g.Rows.Count);
        var hidden = _rows.Values.Count(r => OfKind(r) && !r.Usable);
        var kind = completion ? "code completion model" : "chat model";
        var tooBig = hidden == 0 ? "" : $" · {hidden} too large for this PC {(hidden == 1 ? "is" : "are")} hidden";
        CatalogNote = _catalog is null ? "Loading the catalog…"
            : total == 0 ? (hidden > 0 && roles.Count == 0 && query.Length == 0 ? $"None of these fit this PC{tooBig}" : $"No model matches{tooBig}")
            : $"{total} {kind}{(total == 1 ? "" : "s")}{tooBig}";
    }

    // ── Hugging Face tab ─────────────────────────────────────────────────────────────────────────

    partial void OnHubQueryChanged(string value) => ScheduleSearch(immediate: false);

    private void ScheduleSearch(bool immediate)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var query = HubQuery.Trim();
        HubBusy = true;
        HubStatus = "Searching…";
        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediate) await Task.Delay(TimeSpan.FromMilliseconds(350), cts.Token).ConfigureAwait(false);
                var found = await _owner.Services.SearchHubAsync(query, cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    HubResults.Clear();
                    foreach (var hit in found) HubResults.Add(new HubResultRow(hit));
                    HubStatus = found.Count == 0 ? "No GGUF models match" : query.Length == 0 ? "Most downloaded GGUF models" : found.Count == 1 ? "1 GGUF repo" : $"{found.Count} GGUF repos";
                    HubBusy = false;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Hugging Face search failed: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    HubStatus = ex is RateLimitedException ? ex.Message : $"Could not reach Hugging Face: {ex.Message}";
                    HubBusy = false;
                });
            }
        });
    }

    partial void OnSelectedResultChanged(HubResultRow? value)
    {
        _detailLoad?.Cancel();
        if (value is null)
        {
            Detail = null;
            return;
        }

        var cts = _detailLoad = new CancellationTokenSource();
        var detail = new HubDetailView(value.Id) { Gated = value.Gated };
        Detail = detail;
        _ = Task.Run(async () =>
        {
            try
            {
                var repo = await _owner.Services.GetHubRepoAsync(value.Id, cts.Token).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    var rows = repo.Variants.Select(v => new HubVariantRow(v, repo, _owner)).ToList();
                    foreach (var row in rows.Where(r => r.Usable)) detail.Variants.Add(row);
                    var hidden = rows.Count(r => !r.Usable);
                    detail.Summary = $"{value.Publisher} · {Licenses.Display(repo.Model.License)} · {value.Facts.Split(" · ", 2)[1]}";
                    detail.Gated = repo.Model.Gated != Gating.None;
                    var tooBig = hidden == 0 ? "" : $" · {hidden} quant{(hidden == 1 ? "" : "s")} too large for this PC hidden";
                    detail.FitNote = repo.Shape is null ? $"Fit unknown: {repo.ShapeSource}" : $"Fit judged from the {repo.ShapeSource}, at {Math.Min(16384, repo.NativeContext > 0 ? repo.NativeContext : 8192) / 1024}k of context{tooBig}";
                    detail.Message = repo.Variants.Count == 0 ? "This repo has no GGUF files Wicked can install." : rows.Count > 0 && hidden == rows.Count ? "Every quant of this model is too large for this PC." : detail.Gated ? "Gated: accept its terms on huggingface.co and add your token in Settings → Models, then install." : "";
                    detail.Loading = false;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Log.Warn($"Reading {value.Id} failed: {ex.Message}");
                Dispatcher.UIThread.Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    detail.Message = ex is HubException ? ex.Message : $"Could not read {value.Id}: {ex.Message}";
                    detail.Loading = false;
                });
            }
        });
    }
}
