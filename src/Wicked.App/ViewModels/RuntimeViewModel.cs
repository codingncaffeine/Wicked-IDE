using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Wicked.Core;
using Wicked.Core.Engines;
using Wicked.Core.Models;
using Wicked.Core.Settings;

namespace Wicked.App.ViewModels;

/// <summary>
/// Settings → Runtime: which llama.cpp build runs the models, what it sees of this machine, which backend to insist on, and
/// whether a newer build exists (Update keeps the one it replaces; Roll back goes back to it). Everything that touches a process,
/// the disk or GitHub runs on a worker; the answers are posted here.
/// </summary>
public sealed partial class RuntimeViewModel : ObservableObject
{
    private readonly ModelsViewModel _models;
    private readonly Func<Func<WickedSettings, WickedSettings>, Task> _change;
    private bool _loading;
    private EngineUpdate? _pending;
    private int _refreshing;

    public RuntimeViewModel(ModelsViewModel models, Func<Func<WickedSettings, WickedSettings>, Task> change)
    {
        _models = models;
        _change = change;
        Backends =
        [
            new("auto", "Automatic (what this PC calls for)"),
            new("cuda", "CUDA (NVIDIA)"),
            new("vulkan", "Vulkan (any GPU)"),
            new("rocm", "ROCm (AMD)"),
            new("cpu", "CPU only"),
        ];
        _backend = Backends[0];
        models.Services.Progress += p => Dispatcher.UIThread.Post(() => OnProgress(p));
        models.Applied += state =>
        {
            if (state.Engine?.Id != _shownEngine) _ = RefreshAsync();
        };
        // The daily check found a newer engine: the section offers it at once (its notice leads here).
        models.Services.Updates.Swept += sweep =>
        {
            if (sweep.Engine is { } found) Dispatcher.UIThread.Post(() => OfferFound(found));
        };
    }

    /// <summary>An update the background check found, unless it is the engine already running or one is being offered.</summary>
    private void OfferFound(EngineUpdate found)
    {
        if (_pending is not null || Busy || found.Id == _shownEngine) return;
        Offer(found);
    }

    private void Offer(EngineUpdate update)
    {
        _pending = update;
        UpdateButtonText = update.From is null ? $"Install {update.Release.Tag} ({update.Choice.Backend.DisplayName})" : $"Update to {update.Id}";
        Status = update.From is null
            ? $"{update.Release.Tag} is available for {update.Choice.Backend.DisplayName}"
            : update.From.Backend == update.Choice.Backend.Slug
                ? $"{update.Release.Tag} is newer than {update.From.Tag}"
                : $"{update.Choice.Backend.DisplayName} build {update.Release.Tag} is available; your {Pretty(update.From.Backend)} engine is kept for rollback";
    }

    private string? _shownEngine;

    public IReadOnlyList<SettingChoice> Backends { get; }

    public ObservableCollection<string> Devices { get; } = [];

    [ObservableProperty]
    private SettingChoice? _backend;

    [ObservableProperty]
    private string _engineText = "Looking at the engine…";

    [ObservableProperty]
    private string _versionText = "";

    [ObservableProperty]
    private string _devicesNote = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _busy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    private string? _updateButtonText;

    [ObservableProperty]
    private string? _rollbackButtonText;

    public bool CanRun => !Busy;

    public bool HasUpdate => UpdateButtonText is not null;

    public bool CanRollback => RollbackButtonText is not null;

    partial void OnUpdateButtonTextChanged(string? value) => OnPropertyChanged(nameof(HasUpdate));

    partial void OnRollbackButtonTextChanged(string? value) => OnPropertyChanged(nameof(CanRollback));

    partial void OnBackendChanged(SettingChoice? value)
    {
        if (_loading || value is null) return;
        _pending = null;
        UpdateButtonText = null;
        Status = value.Value == "auto" ? "Backend chosen automatically; check for engine updates to see what that means now" : $"{value.Label.Split(' ')[0]} chosen; check for engine updates to install it";
        _ = _change(s => s with { RuntimeBackend = value.Value });
    }

    /// <summary>The backend setting as the controls show it (called when settings.json changes).</summary>
    public void Load(WickedSettings s)
    {
        _loading = true;
        Backend = Backends.FirstOrDefault(b => b.Value == s.RuntimeBackend) ?? Backends[0];
        _loading = false;
    }

