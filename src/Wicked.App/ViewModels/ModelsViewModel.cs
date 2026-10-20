using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Catalog;
using Wicked.Core.Models;
using Wicked.Core.Store;

namespace Wicked.App.ViewModels;

/// <summary>One recommendation card (D25).</summary>
public sealed partial class PickCard(Pick pick, ModelsViewModel owner) : ObservableObject
{
    public Pick Pick { get; } = pick;

    public string Role => (Pick.AlsoFor.Count > 0 ? string.Join(" · ", new[] { Pick.Kind }.Concat(Pick.AlsoFor)) : Pick.Kind.ToString()).ToUpperInvariant();

    public string Name => Pick.Model.Name;

    public string Quant => Pick.Tier.Quant;

    public string Reason => Pick.Reason;

    public string Details => $"{ModelsViewModel.Size(Pick.Tier.Size + (Pick.Companion?.Tier.Size ?? 0))} · {Pick.Model.LicenseId}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _busy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "";

    public bool CanInstall => !Installed && !Busy;

    [RelayCommand]
    private Task Install() => owner.InstallAsync(this);
}

/// <summary>An installed model in the list.</summary>
public sealed record InstalledRow(string Id, string Name, string Details);

/// <summary>
/// The Models panel: this machine, the recommendations, what is installed and the engine. The services do their work on
/// workers; their state arrives here posted to the UI thread. Installs report to the footer as well, so they stay
/// visible with the panel closed, and a click there comes back here.
/// </summary>
public sealed partial class ModelsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly NoticeBoard _notices;

    public ModelsViewModel(WickedPaths paths, NoticeBoard notices)
    {
        _notices = notices;
        Services = new ModelServices(paths);
        Services.Changed += state => Dispatcher.UIThread.Post(() => Apply(state));
        Services.Progress += p => Dispatcher.UIThread.Post(() => OnProgress(p));
    }

    public ModelServices Services { get; }

    public ObservableCollection<PickCard> Picks { get; } = [];

    public ObservableCollection<InstalledRow> Installed { get; } = [];

    [ObservableProperty]
    private string _hardwareText = "Looking at this PC…";

    [ObservableProperty]
    private string _engineText = "";

    [ObservableProperty]
    private bool _ready;

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

        foreach (var card in Picks)
        {
            card.Installed = state.IsInstalled(card.Pick.Model, card.Pick.Tier);
            card.Busy = state.Busy.Contains(card.Pick.Model.Repo) || (state.Busy.Contains("engine") && card.Busy);
            if (card.Installed) card.Status = "Installed";
        }

        Installed.Clear();
        foreach (var model in state.Installed) Installed.Add(Row(model));
        Ready = state.Catalog is not null;
    }

    private static InstalledRow Row(StoredModel model)
    {
        var name = model.Id.Contains('/') ? model.Id[(model.Id.IndexOf('/') + 1)..] : model.Id; // not a path: a model id
        var quants = string.Join(", ", model.Variants.Select(v => v.Quant));
        return new InstalledRow(model.Id, name, $"{quants} · {Size(model.SizeOnDisk)}");
    }

    public async Task InstallAsync(PickCard card)
    {
        card.Busy = true;
        card.Status = "Starting…";
        _activeCard = card;
        try
        {
            await Task.Run(() => Services.InstallAsync(card.Pick));
            _notices.Clear("install");
            _notices.Post(new Notice("installed", $"{card.Name} is installed and ready", NoticeKind.Success, ShellRoutes.Models));
        }
        catch (Exception ex)
        {
            Log.Error($"Installing {card.Name} failed", ex);
            card.Status = $"Failed: {ex.Message}";
            _notices.Clear("install");
            _notices.Post(new Notice("install-failed", $"{card.Name} could not be installed: {ex.Message}", NoticeKind.Error, ShellRoutes.Models));
        }
        finally
        {
            card.Busy = false;
            _activeCard = null;
        }
    }

    private PickCard? _activeCard;

    private void OnProgress(InstallProgress p)
    {
        if (_activeCard is { } card)
        {
            card.Status = p.Message;
            if (p.Fraction is { } f) card.Progress = f;
        }

        _notices.Post(new Notice("install", p.Message, NoticeKind.Progress, ShellRoutes.Models, p.Fraction));
    }

    public static string Size(long bytes) => bytes >= 1L << 30
        ? (bytes / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB"
        : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
