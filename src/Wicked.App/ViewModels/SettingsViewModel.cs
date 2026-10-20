using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Settings;
using Wicked.Editor.Syntax;

namespace Wicked.App.ViewModels;

/// <summary>A choice in a drop-down: the value stored in settings.json and the words shown.</summary>
public sealed record SettingChoice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The Settings sheet: every setting has a control here (code is never required, D16), and each change applies at once
/// and is written to settings.json. When the file changes by hand the controls follow.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _loading;

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        CodeThemes = [new("", "Wicked Dark (the theme's own)"), .. SyntaxService.Themes.Select(t => new SettingChoice(t.ToString(), ShellViewModel.Friendly(t.ToString())))];
        DownloadLimits = [new("0", "Unlimited"), .. new[] { 1, 2, 5, 10, 25, 50, 100 }.Select(n => new SettingChoice(n.ToString(CultureInfo.InvariantCulture), n + " MB/s"))];
        Runtime = new RuntimeViewModel(shell.Models, shell.UpdateSettingsAsync);
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Settings)) Load(shell.Settings);
            if (e.PropertyName == nameof(ShellViewModel.SettingsOpen) && shell.SettingsOpen) _ = Runtime.RefreshAsync();
        };
        Load(shell.Settings);
    }

    /// <summary>Raised when something outside the sheet (a footer notice) wants a section shown: "models", "runtime".</summary>
    public event Action<string>? SectionRequested;

    public void RequestSection(string section) => SectionRequested?.Invoke(section);

    public IReadOnlyList<SettingChoice> CodeThemes { get; }

    /// <summary>Settings → Runtime: the engine, its devices, the backend choice, updates and rollback.</summary>
    public RuntimeViewModel Runtime { get; }

    public IReadOnlyList<SettingChoice> DownloadLimits { get; }

    /// <summary>Asks the user for a folder (set by the sheet, which owns the window's storage provider); null when they decline.</summary>
    public Func<Task<string?>>? FolderPicker { get; set; }

    public IReadOnlyList<SettingChoice> AutoSaveModes { get; } =
    [
        new("off", "Off"),
        new("afterDelay", "After a delay"),
        new("onFocusChange", "When the editor loses focus"),
        new("onWindowChange", "When the window loses focus"),
    ];

    [ObservableProperty]
    private double _fontSize;

    [ObservableProperty]
    private double _lineHeight;

    [ObservableProperty]
    private bool _ligatures;

    [ObservableProperty]
    private double _tabSize;

    [ObservableProperty]
    private bool _insertSpaces;

    [ObservableProperty]
    private bool _autoClosingBrackets;

    [ObservableProperty]
    private SettingChoice? _codeTheme;

    [ObservableProperty]
    private SettingChoice? _autoSave;

    [ObservableProperty]
    private double _autoSaveDelay;

    [ObservableProperty]
    private bool _trimTrailingWhitespace;

    [ObservableProperty]
    private bool _insertFinalNewline;

    [ObservableProperty]
    private bool _restoreSession;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StoreLocationText), nameof(UsesDefaultStore))]
    private string _storeLocation = "";

    [ObservableProperty]
    private string _huggingFaceToken = "";

    [ObservableProperty]
    private bool _showToken;

    [ObservableProperty]
    private SettingChoice? _downloadLimit;

    public bool UsesDefaultStore => StoreLocation.Length == 0;

    /// <summary>Where new models go, in words: the folder, or the default one named.</summary>
    public string StoreLocationText => StoreLocation.Length > 0 ? StoreLocation : $"{Path.Combine(_shell.Paths.Data, "models")}  (default)";

    private void Load(WickedSettings s)
    {
        _loading = true;
        FontSize = s.FontSize;
        LineHeight = s.LineHeight;
        Ligatures = s.FontLigatures;
        TabSize = s.TabSize;
        InsertSpaces = s.InsertSpaces;
        AutoClosingBrackets = s.AutoClosingBrackets;
        CodeTheme = CodeThemes.FirstOrDefault(c => string.Equals(c.Value, s.CodeColorTheme, StringComparison.OrdinalIgnoreCase)) ?? CodeThemes[0];
        AutoSave = AutoSaveModes.FirstOrDefault(c => c.Value == s.AutoSave) ?? AutoSaveModes[0];
        AutoSaveDelay = s.AutoSaveDelay;
        TrimTrailingWhitespace = s.TrimTrailingWhitespace;
        InsertFinalNewline = s.InsertFinalNewline;
        RestoreSession = s.RestoreSession;
        StoreLocation = s.StoreLocation;
        HuggingFaceToken = s.HuggingFaceToken;
        var limit = s.DownloadLimit.ToString("0.##", CultureInfo.InvariantCulture);
        DownloadLimit = DownloadLimits.FirstOrDefault(c => c.Value == limit) ?? new SettingChoice(limit, limit + " MB/s");
        Runtime.Load(s);
        _loading = false;
    }

    // Each control's change, straight into the settings.
    partial void OnFontSizeChanged(double value) => Change(s => s with { FontSize = Math.Round(value) });

    partial void OnLineHeightChanged(double value) => Change(s => s with { LineHeight = Math.Round(value, 1) });

    partial void OnLigaturesChanged(bool value) => Change(s => s with { FontLigatures = value });

    partial void OnTabSizeChanged(double value) => Change(s => s with { TabSize = (int)Math.Round(value) });

    partial void OnInsertSpacesChanged(bool value) => Change(s => s with { InsertSpaces = value });

    partial void OnAutoClosingBracketsChanged(bool value) => Change(s => s with { AutoClosingBrackets = value });

    partial void OnCodeThemeChanged(SettingChoice? value) => Change(s => s with { CodeColorTheme = value?.Value ?? "" });

    partial void OnAutoSaveChanged(SettingChoice? value) => Change(s => s with { AutoSave = value?.Value ?? "off" });

    partial void OnAutoSaveDelayChanged(double value) => Change(s => s with { AutoSaveDelay = (int)Math.Round(value) });

    partial void OnTrimTrailingWhitespaceChanged(bool value) => Change(s => s with { TrimTrailingWhitespace = value });

    partial void OnInsertFinalNewlineChanged(bool value) => Change(s => s with { InsertFinalNewline = value });

    partial void OnRestoreSessionChanged(bool value) => Change(s => s with { RestoreSession = value });

    partial void OnStoreLocationChanged(string value) => Change(s => s with { StoreLocation = value });

    partial void OnDownloadLimitChanged(SettingChoice? value) =>
        Change(s => s with { DownloadLimit = double.TryParse(value?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb) ? mb : 0 });

    private CancellationTokenSource? _tokenDelay;

    // A token is typed or pasted in one go: written to settings.json a moment after the last keystroke, not once per key.
    partial void OnHuggingFaceTokenChanged(string value)
    {
        if (_loading) return;
        _tokenDelay?.Cancel();
        var delay = _tokenDelay = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMilliseconds(400), delay.Token).ContinueWith(
            _ => Dispatcher.UIThread.Post(() => Change(s => s with { HuggingFaceToken = value.Trim() })),
            CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    [RelayCommand]
    private async Task ChooseStore()
    {
        if (FolderPicker is null) return;
        if (await FolderPicker() is { Length: > 0 } folder) StoreLocation = folder;
    }

    [RelayCommand]
    private void UseDefaultStore() => StoreLocation = "";

    private void Change(Func<WickedSettings, WickedSettings> change)
    {
        if (!_loading) _ = _shell.UpdateSettingsAsync(change);
    }

    [RelayCommand]
    private Task OpenJson()
    {
        _shell.SettingsOpen = false;
        var command = _shell.Commands.First(c => c.Id == "workbench.action.openSettingsJson");
        return command.Run();
    }
}