    /// <summary>Asks the engine what it is and what it sees (a worker starts <c>llama-server --version</c> and <c>--list-devices</c>).</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        try
        {
            var status = await Task.Run(() => _models.Services.GetRuntimeAsync());
            _shownEngine = status.Current?.Id;
            if (status.Current is not { } current)
            {
                EngineText = "No engine yet. It installs itself with your first model, or from Check for engine updates below.";
                VersionText = "";
                Devices.Clear();
                DevicesNote = "";
            }
            else
            {
                EngineText = $"{current.Id}  ·  {Pretty(current.Backend)}";
                VersionText = status.Report?.Version is { } v
                    ? $"llama.cpp {v.Text}{(v.Build is { } b ? $" (build {b.ToString(CultureInfo.InvariantCulture)})" : "")}"
                    : "The engine did not report a version";
                Devices.Clear();
                foreach (var d in status.Report?.Devices ?? []) Devices.Add(DeviceLine(d));
                DevicesNote = Devices.Count == 0 ? "The engine reports no devices: models run on the CPU" : "";
            }

            RollbackButtonText = status.Previous is { } previous ? $"Roll back to {previous.Id}" : null;
            if (_models.Services.Updates.Last?.Engine is { } found) OfferFound(found);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the engine", ex);
            EngineText = $"The engine could not be read: {ex.Message}";
        }
        finally
        {
            Interlocked.Exchange(ref _refreshing, 0);
        }
    }

    [RelayCommand]
    private async Task Check()
    {
        Busy = true;
        Status = "Looking for a newer engine…";
        try
        {
            var update = await Task.Run(() => _models.Services.CheckEngineUpdateAsync());
            _pending = update;
            if (update is null)
            {
                UpdateButtonText = null;
                Status = _models.Services.Engines.Current is { } c ? $"The engine is up to date ({c.Tag})" : "No engine build for this PC was found in the recent releases";
            }
            else
            {
                Offer(update);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Engine update check failed: {ex.Message}");
            Status = ex is GitHubRateLimitException ? $"GitHub is rate limiting; try again at {((GitHubRateLimitException)ex).ResetsAt.LocalDateTime:t}" : $"Could not check: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task Update()
    {
        if (_pending is not { } update) return;
        Busy = true;
        ShowProgress = true;
        Progress = 0;
        Status = $"Installing {update.Id}…";
        try
        {
            var install = await Task.Run(() => _models.Services.UpdateEngineAsync(update));
            _pending = null;
            UpdateButtonText = null;
            Status = $"Now running {install.Id}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Error($"Installing engine {update.Id} failed", ex);
            Status = $"The engine could not be installed: {ex.Message}";
        }
        finally
        {
            Busy = false;
            ShowProgress = false;
        }
    }

    [RelayCommand]
    private async Task Rollback()
    {
        Busy = true;
        try
        {
            var install = await Task.Run(() => _models.Services.RollbackEngineAsync());
            _pending = null;
            UpdateButtonText = null;
            Status = $"Rolled back to {install.Id}";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Status = $"Could not roll back: {ex.Message}";
        }
        finally
        {
            Busy = false;
        }
    }

    private void OnProgress(InstallProgress p)
    {
        if (p.ModelId != "engine" || !Busy) return;
        Progress = p.Fraction ?? 0;
        Status = p.Message;
    }

    public static string Pretty(string slug) => slug switch
    {
        "cpu" => "CPU",
        "vulkan" => "Vulkan",
        "metal" => "Metal",
        _ when slug.StartsWith("cuda-", StringComparison.Ordinal) => "CUDA " + slug[5..],
        _ when slug.StartsWith("rocm-", StringComparison.Ordinal) => "ROCm " + slug[5..],
        _ => slug,
    };

    private static string DeviceLine(EngineDevice d)
    {
        var memory = d.TotalBytes is { } total
            ? $"  ·  {ModelsViewModel.Size(total)}{(d.FreeBytes is { } free ? $" ({ModelsViewModel.Size(free)} free)" : "")}"
            : "";
        return $"{d.Id}  ·  {d.Name}{memory}";
    }
}
