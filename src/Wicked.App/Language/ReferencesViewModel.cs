using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Lsp;

namespace Wicked.App.Language;

/// <summary>A place in the References panel: its file's line with the place marked.</summary>
public sealed record ReferenceRow(string Path, int Line, int Column, int EndLine, int EndColumn, string Before, string Hit, string After);

/// <summary>A file's places in the panel, under its name and folder.</summary>
public sealed partial class ReferenceFile(string path, string folder, IReadOnlyList<ReferenceRow> rows) : ObservableObject
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder { get; } = folder;

    public IReadOnlyList<ReferenceRow> Rows { get; } = rows;

    public string Count => Rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>A function in a call hierarchy, with its callers (or callees) worked out when it is opened.</summary>
public sealed partial class CallRow(CallHierarchyItem item, int depth, string where) : ObservableObject
{
    public CallHierarchyItem Item { get; } = item;

    public int Depth { get; } = depth;

    public string Name => Item.Name;

    public string Where { get; } = where;

    public double Indent => Depth * 16;

    [ObservableProperty]
    private bool _isExpanded;

    public bool Loaded { get; set; }

    public List<CallRow> Children { get; } = [];
}

/// <summary>
/// The References panel (Shift+F12's list, and Show Call Hierarchy): what the language server found, by file, each line
/// with the place marked, a click opening it. Lines are read on a worker; the list virtualises.
/// </summary>
public sealed partial class ReferencesViewModel : ObservableObject
{
    private readonly Func<string, int, int, int, int, Task> _open;
    private Func<CallHierarchyItem, Task<IReadOnlyList<(CallHierarchyItem Item, IReadOnlyList<Wicked.Core.Lsp.Range> Ranges)>>>? _calls;

    public ReferencesViewModel(Func<string, int, int, int, int, Task> open) => _open = open;

    /// <summary>"5 references to Total", "Callers of Total".</summary>
    [ObservableProperty]
    private string _title = "";

    public ObservableCollection<object> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    /// <summary>Shows places found (references, implementations): rows by file, the lines read on a worker.</summary>
    public async Task ShowAsync(string title, IReadOnlyList<Location> locations, string? root)
    {
        Title = title;
        var files = await Task.Run(() => locations
            .Select(l => (Location: l, Path: LspPaths.ToPath(l.Uri)))
            .Where(x => x.Path is not null)
            .GroupBy(x => x.Path!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                string[] lines;
                try
                {
                    lines = File.ReadAllLines(g.Key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lines = [];
                }

                var rows = g.OrderBy(x => x.Location.Range.Start.Line).ThenBy(x => x.Location.Range.Start.Character).Select(x =>
                {
                    var r = x.Location.Range;
                    var text = r.Start.Line < lines.Length ? lines[r.Start.Line] : "";
                    var from = Math.Clamp(r.Start.Character, 0, text.Length);
                    var to = r.End.Line == r.Start.Line ? Math.Clamp(r.End.Character, from, text.Length) : text.Length;
                    return new ReferenceRow(g.Key, r.Start.Line, r.Start.Character, r.End.Line, r.End.Character, text[..from].TrimStart(), text[from..to], text[to..]);
                }).ToList();
                var folder = root is not null ? System.IO.Path.GetDirectoryName(System.IO.Path.GetRelativePath(root, g.Key)) ?? "" : System.IO.Path.GetDirectoryName(g.Key) ?? "";
                return new ReferenceFile(g.Key, folder, rows);
            }).ToList());
        Rows.Clear();
        foreach (var file in files)
        {
            Rows.Add(file);
            foreach (var row in file.Rows) Rows.Add(row);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Shows a call hierarchy: the item, its callers (or what it calls) one level down, more as rows are opened.</summary>
    public async Task ShowCallsAsync(string title, CallHierarchyItem root, Func<CallHierarchyItem, Task<IReadOnlyList<(CallHierarchyItem Item, IReadOnlyList<Wicked.Core.Lsp.Range> Ranges)>>> calls)
    {
        Title = title;
        _calls = calls;
        Rows.Clear();
        var top = new CallRow(root, 0, Where(root));
        Rows.Add(top);
        await ToggleCallAsync(top);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Where(CallHierarchyItem item) => $"{System.IO.Path.GetFileName(LspPaths.ToPath(item.Uri) ?? item.Uri)}:{item.SelectionRange.Start.Line + 1}";

    [RelayCommand]
    private async Task ToggleCallAsync(CallRow row)
    {
        var at = Rows.IndexOf(row);
        if (at < 0 || _calls is null) return;
        if (row.IsExpanded)
        {
            row.IsExpanded = false;
            while (at + 1 < Rows.Count && Rows[at + 1] is CallRow child && child.Depth > row.Depth) Rows.RemoveAt(at + 1);
            return;
        }

        if (!row.Loaded)
        {
            row.Loaded = true;
            foreach (var (item, _) in await _calls(row.Item)) row.Children.Add(new CallRow(item, row.Depth + 1, Where(item)));
        }

        row.IsExpanded = true;
        for (var i = 0; i < row.Children.Count; i++) Rows.Insert(at + 1 + i, row.Children[i]);
    }

    [RelayCommand]
    private void ToggleFile(ReferenceFile file)
    {
        var at = Rows.IndexOf(file);
        if (at < 0) return;
        file.IsExpanded = !file.IsExpanded;
        if (!file.IsExpanded)
        {
            while (at + 1 < Rows.Count && Rows[at + 1] is ReferenceRow) Rows.RemoveAt(at + 1);
        }
        else
        {
            for (var i = 0; i < file.Rows.Count; i++) Rows.Insert(at + 1 + i, file.Rows[i]);
        }
    }

    [RelayCommand]
    private Task Open(object row) => row switch
    {
        ReferenceRow r => _open(r.Path, r.Line, r.Column, r.EndLine, r.EndColumn),
        CallRow c when LspPaths.ToPath(c.Item.Uri) is { } path => _open(path, c.Item.SelectionRange.Start.Line, c.Item.SelectionRange.Start.Character, c.Item.SelectionRange.End.Line, c.Item.SelectionRange.End.Character),
        _ => Task.CompletedTask,
    };
}

/// <summary>A depth in pixels as a left margin (the call tree's rows).</summary>
public sealed class IndentMargin : Avalonia.Data.Converters.IValueConverter
{
    public static readonly IndentMargin Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        new Avalonia.Thickness(value is double d ? d : 0, 0, 0, 0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}
