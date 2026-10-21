using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core;
using Wicked.Core.Chat;

namespace Wicked.App.ViewModels;

/// <summary>
/// One model's Run settings sheet (the router preset the user edits): context, GPU layers, KV cache type, flash
/// attention, draft model, reasoning and sampling. Every choice starts at Automatic, which Wicked works out from this PC
/// and the model; a loaded model is reloaded for a change to take effect.
/// </summary>
public sealed partial class RunSettingsViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;
    private readonly string _id;

    public RunSettingsViewModel(ModelsViewModel owner, InstalledRow row, ModelRunSettings current, IReadOnlyList<SettingChoice> otherModels)
    {
        _owner = owner;
        _id = row.RunId;
        Title = $"Run settings: {row.Name}";
        var auto = new SettingChoice("", "Automatic");

        Contexts = Choices(auto, [2048, 4096, 8192, 16384, 32768, 65536, 131072], v => v.ToString(CultureInfo.InvariantCulture), v => v.ToString("N0", CultureInfo.InvariantCulture) + " tokens", current.ContextSize?.ToString(CultureInfo.InvariantCulture));
        GpuLayerChoices = Choices(auto, ["all", "0", "8", "16", "24", "32", "48", "64"], v => v, v => v == "all" ? "All layers" : v == "0" ? "None (CPU only)" : $"{v} layers", current.GpuLayers);
        KvCacheChoices = Choices(auto, ["f16", "q8_0", "q4_0"], v => v, v => v switch { "f16" => "F16 (full precision)", "q8_0" => "Q8_0 (about half the memory)", "q4_0" => "Q4_0 (about a quarter)", _ => v }, current.KvCacheType);
        OnOff = [auto, new SettingChoice("on", "On"), new SettingChoice("off", "Off")];
        DraftModels = [auto, .. otherModels];

        _context = Pick(Contexts, current.ContextSize?.ToString(CultureInfo.InvariantCulture));
        _gpuLayers = Pick(GpuLayerChoices, current.GpuLayers);
        _kvCache = Pick(KvCacheChoices, current.KvCacheType);
        _flashAttention = Pick(OnOff, current.FlashAttention is { } f ? (f ? "on" : "off") : null);
        _reasoning = Pick(OnOff, current.Reasoning is { } r ? (r ? "on" : "off") : null);
        _draftModel = Pick(DraftModels, current.DraftModelId);
        _temperature = current.Sampling?.Temperature is { } t ? (decimal)t : null;
        _topP = current.Sampling?.TopP is { } p ? (decimal)p : null;
        _maxTokens = current.Sampling?.MaxTokens;
    }

    public string Title { get; }

    public IReadOnlyList<SettingChoice> Contexts { get; }

    public IReadOnlyList<SettingChoice> GpuLayerChoices { get; }

    public IReadOnlyList<SettingChoice> KvCacheChoices { get; }

    public IReadOnlyList<SettingChoice> OnOff { get; }

    public IReadOnlyList<SettingChoice> DraftModels { get; }

    [ObservableProperty]
    private SettingChoice _context;

    [ObservableProperty]
    private SettingChoice _gpuLayers;

    [ObservableProperty]
    private SettingChoice _kvCache;

    [ObservableProperty]
    private SettingChoice _flashAttention;

    [ObservableProperty]
    private SettingChoice _reasoning;

    [ObservableProperty]
    private SettingChoice _draftModel;

    /// <summary>Empty means Automatic.</summary>
    [ObservableProperty]
    private decimal? _temperature;

    [ObservableProperty]
    private decimal? _topP;

    [ObservableProperty]
    private int? _maxTokens;

    [ObservableProperty]
    private string _problem = "";

    [ObservableProperty]
    private bool _saving;

    private static SettingChoice Pick(IReadOnlyList<SettingChoice> choices, string? value) =>
        choices.FirstOrDefault(c => c.Value == (value ?? "")) ?? choices[0];

    /// <summary>The usual choices, plus the model's own value when it is not among them.</summary>
    private static IReadOnlyList<SettingChoice> Choices<T>(SettingChoice auto, T[] usual, Func<T, string> value, Func<T, string> label, string? current)
    {
        var list = new List<SettingChoice> { auto };
        list.AddRange(usual.Select(v => new SettingChoice(value(v), label(v))));
        if (!string.IsNullOrEmpty(current) && list.All(c => c.Value != current)) list.Add(new SettingChoice(current, current));
        return list;
    }

    private static bool? Tri(SettingChoice choice) => choice.Value switch { "on" => true, "off" => false, _ => null };

    /// <summary>The settings the sheet describes; null for a choice left at Automatic.</summary>
    public ModelRunSettings ToSettings()
    {
        var sampling = Temperature is null && TopP is null && MaxTokens is null
            ? null
            : new ChatSampling((double?)Temperature, (double?)TopP, MaxTokens);
        return new ModelRunSettings(
            Context.Value.Length == 0 ? null : int.Parse(Context.Value, CultureInfo.InvariantCulture),
            GpuLayers.Value.Length == 0 ? null : GpuLayers.Value,
            KvCache.Value.Length == 0 ? null : KvCache.Value,
            Tri(FlashAttention),
            DraftModel.Value.Length == 0 ? null : DraftModel.Value,
            Tri(Reasoning),
            sampling);
    }

    [RelayCommand]
    private async Task Save()
    {
        if (Saving) return;
        if (Temperature is < 0 or > 5) { Problem = "Temperature is between 0 and 5."; return; }
        if (TopP is < 0 or > 1) { Problem = "Top-p is between 0 and 1."; return; }
        if (MaxTokens is < 1) { Problem = "Max tokens must be at least 1."; return; }
        Saving = true;
        Problem = "";
        try
        {
            await _owner.SaveRunSettingsAsync(_id, ToSettings());
        }
        catch (Exception ex)
        {
            Log.Warn($"Run settings for {_id} could not be saved: {ex.Message}");
            Problem = $"Not saved: {ex.Message}";
        }
        finally
        {
            Saving = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _owner.RunSettings = null;

    /// <summary>Every choice back to Automatic.</summary>
    [RelayCommand]
    private void Reset()
    {
        Context = Contexts[0];
        GpuLayers = GpuLayerChoices[0];
        KvCache = KvCacheChoices[0];
        FlashAttention = OnOff[0];
        Reasoning = OnOff[0];
        DraftModel = DraftModels[0];
        Temperature = null;
        TopP = null;
        MaxTokens = null;
    }
}
