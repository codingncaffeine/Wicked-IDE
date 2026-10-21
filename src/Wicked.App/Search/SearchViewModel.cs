using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Core.Search;
using Wicked.Editor.Model;

namespace Wicked.App.Search;

/// <summary>A match as the results list shows it: the text before it, the match (struck through when a replacement is shown), after.</summary>
public sealed partial class SearchMatchRow(SearchFileRow file, TextMatch match) : ObservableObject
{
    public SearchFileRow File { get; } = file;

    public TextMatch Match { get; } = match;

    public string Before => Match.Preview[..Math.Min(Match.PreviewStart, Match.Preview.Length)].TrimStart();

    public string Hit => Match.Preview.Substring(Math.Min(Match.PreviewStart, Match.Preview.Length), Math.Min(Match.Length, Math.Max(0, Match.Preview.Length - Match.PreviewStart)));

    public string After => Match.PreviewStart + Match.Length <= Match.Preview.Length ? Match.Preview[(Match.PreviewStart + Match.Length)..] : "";

    /// <summary>What the match would become (shown beside it while the replace box is open).</summary>
    [ObservableProperty]
    private string? _replacement;

    public bool ShowsReplacement => Replacement is not null;

    partial void OnReplacementChanged(string? value) => OnPropertyChanged(nameof(ShowsReplacement));
}

/// <summary>A file in the results: its name, folder, count, and whether its matches are folded away.</summary>
public sealed partial class SearchFileRow(string path, string folder) : ObservableObject
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder { get; } = folder;

    public List<SearchMatchRow> Matches { get; } = [];

    public string Count => Matches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>
