using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Editor.Model;
using Wicked.Editor.Search;

namespace Wicked.App.Editor;

/// <summary>
/// One editor's find and replace. Searching runs on a worker over the text as it is now (an immutable rope); a newer
/// query or edit cancels an older search, and only results for the document's current version are shown.
/// </summary>
public sealed partial class FindViewModel : ObservableObject
{
    private readonly CodeEditor _editor;
    private readonly DispatcherTimer _afterEdit;
    private CancellationTokenSource? _search;

    public FindViewModel(CodeEditor editor)
    {
        _editor = editor;
        // Made stopped: the (interval, priority, handler) constructor starts a timer at once.
        _afterEdit = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(120) };
        _afterEdit.Tick += (_, _) =>
        {
            _afterEdit.Stop();
            _ = SearchAsync(keepPosition: true);
        };
        editor.Document.Changed += OnDocumentChanged;
    }

    private void OnDocumentChanged(DocumentChange change)
    {
        // The Find in Selection scope moves with the text, as VS Code's does.
        if (_scope.Count > 0) _scope = [.. _scope.Select(s => (ChangeSet.Map(s.Start, change.Changes, stickAfter: false), ChangeSet.Map(s.End, change.Changes)))];
        if (!IsOpen) return;
        _afterEdit.Stop();
        _afterEdit.Start();
    }

    /// <summary>The editor closed: no more searching as its document changes.</summary>
    public void Detach()
    {
        _editor.Document.Changed -= OnDocumentChanged;
        _afterEdit.Stop();
        _search?.Cancel();
    }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _showReplace;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private string _replacement = "";

    [ObservableProperty]
    private bool _matchCase;

    [ObservableProperty]
    private bool _wholeWord;

    [ObservableProperty]
    private bool _regex;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _hasError;

    /// <summary>Find in Selection (Alt+L): only matches inside the selections made when it was turned on.</summary>
    [ObservableProperty]
    private bool _inSelection;

    private List<(int Start, int End)> _scope = [];

    /// <summary>The selections when Find opened, the scope Alt+L takes while the text is unchanged since.</summary>
    private List<Selection>? _selectedAtOpen;
    private long _selectedAtOpenVersion;

    /// <summary>The queries searched for, newest last, shared by every editor (Up and Down in the box walk them).</summary>
    private static readonly List<string> History = [];
    private const int HistoryLimit = 20;
    private int _historyAt = -1;

    /// <summary>The widget's view puts the keyboard in the query box when this fires.</summary>
    public event Action? FocusRequested;

    private SearchQuery CurrentQuery => new(Query, MatchCase, WholeWord, Regex);

    partial void OnQueryChanged(string value) => _ = SearchAsync(keepPosition: false);

    partial void OnMatchCaseChanged(bool value) => _ = SearchAsync(keepPosition: true);

    partial void OnWholeWordChanged(bool value) => _ = SearchAsync(keepPosition: true);

    partial void OnRegexChanged(bool value) => _ = SearchAsync(keepPosition: true);

    partial void OnInSelectionChanged(bool value)
    {
        // The scope is what was selected when Find opened (searching moves the selection onto the matches since), else the
        // selections now; a caret alone scopes its line.
        var rope = _editor.Document.Text;
        IReadOnlyList<Selection> selections = _selectedAtOpen is { Count: > 0 } atOpen && _selectedAtOpenVersion == _editor.Document.Version ? atOpen : _editor.Document.Selections;
        _scope = !value ? [] : [.. selections.Select(s =>
        {
            if (!s.IsEmpty) return (s.Start, s.End);
            var line = rope.LineOf(s.Active);
            return (rope.LineStart(line), rope.LineStart(line) + rope.LineLength(line));
        })];
        _ = SearchAsync(keepPosition: true);
    }

    [RelayCommand]
    private void ToggleInSelection() => InSelection = !InSelection;

    /// <summary>Remembers the query when it is searched for (Enter, the arrows, closing).</summary>
    private void Remember()
    {
        _historyAt = -1;
        if (Query.Length == 0) return;
        lock (History)
        {
            History.Remove(Query);
            History.Add(Query);
            if (History.Count > HistoryLimit) History.RemoveAt(0);
        }
    }

    /// <summary>Up (-1) and Down (+1) in the query box: the queries searched for before, the newest first going up.</summary>
    public void Recall(int direction)
    {
        string recalled;
        lock (History)
        {
            if (History.Count == 0) return;
            if (_historyAt < 0) _historyAt = History.Count;
            _historyAt = Math.Clamp(_historyAt + direction, 0, History.Count);
            recalled = _historyAt == History.Count ? "" : History[_historyAt];
        }

        var at = _historyAt;
        Query = recalled;
        _historyAt = at;
    }

    /// <summary>Ctrl+F / Ctrl+H: a one-line selection becomes the query, as in VS Code.</summary>
    public void Open(bool replace)
    {
        var doc = _editor.Document;
        var primary = doc.Primary;
        _selectedAtOpen = doc.Selections.Any(s => !s.IsEmpty) ? [.. doc.Selections] : null;
        _selectedAtOpenVersion = doc.Version;
        if (!primary.IsEmpty && primary.Length < 200)
        {
            var selected = doc.Text.Substring(primary.Start, primary.Length);
            if (!selected.Contains('\n')) Query = selected;
        }

        ShowReplace = replace;
        if (InSelection) OnInSelectionChanged(true); // a new selection is the new scope
        IsOpen = true;
        FocusRequested?.Invoke();
        _ = SearchAsync(keepPosition: true);
    }

    [RelayCommand]
    public void Close()
    {
        Remember();
        IsOpen = false;
        _search?.Cancel();
        _editor.View.Found = SearchResult.None;
        _editor.View.CurrentMatch = -1;
        _editor.RevealCaret();
    }

    [RelayCommand]
    private void ToggleReplace() => ShowReplace = !ShowReplace;

    [RelayCommand]
    private void ToggleMatchCase() => MatchCase = !MatchCase;

    [RelayCommand]
    private void ToggleWholeWord() => WholeWord = !WholeWord;

    [RelayCommand]
    private void ToggleRegex() => Regex = !Regex;

    private async Task SearchAsync(bool keepPosition)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        var doc = _editor.Document;
        var text = doc.Text;
        var version = doc.Version;
        var query = CurrentQuery;
        SearchResult result;
        try
        {
            result = await Task.Run(() => TextSearch.Find(text, version, query, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested || !IsOpen || version != doc.Version) return;
        if (InSelection && _scope.Count > 0)
        {
            var scope = _scope;
            result = result with { Matches = [.. result.Matches.Where(m => scope.Any(s => m.Start >= s.Start && m.Start + m.Length <= s.End))] };
        }

        var view = _editor.View;
        view.Found = result;
        HasError = result.Error is not null;
        view.CurrentMatch = result.Matches.IsEmpty ? -1 : TextSearch.NextIndex(result.Matches, doc.Primary.Start);
        if (!keepPosition && view.CurrentMatch >= 0) Select(view.CurrentMatch);
        UpdateStatus();
        _editor.InvalidateVisual();
    }

    private void UpdateStatus()
    {
        var view = _editor.View;
        var found = view.Found;
        Status = found.Error is { } error ? error
            : Query.Length == 0 ? ""
            : found.Matches.IsEmpty ? "No results"
            : string.Create(CultureInfo.InvariantCulture, $"{(view.CurrentMatch >= 0 ? view.CurrentMatch + 1 : 0)} of {found.Matches.Length}{(found.Capped ? "+" : "")}");
    }

    [RelayCommand]
    public void Next() => Step(forward: true);

    [RelayCommand]
    public void Previous() => Step(forward: false);

    private void Step(bool forward)
    {
        Remember();
        var view = _editor.View;
        var matches = view.Found.Matches;
        if (matches.IsEmpty || view.Found.Version != _editor.Document.Version) return;
        var primary = _editor.Document.Primary;
        var index = forward ? TextSearch.NextIndex(matches, primary.IsEmpty ? primary.Start : primary.Start + 1)
                            : TextSearch.PreviousIndex(matches, primary.Start);
        Select(index);
        UpdateStatus();
    }

    private void Select(int index)
    {
        var (start, length) = _editor.View.Found.Matches[index];
        _editor.View.CurrentMatch = index;
        _editor.Document.SetSelections([new Selection(start, start + length)]);
        _editor.Reveal(start + length);
    }

    /// <summary>Replaces the current match when it is selected, then moves to the next one.</summary>
    [RelayCommand]
    public void ReplaceOne()
    {
        var view = _editor.View;
        var doc = _editor.Document;
        if (view.CurrentMatch < 0 || view.Found.Version != doc.Version) return;
        var match = view.Found.Matches[view.CurrentMatch];
        if (doc.Primary.Start != match.Start || doc.Primary.Length != match.Length)
        {
            Select(view.CurrentMatch);
            return;
        }

        var text = TextSearch.Replacement(doc.Text, match, CurrentQuery, Replacement);
        doc.Apply([new TextChange(match.Start, match.Length, text)], [Selection.Caret(match.Start + text.Length)]);
        _ = SearchAndSelectNextAsync();
    }

    private async Task SearchAndSelectNextAsync()
    {
        await SearchAsync(keepPosition: true);
        Step(forward: true);
    }

    [RelayCommand]
    public void ReplaceAll()
    {
        var view = _editor.View;
        if (TextSearch.ReplaceAll(_editor.Document, view.Found, CurrentQuery, Replacement)) _ = SearchAsync(keepPosition: true);
    }
}
