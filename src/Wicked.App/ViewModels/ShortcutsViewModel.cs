using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.Shell;
using Wicked.Core.Settings;

namespace Wicked.App.ViewModels;

/// <summary>One row of the Keyboard Shortcuts editor: a command and one of its keys (or none), where the keys come from.</summary>
public sealed record ShortcutRow(WorkbenchCommand Command, string? Keys, string Source, string? When, bool HasConflict)
{
    public string Title => Command.Label;

    public string Id => Command.Id;

    /// <summary>The keys as people read them ("Ctrl+K Ctrl+S"); empty for a command with none.</summary>
    public string DisplayKeys => Keys is null ? "" : KeyNames.Readable(Keys);

    /// <summary>A line of keybindings.json (edited in place) rather than a default, a keymap preset's or an extension's key.</summary>
    public bool User => Source == KeyDispatcher.UserSource;

    /// <summary>Where the keys come from, as VS Code's Source column: Default, a keymap preset, Extension or User.</summary>
    public string SourceText => Keys is null ? "" : Source;

    public string WhenText => When ?? "";
}

/// <summary>
/// The Keyboard Shortcuts editor (Ctrl+K Ctrl+S), as VS Code's: every command with its keys, searched by words or by
/// keys pressed, a new key recorded with the commands that already have it shown, Remove and Reset; every change is a
/// change to keybindings.json, which the window reads back as it does a hand edit.
/// </summary>
public sealed partial class ShortcutsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private List<ShortcutRow> _all = [];

    public ShortcutsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        shell.PropertyChanged += OnShellChanged;
        Refresh();
    }

    public ObservableCollection<ShortcutRow> Rows { get; } = [];

    /// <summary>Words that must all appear in a command's title or id, or with <see cref="RecordKeys"/> on, the keys pressed.</summary>
    [ObservableProperty]
    private string _searchText = "";

    /// <summary>The search box takes pressed keys instead of words ("Record Keys", Alt+K in VS Code).</summary>
    [ObservableProperty]
    private bool _recordKeys;

    /// <summary>Keys recorded in the search box, in Avalonia's names; null when searching by words.</summary>
    public string? RecordedKeys { get; private set; }

    [ObservableProperty]
    private string _resultText = "";

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Keys)) Refresh();
    }

    partial void OnSearchTextChanged(string value) => Filter();

    partial void OnRecordKeysChanged(bool value)
    {
        RecordedKeys = null;
        SearchText = "";
    }

    /// <summary>A key pressed in the search box while recording: its keys become the search ("Ctrl+K" then "Ctrl+K Ctrl+S").</summary>
    public void Record(string keys)
    {
        RecordedKeys = RecordedKeys is { } first && !first.Contains(' ', StringComparison.Ordinal) ? $"{first} {keys}" : keys;
        SearchText = $"\"{KeyNames.Readable(RecordedKeys)}\"";
    }

    /// <summary>The rows made again from the key map in force.</summary>
    public void Refresh()
    {
        var keys = _shell.Keys;
        var whenByUser = _shell.UserKeybindings.Where(b => !b.Remove && b.Keys is not null).GroupBy(b => (b.Command, b.Keys!)).ToDictionary(g => g.Key, g => g.First().When);
        var rows = new List<ShortcutRow>();
        var bound = keys.Bindings.ToList();
        // Keys more than one command answers to, whatever the "when" (VS Code shows them through "Show Same Keybindings").
        var shared = bound.GroupBy(b => b.Keys).Where(g => g.Select(b => b.Command.Id).Distinct().Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var command in _shell.Commands.DistinctBy(c => c.Id))
        {
            var mine = bound.Where(b => b.Command.Id == command.Id).ToList();
            if (mine.Count == 0 && command.EditorKeys && keys.KeysFor(command.Id) is { Length: > 0 } editorKeys)
            {
                mine.Add((editorKeys, command, "Default"));
            }

            if (mine.Count == 0) rows.Add(new ShortcutRow(command, null, "", null, false));
            foreach (var (k, _, source) in mine)
            {
                var when = source == KeyDispatcher.UserSource ? whenByUser.GetValueOrDefault((command.Id, k)) : command.WhenText;
                rows.Add(new ShortcutRow(command, k, source, when, shared.Contains(k)));
            }
        }

        _all = [.. rows.OrderBy(r => r.Keys is null ? 1 : 0).ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)];
        Filter();
    }

    private void Filter()
    {
        Rows.Clear();
        foreach (var row in _all.Where(Matches)) Rows.Add(row);
        ResultText = Rows.Count == 1 ? "1 keybinding" : $"{Rows.Count} keybindings";
    }

    private bool Matches(ShortcutRow row)
    {
        if (RecordedKeys is { } recorded) return row.Keys is { } k && (k == recorded || k.StartsWith(recorded + " ", StringComparison.Ordinal));
        foreach (var word in SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!(row.Title.Contains(word, StringComparison.OrdinalIgnoreCase) || row.Id.Contains(word, StringComparison.OrdinalIgnoreCase)
                || row.DisplayKeys.Contains(word.Trim('"'), StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The other commands <paramref name="keys"/> already runs, for the record box ("2 existing commands have this keybinding").</summary>
    public IReadOnlyList<string> CommandsWith(string keys, string exceptId) =>
        [.. _all.Where(r => r.Keys == keys && r.Id != exceptId).Select(r => r.Title).Distinct()];

    /// <summary>"Show Same Keybindings": the rows with these keys.</summary>
    public void ShowSame(string keys)
    {
        RecordKeys = true;
        RecordedKeys = keys;
        SearchText = $"\"{KeyNames.Readable(keys)}\"";
    }

    public Task ChangeAsync(ShortcutRow row, string keys) => EditAsync(json => KeybindingsEdit.Change(json, row.Id, keys, row.Keys, !row.User, row.When));

    public Task RemoveAsync(ShortcutRow row) =>
        row.Keys is { } keys ? EditAsync(json => KeybindingsEdit.Remove(json, row.Id, keys, !row.User)) : Task.CompletedTask;

    public Task ResetAsync(ShortcutRow row) => EditAsync(json => KeybindingsEdit.Reset(json, row.Id));

    /// <summary>keybindings.json read, changed and written by a worker; the window then reads it back as it does a hand edit.</summary>
    private async Task EditAsync(Func<string, string> change)
    {
        var path = _shell.Paths.KeybindingsFile;
        try
        {
            await Task.Run(async () =>
            {
                var json = File.Exists(path) ? await File.ReadAllTextAsync(path).ConfigureAwait(false) : "";
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await Wicked.Core.Files.TextFile.WriteAsync(path, change(json), Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None).ConfigureAwait(false);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _shell.Notices.Post(new Wicked.Core.Notice("keybindings", $"keybindings.json could not be changed: {ex.Message}", Wicked.Core.NoticeKind.Error, $"tab/{path}"));
            return;
        }

        await _shell.ReloadKeybindingsAsync();
    }
}