/// The Search panel (Ctrl+Shift+F): a query with VS Code's toggles, a replacement, include and exclude globs, and the
/// results as they arrive, file by file. Searching runs on workers over the workspace's files (open files as edited);
/// results reach the list in batches at most every 50 ms. Replacing works on open files through their editors (one undo
/// step each) and on the rest on disk.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly ConcurrentQueue<FileMatches> _arrived = new();
    private readonly DispatcherTimer _debounce;
    private readonly DispatcherTimer _flush;
    private readonly Dictionary<string, SearchFileRow> _files = new(StringComparer.Ordinal);
    private CancellationTokenSource? _running;

    public SearchViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _ = RunAsync();
        };
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _flush.Tick += (_, _) => Flush();
    }

    /// <summary>The flat list the panel shows (file headers and their matches), so a long result virtualises.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public IEnumerable<SearchFileRow> Files => _files.Values;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private bool _useRegex;

    [ObservableProperty]
    private string _replace = "";

    [ObservableProperty]
    private bool _showReplace;

    [ObservableProperty]
    private bool _showDetails;

    [ObservableProperty]
    private string _include = "";

    [ObservableProperty]
    private string _exclude = "";

    /// <summary>"14 results in 3 files", "Searching…", or what is wrong with the query.</summary>
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isSearching;

    public int MatchCount { get; private set; }

    [RelayCommand]
    private void ToggleShowReplace() => ShowReplace = !ShowReplace;

    [RelayCommand]
    private void ToggleShowDetails() => ShowDetails = !ShowDetails;

    partial void OnQueryChanged(string value) => Restart();

    partial void OnMatchCaseChanged(bool value) => Restart();

    partial void OnWholeWordChanged(bool value) => Restart();

    partial void OnUseRegexChanged(bool value) => Restart();

    partial void OnIncludeChanged(string value) => Restart();

    partial void OnExcludeChanged(string value) => Restart();

    partial void OnReplaceChanged(string value) => ShowReplacements();

    partial void OnShowReplaceChanged(bool value) => ShowReplacements();

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private ProjectQuery CurrentQuery => new(Query, MatchCase, WholeWord, UseRegex, Include, Exclude);

    /// <summary>Enter in the query box: search now.</summary>
    [RelayCommand]
    public async Task RunAsync()
    {
        _debounce.Stop();
        _running?.Cancel();
        var cts = _running = new CancellationTokenSource();
        Clear();
        if (Query.Length == 0)
        {
            Summary = "";
            return;
        }

        if (_shell.Workbench.Root is not { } root || _shell.Workbench.Index is not { } index)
        {
            Summary = "Open a folder to search its files.";
            return;
        }

        // The open files as they are in their editors: their texts are immutable, read on the workers.
        var open = new Dictionary<string, Func<string>>(StringComparer.Ordinal);
        foreach (var tab in _shell.Workbench.AllTabs)
        {
            if (tab.Path is not { } path || open.ContainsKey(path)) continue;
            var text = tab.Document.Text;
            open[path] = text.ToString;
        }

        IsSearching = true;
        Summary = "Searching…";
        _flush.Start();
        SearchSummary summary;
        try
        {
            var query = CurrentQuery;
            var files = index.Files;
            summary = await Task.Run(() => ProjectSearch.RunAsync(root, files, query, open, _arrived.Enqueue, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (_running == cts)
            {
                IsSearching = false;
                _flush.Stop();
            }
        }

        if (cts.IsCancellationRequested) return;
        Flush();
        Summary = summary.Error is { } error ? error
            : summary.Matches == 0 ? $"No results ({summary.FilesSearched} files searched)."
            : $"{summary.Matches} result{(summary.Matches == 1 ? "" : "s")} in {summary.FilesWithMatches} file{(summary.FilesWithMatches == 1 ? "" : "s")}"
              + (summary.Capped ? " (stopped at the limit; narrow the search)" : "");
        Completed?.Invoke();
    }

    /// <summary>A search finished (tests wait on it).</summary>
    public event Action? Completed;

    private void Clear()
    {
        while (_arrived.TryDequeue(out _))
        {
        }

        _files.Clear();
        Rows.Clear();
        MatchCount = 0;
    }

    /// <summary>The files found since the last flush added to the list, in path order within the batch.</summary>
    private void Flush()
    {
        var batch = new List<FileMatches>();
        while (_arrived.TryDequeue(out var f)) batch.Add(f);
        if (batch.Count == 0) return;
        var root = _shell.Workbench.Root ?? "";
        foreach (var f in batch.OrderBy(b => b.Path, StringComparer.Ordinal))
        {
            var folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(root, f.Path)) ?? "";
            var row = new SearchFileRow(f.Path, folder);
            foreach (var m in f.Matches) row.Matches.Add(new SearchMatchRow(row, m));
            _files[f.Path] = row;
            MatchCount += row.Matches.Count;
            Rows.Add(row);
            foreach (var m in row.Matches) Rows.Add(m);
        }

        ShowReplacements();
    }

    private void ShowReplacements()
    {
        foreach (var file in _files.Values)
        {
            foreach (var m in file.Matches) m.Replacement = ShowReplace ? (UseRegex ? ReplacementFor(m) : Replace) : null;
        }
    }

    private string ReplacementFor(SearchMatchRow row)
    {
        if (CurrentQuery.Compile().Regex is not { } regex) return Replace;
        var m = regex.Match(row.Hit);
        return m.Success ? m.Result(Replace) : Replace;
    }

    [RelayCommand]
    private void Toggle(SearchFileRow file)
    {
        file.IsExpanded = !file.IsExpanded;
        var at = Rows.IndexOf(file);
        if (at < 0) return;
        if (!file.IsExpanded)
        {
            while (at + 1 < Rows.Count && Rows[at + 1] is SearchMatchRow) Rows.RemoveAt(at + 1);
        }
        else
        {
            for (var i = 0; i < file.Matches.Count; i++) Rows.Insert(at + 1 + i, file.Matches[i]);
        }
    }

    /// <summary>A match clicked: its file opens with the match selected.</summary>
    [RelayCommand]
    private Task Open(SearchMatchRow row) =>
        _shell.OpenAtAsync(row.File.Path, row.Match.Line, row.Match.Column, row.Match.Line, row.Match.Column + row.Match.Length);

    // ── Replacing ────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task ReplaceOne(SearchMatchRow row) => ReplaceInAsync(row.File, new HashSet<int> { row.Match.Offset });

    [RelayCommand]
    private Task ReplaceFile(SearchFileRow file) => ReplaceInAsync(file, null);

    /// <summary>Replace All: every match in every file, after one question naming how many and where.</summary>
    [RelayCommand]
    private async Task ReplaceAll()
    {
        if (MatchCount == 0) return;
        var files = _files.Values.ToList();
        if (_shell.Workbench.Dialogs is { } dialogs
            && !await dialogs.ConfirmAsync($"Replace {MatchCount} occurrence{(MatchCount == 1 ? "" : "s")} across {files.Count} file{(files.Count == 1 ? "" : "s")} with '{Replace}'?", "Open files change in their editors and can be undone there; the others are written now.", "Replace"))
        {
            return;
        }

        foreach (var file in files) await ReplaceInAsync(file, null, rerun: false);
        await RunAsync();
    }

    /// <summary>Replaces matches in one file: in its editor when it is open (one undo step), else on disk on a worker.</summary>
    private async Task ReplaceInAsync(SearchFileRow file, IReadOnlySet<int>? only, bool rerun = true)
    {
        var query = CurrentQuery;
        var replacement = Replace;
        var tab = _shell.Workbench.AllTabs.FirstOrDefault(t => string.Equals(t.Path, file.Path, StringComparison.Ordinal));
        try
        {
            if (tab is not null)
            {
                var doc = tab.Document;
                var text = doc.Text;
                if (query.Compile().Regex is not { } regex) return;
                var changes = await Task.Run(() =>
                {
                    var all = text.ToString();
                    return regex.Matches(all).Where(m => m.Length > 0 && (only is null || only.Contains(m.Index)))
                        .Select(m => new TextChange(m.Index, m.Length, query.Regex ? m.Result(replacement) : replacement)).ToList();
                });
                // Edited while the matches were found: nothing is changed, the search runs again.
                if (changes.Count > 0 && ReferenceEquals(text, doc.Text)) doc.Apply(changes, Wicked.App.Language.LspText.MapSelections(doc.Selections, changes), EditKind.Other, doc.PrimaryIndex);
            }
            else
            {
                await Task.Run(() => ProjectSearch.ReplaceInFileAsync(file.Path, query, replacement, only, CancellationToken.None));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.Notices.Post(new Notice($"replace:{file.Path}", $"{file.Name} could not be changed: {ex.Message}", NoticeKind.Error));
        }

        if (rerun) await RunAsync();
    }
}
