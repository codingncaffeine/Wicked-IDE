using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core.Files;
using Wicked.Editor.Model;

namespace Wicked.App.ViewModels;

/// <summary>One row of the palette: what it says, a quieter line beside it, its keys, and what choosing it does.</summary>
public sealed record QuickItem(string Label, string Detail, string Keys, Func<Task> Choose, Action? Preview = null);

/// <summary>
/// The palette at the top of the window (Ctrl+P, Ctrl+Shift+P, Ctrl+G). The first character picks the mode, as in VS
/// Code: ">" commands, ":" a line in the current file, anything else a file in the workspace. File matching runs on a
/// worker; a newer keystroke cancels the older search.
/// </summary>
public sealed partial class QuickInputViewModel(ShellViewModel shell) : ObservableObject
{
    private CancellationTokenSource? _search;

    public ObservableCollection<QuickItem> Items { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private int _selected;

    [ObservableProperty]
    private string _placeholder = "";

    private IReadOnlyList<QuickItem>? _list;
    private Action? _onCancel;
    private Action<string>? _prompt;

    /// <summary>A line of text to type (a new file's name): Enter accepts it, Escape gives null.</summary>
    public void OpenPrompt(string placeholder, string initial, Action<string?> done)
    {
        _list = null;
        _prompt = text => done(text);
        _onCancel = () => done(null);
        IsOpen = true;
        Query = initial;
        Placeholder = placeholder;
        _ = RefreshAsync();
    }

    public void Open(string prefix)
    {
        _prompt = null;
        _list = null;
        _onCancel = null;
        IsOpen = true;
        Query = prefix;
        _ = RefreshAsync();
    }

    /// <summary>A list of choices of the caller's own (a theme picker): filtered by what is typed; Escape calls <paramref name="onCancel"/>.</summary>
    public void OpenList(string placeholder, IReadOnlyList<QuickItem> items, int selected, Action? onCancel)
    {
        _prompt = null;
        _list = items;
        _onCancel = onCancel;
        IsOpen = true;
        Query = "";
        Placeholder = placeholder;
        _ = RefreshAsync().ContinueWith(_ => Selected = Math.Clamp(selected, 0, Math.Max(0, Items.Count - 1)), TaskScheduler.FromCurrentSynchronizationContext());
    }

    public void Close()
    {
        var cancel = _onCancel;
        _onCancel = null;
        IsOpen = false;
        _search?.Cancel();
        Items.Clear();
        cancel?.Invoke();
    }

    partial void OnSelectedChanged(int value)
    {
        if (value >= 0 && value < Items.Count) Items[value].Preview?.Invoke();
    }

    partial void OnQueryChanged(string value) => _ = RefreshAsync();

    public void Move(int by)
    {
        if (Items.Count == 0) return;
        Selected = (Selected + by + Items.Count) % Items.Count;
    }

    public async Task ChooseAsync(int index = -1)
    {
        if (_prompt is { } prompt)
        {
            var text = Query;
            _onCancel = null;
            _prompt = null;
            Close();
            prompt(text);
            return;
        }

        index = index < 0 ? Selected : index;
        if (index < 0 || index >= Items.Count) return;
        var item = Items[index];
        _onCancel = null; // chosen, not cancelled
        Close();
        await item.Choose();
    }

    private async Task RefreshAsync()
    {
        if (!IsOpen) return;
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var query = Query;
        List<QuickItem> items;
        if (_prompt is not null)
        {
            items = [new QuickItem("Press Enter to confirm, or Escape to cancel", "", "", () => Task.CompletedTask)];
        }
        else if (_list is { } list)
        {
            items = query.Length == 0 ? [.. list] : [.. list.Select(i => (Item: i, Score: FuzzyMatch.Score(query, i.Label))).Where(p => p.Score >= 0).OrderByDescending(p => p.Score).Select(p => p.Item)];
        }
        else if (query.StartsWith('>'))
        {
            Placeholder = "Type the name of a command";
            var text = query[1..].Trim();
            items = [.. shell.Commands
                .Select(c => (Command: c, Score: text.Length == 0 ? 0 : FuzzyMatch.Score(text, c.Label)))
                .Where(p => p.Score >= 0)
                .OrderByDescending(p => p.Score).ThenBy(p => p.Command.Label, StringComparer.Ordinal)
                .Select(p => new QuickItem(p.Command.Label, "", shell.Keys.KeysFor(p.Command.Id), p.Command.Run))];
        }
        else if (query.StartsWith(':'))
        {
            items = GoToLine(query[1..].Trim());
        }
        else if (query.StartsWith('@'))
        {
            Placeholder = "Go to a symbol in this file";
            items = await shell.Language.SymbolItemsAsync(shell.Workbench.ActiveTab, query[1..].Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;
        }
        else if (query.StartsWith('#'))
        {
            Placeholder = "Go to a symbol in the workspace";
            items = await shell.Language.WorkspaceSymbolItemsAsync(query[1..].Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;
        }
        else
        {
            Placeholder = "Search files by name (> commands, : a line, @ a symbol, # a workspace symbol)";
            items = await FilesAsync(query.Trim(), cts.Token);
            if (cts.IsCancellationRequested) return;
        }

        Items.Clear();
        foreach (var item in items.Take(60)) Items.Add(item);
        Selected = 0;
    }

    private List<QuickItem> GoToLine(string text)
    {
        var tab = shell.Workbench.ActiveTab;
        if (tab is null)
        {
            Placeholder = "Open a file first to go to a line";
            return [];
        }

        var doc = tab.Document;
        Placeholder = $"Type a line number between 1 and {doc.Text.LineCount} (and :column)";
        var parts = text.Split(':', ',');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var line)) return [];
        var column = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 1;
        line = Math.Clamp(line, 1, doc.Text.LineCount);
        return [new QuickItem($"Go to line {line}" + (parts.Length > 1 ? $", column {column}" : ""), "", "", () =>
        {
            var offset = doc.Text.Offset(line - 1, column - 1);
            doc.SetSelections([Selection.Caret(offset)]);
            tab.Editor?.RevealCaret();
            return Task.CompletedTask;
        })];
    }

    private async Task<List<QuickItem>> FilesAsync(string text, CancellationToken ct)
    {
        var bench = shell.Workbench;
        QuickItem ForPath(string full, string relative) =>
            new(Path.GetFileName(full), Path.GetDirectoryName(relative) ?? "", "", async () => await bench.OpenAsync(full));

        // With nothing typed: the open files, most recent first, as VS Code shows its recently opened.
        if (text.Length == 0)
        {
            return [.. bench.AllTabs.Where(t => t.Path is not null).Select(t => t.Path!).Distinct()
                .Select(p => ForPath(p, bench.Root is { } r && p.StartsWith(r, StringComparison.Ordinal) ? Path.GetRelativePath(r, p) : p))];
        }

        if (bench.Index is not { } index) return [];
        try
        {
            var found = await Task.Run(() => index.Search(text, 60), ct);
            return [.. found.Select(rel => ForPath(Path.Combine(index.Root, rel.Replace('/', Path.DirectorySeparatorChar)), rel))];
        }
        catch (OperationCanceledException)
        {
            return [];
        }
    }
}
