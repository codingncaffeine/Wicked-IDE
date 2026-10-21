using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Settings;

namespace Wicked.App.ViewModels;

/// <summary>
/// One setting in the searchable list, as VS Code's Settings editor shows it: its name and group, what it does, a control
/// for its kind, a mark and Reset when it differs from the default.
/// </summary>
public sealed partial class SettingRow : ObservableObject
{
    private readonly Func<string, JsonNode?, Task> _set;
    private bool _loading;

    public SettingRow(SettingInfo info, Func<string, JsonNode?, Task> set)
    {
        Info = info;
        _set = set;
    }

    public SettingInfo Info { get; }

    public string Key => Info.Key;

    /// <summary>"Text Editor › Font Size".</summary>
    public string Heading => $"{Info.Group} › {Info.Title}";

    public string Description => Info.Description;

    public bool IsToggle => Info.Kind == SettingKind.Toggle;

    public bool IsNumber => Info.Kind is SettingKind.Integer or SettingKind.Number;

    public bool IsText => Info.Kind == SettingKind.Text && !Info.Secret;

    public bool IsSecret => Info.Kind == SettingKind.Text && Info.Secret;

    public bool IsChoice => Info.Kind == SettingKind.Choice;

    /// <summary>A list or an object: the row links to settings.json instead of a control.</summary>
    public bool IsJson => Info.Kind == SettingKind.Json;

    public IReadOnlyList<string> Values => Info.Values;

    public decimal Minimum => double.IsNaN(Info.Minimum) ? decimal.MinValue : (decimal)Info.Minimum;

    public decimal Maximum => double.IsNaN(Info.Maximum) ? decimal.MaxValue : (decimal)Info.Maximum;

    public string NumberFormat => Info.Kind == SettingKind.Integer ? "0" : "0.##";

    public decimal Increment => Info.Kind == SettingKind.Integer ? 1 : 0.1m;

    [ObservableProperty]
    private bool _isModified;

    [ObservableProperty]
    private bool _toggleValue;

    [ObservableProperty]
    private decimal? _numberValue;

    [ObservableProperty]
    private string _textValue = "";

    [ObservableProperty]
    private string? _choiceValue;

    /// <summary>The value for the list, from the settings now in force.</summary>
    public void Load(WickedSettings settings)
    {
        _loading = true;
        try
        {
            var value = Info.ValueIn(settings);
            switch (Info.Kind)
            {
                case SettingKind.Toggle: ToggleValue = value is true; break;
                case SettingKind.Integer: NumberValue = value is int i ? i : null; break;
                case SettingKind.Number: NumberValue = value is double d ? (decimal)d : null; break;
                case SettingKind.Text: TextValue = value as string ?? ""; break;
                case SettingKind.Choice: ChoiceValue = value as string; break;
            }

            IsModified = Info.IsModified(settings);
        }
        finally
        {
            _loading = false;
        }
    }

    partial void OnToggleValueChanged(bool value) => Apply(JsonValue.Create(value));

    partial void OnNumberValueChanged(decimal? value)
    {
        if (value is not { } v) return;
        Apply(Info.Kind == SettingKind.Integer ? JsonValue.Create((int)v) : JsonValue.Create((double)v));
    }

    partial void OnTextValueChanged(string value) => Apply(JsonValue.Create(value));

    partial void OnChoiceValueChanged(string? value)
    {
        if (value is not null) Apply(JsonValue.Create(value));
    }

    private void Apply(JsonNode? value)
    {
        if (!_loading) _ = _set(Key, value);
    }

    /// <summary>Back to the default (VS Code's "Reset Setting").</summary>
    [RelayCommand]
    private Task Reset() => _set(Key, null);

    /// <summary>The setting as a line of settings.json ("editor.fontSize": 16), for the clipboard.</summary>
    public string AsJson(WickedSettings settings) =>
        $"\"{Key}\": {JsonSerializer.Serialize(Info.ValueIn(settings), Info.Property.PropertyType, WickedSettings.Json)}";
}

/// <summary>The Settings sheet's search over every setting (VS Code's Settings editor).</summary>
public sealed partial class SettingsViewModel
{
    private List<SettingRow>? _allRows;

    /// <summary>
    /// The search: words that must all appear in a setting's name, key, group or description; "@modified" keeps the
    /// changed ones, "@id:editor.fontSize" one setting by its key.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private string _searchText = "";

    /// <summary>The list shows every setting (the "All settings" button), with or without a search.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching))]
    private bool _showAll;

    /// <summary>The searchable list is shown in place of the sheet's sections.</summary>
    public bool IsSearching => SearchText.Length > 0 || ShowAll;

    public ObservableCollection<SettingRow> Rows { get; } = [];

    /// <summary>"3 settings found", "No settings found", or nothing while the sections show.</summary>
    [ObservableProperty]
    private string _resultText = "";

    private List<SettingRow> AllRows => _allRows ??= [.. SettingsSchema.All.Select(info => new SettingRow(info, SetAsync))];

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnShowAllChanged(bool value) => Filter();

    [RelayCommand]
    private void ShowAllSettings()
    {
        ShowAll = true;
        Filter();
    }

    [RelayCommand]
    private void ShowSections()
    {
        ShowAll = false;
        SearchText = "";
    }

    private void Filter()
    {
        Rows.Clear();
        if (!IsSearching)
        {
            ResultText = "";
            return;
        }

        var settings = _shell.Settings;
        foreach (var row in AllRows.Where(r => Matches(r, SearchText, settings)))
        {
            row.Load(settings);
            Rows.Add(row);
        }

        ResultText = Rows.Count switch
        {
            0 => "No settings found",
            1 => "1 setting found",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} settings found"),
        };
    }

    /// <summary>Whether <paramref name="row"/> answers the search <paramref name="query"/>.</summary>
    public static bool Matches(SettingRow row, string query, WickedSettings settings)
    {
        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Equals("@modified", StringComparison.OrdinalIgnoreCase))
            {
                if (!row.Info.IsModified(settings)) return false;
            }
            else if (word.StartsWith("@id:", StringComparison.OrdinalIgnoreCase))
            {
                if (!row.Key.Equals(word[4..], StringComparison.OrdinalIgnoreCase)) return false;
            }
            else if (!(row.Key.Contains(word, StringComparison.OrdinalIgnoreCase) || row.Heading.Contains(word, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(word, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The rows follow the settings when they change (from the list, the sections or settings.json by hand).</summary>
    private void ReloadRows(WickedSettings settings)
    {
        foreach (var row in Rows) row.Load(settings);
    }

    private Task SetAsync(string key, JsonNode? value) => _shell.UpdateSettingsAsync(s => SettingsSchema.With(s, key, value) ?? s);

    /// <summary>The setting as a line of settings.json, for "Copy Setting as JSON".</summary>
    public string SettingJson(SettingRow row) => row.AsJson(_shell.Settings);
}
