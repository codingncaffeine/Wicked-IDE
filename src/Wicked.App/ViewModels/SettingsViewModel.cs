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
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.Settings)) Load(shell.Settings);
        };
        Load(shell.Settings);
    }

    public IReadOnlyList<SettingChoice> CodeThemes { get; }

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
