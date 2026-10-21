using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core;
using Wicked.Core.Models;

namespace Wicked.App.ViewModels;

/// <summary>One installed model that has something newer: what changed, in the words of the commits.</summary>
public sealed partial class WhatsNewRow : ObservableObject
{
    private readonly WhatsNewViewModel _owner;

    public WhatsNewRow(ModelUpdate update, string name, UpdatePolicy policy, WhatsNewViewModel owner)
    {
        Update = update;
        Name = name;
        Policy = policy;
        _owner = owner;
    }

    public ModelUpdate Update { get; }

    public string Name { get; }

    public UpdatePolicy Policy { get; }

    public string Detail => Update.Detail;

    public bool IsUpload => Update.Kind == UpdateKind.NewUpload;

    /// <summary>"3f2a9c1 → b81d04e": the commit installed and the one on offer.</summary>
    public string Revisions => IsUpload ? $"{Short(Update.InstalledRevision)} → {Short(Update.LatestRevision)}" : "";

    /// <summary>"1.6 GB → 2.1 GB (+0.5 GB)": what the changed files weigh before and after.</summary>
    public string SizeText
    {
        get
        {
            if (!IsUpload || Update.NewSize <= 0) return "";
            if (Update.InstalledSize <= 0) return ModelsViewModel.Size(Update.NewSize);
            var delta = Update.NewSize - Update.InstalledSize;
            var sign = delta >= 0 ? "+" : "−";
            return $"{ModelsViewModel.Size(Update.InstalledSize)} → {ModelsViewModel.Size(Update.NewSize)} ({sign}{ModelsViewModel.Size(Math.Abs(delta))})";
        }
    }

    public IReadOnlyList<string> Titles => Update.Titles;

    public bool HasTitles => Update.Titles.Count > 0;

    public bool CanUpdate => Update.CanApply;

    public string PolicyText => Policy switch
    {
        UpdatePolicy.Auto => "Updates itself",
        UpdatePolicy.Pin => "Pinned",
        _ => "Asks first",
    };

    [ObservableProperty]
    private bool _busy;

    [RelayCommand]
    private Task Apply() => _owner.RunAsync(this, "update");

    [RelayCommand]
    private Task Dismiss() => _owner.RunAsync(this, "dismiss");

    [RelayCommand]
    private Task Mute() => _owner.RunAsync(this, "mute");

    private static string Short(string? revision) => string.IsNullOrEmpty(revision) ? "?" : revision.Length > 7 ? revision[..7] : revision;
}

/// <summary>"What's new in your models" (D7, D26): per model, the revision it is at and the one on offer, the size change, and the commit titles.</summary>
public sealed partial class WhatsNewViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public WhatsNewViewModel(ShellViewModel shell)
    {
        _shell = shell;
        shell.Models.Applied += _ =>
        {
            if (IsOpen) Rebuild();
        };
    }

    public ObservableCollection<WhatsNewRow> Rows { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _summary = "";

    public void Open()
    {
        Rebuild();
        IsOpen = true;
    }

    private void Rebuild()
    {
        var state = _shell.Models.Services.State;
        Rows.Clear();
        foreach (var update in (state.Updates?.Values ?? (IEnumerable<ModelUpdate>)[]).OrderBy(u => u.ModelId, StringComparer.Ordinal))
        {
            var model = state.Installed.FirstOrDefault(m => m.Id == update.ModelId);
            var name = update.ModelId[(update.ModelId.IndexOf('/') + 1)..]; // not a path: a model id
            Rows.Add(new WhatsNewRow(update, name, model is null ? UpdatePolicy.Notify : _shell.Models.Services.PolicyOf(model), this));
        }

        IsEmpty = Rows.Count == 0;
        Summary = Rows.Count == 0 ? "Everything you have installed is up to date." : string.Create(CultureInfo.InvariantCulture, $"{Rows.Count} of your models {(Rows.Count == 1 ? "has" : "have")} something newer.");
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    internal async Task RunAsync(WhatsNewRow row, string actionId)
    {
        if (row.Busy) return;
        row.Busy = true;
        try
        {
            var key = "model-update:" + row.Update.ModelId;
            await Task.Run(() => _shell.Models.Services.Updates.PerformAsync(key, actionId));
        }
        catch (Exception ex)
        {
            Log.Warn($"The {actionId} for {row.Update.ModelId} failed: {ex.Message}");
            _shell.Notices.Post(new Notice("model-update:" + row.Update.ModelId, $"{row.Name} could not be updated: {ex.Message}", NoticeKind.Error, "panel/models"));
        }
        finally
        {
            row.Busy = false;
            Rebuild();
        }
    }
}
