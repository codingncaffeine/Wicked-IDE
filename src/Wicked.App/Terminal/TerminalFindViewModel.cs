using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Terminal;

namespace Wicked.App.Terminal;

/// <summary>The terminal's find widget (Ctrl+Shift+F): searches the whole buffer, scrollback included, on a worker.</summary>
public sealed partial class TerminalFindViewModel : ObservableObject
{
    private readonly TerminalView _view;
    private CancellationTokenSource? _search;
    private List<TerminalMatch> _matches = [];
    private int _current = -1;

    public TerminalFindViewModel(TerminalView view)
    {
        _view = view;
        view.BufferRebuilt += () =>
        {
            // Positions from before the resize are gone: look again.
            if (IsOpen) Search();
        };
    }

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = "";

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

    /// <summary>Raised when the widget should take the keyboard.</summary>
    public event Action? FocusRequested;

    public int MatchCount => _matches.Count;

    public int CurrentIndex => _current;

    public void Open(string? seed = null)
    {
        IsOpen = true;
        if (!string.IsNullOrEmpty(seed)) Query = seed;
        FocusRequested?.Invoke();
        if (Query.Length > 0) Search();
    }

    partial void OnQueryChanged(string value) => Search();

    partial void OnMatchCaseChanged(bool value) => Search();

    partial void OnWholeWordChanged(bool value) => Search();

    partial void OnRegexChanged(bool value) => Search();

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        _search?.Cancel();
        _view.SetMatches([], -1);
        _view.Focus();
    }

    [RelayCommand]
    public void Next() => Step(1);

    [RelayCommand]
    public void Previous() => Step(-1);

    private void Step(int direction)
    {
        if (_matches.Count == 0)
        {
            Search();
            return;
        }

        _current = (_current + direction + _matches.Count) % _matches.Count;
        Show();
    }

    private void Show()
    {
        _view.SetMatches(_matches, _current);
        if (_current >= 0) _view.RevealMatch(_matches[_current]);
        Status = _matches.Count == 0 ? (Query.Length == 0 ? "" : "No results") : $"{_current + 1} of {_matches.Count}{(_matches.Count >= 10_000 ? "+" : "")}";
    }

    /// <summary>Looks for the query again; the result comes back to the UI thread.</summary>
    public void Search()
    {
        _search?.Cancel();
        if (_view.Session is not { } session || Query.Length == 0)
        {
            _matches = [];
            _current = -1;
            HasError = false;
            _view.SetMatches([], -1);
            Status = "";
            return;
        }

        var cts = _search = new CancellationTokenSource();
        var query = Query;
        var options = new SearchOptions(MatchCase, WholeWord, Regex);
        var frame = _view.Frame;
        _ = SearchAsync(session, query, options, frame, cts);
    }

    private async Task SearchAsync(TerminalSession session, string query, SearchOptions options, TerminalFrame? frame, CancellationTokenSource cts)
    {
        try
        {
            // A short pause, so typing a word is one search, not one per letter.
            await Task.Delay(80, cts.Token).ConfigureAwait(false);
            var (epoch, matches) = await session.SearchAsync(query, options, cts.Token).ConfigureAwait(false);
            if (cts.IsCancellationRequested) return;
            var invalid = options.Regex && matches.Count == 0 && !IsValidRegex(query);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested || _view.Frame is { } now && now.Epoch != epoch && frame is not null && now.Epoch != frame.Epoch) return;
                _matches = matches;
                HasError = invalid;
                // Start from the first match on or below what is on screen.
                var top = _view.Frame?.TopLine ?? 0;
                var from = matches.FindIndex(m => m.Start.Line >= top);
                _current = matches.Count == 0 ? -1 : from < 0 ? matches.Count - 1 : from;
                Show();
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
