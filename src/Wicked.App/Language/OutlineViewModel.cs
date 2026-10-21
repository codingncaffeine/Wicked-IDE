using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Workbench;
using Wicked.Core.Lsp;
using Bench = Wicked.App.Workbench.Workbench;

namespace Wicked.App.Language;

/// <summary>A symbol in the Outline: its name, kind and depth, and where it is.</summary>
public sealed record OutlineRow(string Name, string Detail, int Kind, int Depth, Wicked.Core.Lsp.Range Range)
{
    public double Indent => Depth * 14;

    public string Icon => Kind switch
    {
        5 or 23 or 10 or 11 => "Icon.Box",
        6 or 9 or 12 => "Icon.SquareFunction",
        7 or 8 or 22 => "Icon.Wrench",
        13 or 14 => "Icon.Variable",
        2 or 3 or 4 => "Icon.Boxes",
        _ => "Icon.Braces",
    };
}

/// <summary>
/// The Outline panel: the active file's symbols from its language server, as a tree in document order, asked for again a
/// moment after edits stop, filtered by what is typed; a click goes to the symbol.
/// </summary>
public sealed partial class OutlineViewModel : ObservableObject
{
    private readonly LanguageFeatures _features;
    private readonly Bench _bench;
    private readonly DispatcherTimer _settle;
    private IReadOnlyList<OutlineRow> _all = [];
    private EditorTab? _tab;
    private CancellationTokenSource? _cts;

    public OutlineViewModel(LanguageFeatures features, Bench bench)
    {
        _features = features;
        _bench = bench;
        _settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            _ = RefreshAsync();
        };
        bench.ActiveEditorChanged += Follow;
    }

    public ObservableCollection<OutlineRow> Rows { get; } = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _empty = "Open a file to see its symbols.";

    /// <summary>Whether the panel is shown: it asks only then.</summary>
    public bool Active { get; set; }

    partial void OnFilterChanged(string value) => Show();

    private void Follow()
    {
        if (ReferenceEquals(_tab, _bench.ActiveTab)) return;
        if (_tab is not null) _tab.Document.Changed -= OnEdited;
        _tab = _bench.ActiveTab;
        if (_tab is not null) _tab.Document.Changed += OnEdited;
        if (Active) _ = RefreshAsync();
    }

    private void OnEdited(Wicked.Editor.Model.DocumentChange change)
    {
        if (!Active) return;
        _settle.Stop();
        _settle.Start();
    }

    public async Task RefreshAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        if (_bench.ActiveTab is not { ViewFactory: null } tab || _features.DocumentFor(tab) is null)
        {
            _all = [];
            Empty = _bench.ActiveTab is null ? "Open a file to see its symbols." : "No language server for this file.";
            Show();
            return;
        }

        var symbols = await _features.DocumentSymbolsAsync(tab, cts.Token);
        if (cts.IsCancellationRequested) return;
        var rows = new List<OutlineRow>();
        void Walk(IEnumerable<DocumentSymbol> list, int depth)
        {
            foreach (var s in list.OrderBy(s => s.Range.Start.Line).ThenBy(s => s.Range.Start.Character))
            {
                rows.Add(new OutlineRow(s.Name, s.Detail ?? LanguageFeatures.KindName(s.Kind), s.Kind, depth, s.SelectionRange));
                Walk(s.Children, depth + 1);
            }
        }

        Walk(symbols, 0);
        _all = rows;
        Empty = rows.Count == 0 ? "No symbols in this file." : "";
        Show();
    }

    private void Show()
    {
        Rows.Clear();
        var filter = Filter.Trim();
        foreach (var row in _all)
        {
            if (filter.Length == 0 || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) Rows.Add(filter.Length == 0 ? row : row with { Depth = 0 });
        }
    }

    [RelayCommand]
    private Task Open(OutlineRow row) =>
        _bench.ActiveTab?.Path is { } path && _features.OpenLocation is { } open ? open(path, row.Range) : Task.CompletedTask;
}
