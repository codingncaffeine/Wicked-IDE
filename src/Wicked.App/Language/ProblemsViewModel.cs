using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Lsp;

namespace Wicked.App.Language;

/// <summary>One problem as the Problems panel lists it.</summary>
public sealed record ProblemItem(string Path, DiagnosticSeverity Severity, string Message, string? Source, string? Code, int Line, int Column)
{
    /// <summary>"[Ln 8, Col 12]", as VS Code writes it after the message.</summary>
    public string Where => $"[Ln {Line + 1}, Col {Column + 1}]";

    public string Origin => Source is null ? "" : Code is null ? Source : $"{Source}({Code})";

    public bool IsError => Severity == DiagnosticSeverity.Error;

    public bool IsWarning => Severity == DiagnosticSeverity.Warning;

    public bool IsInfo => Severity is DiagnosticSeverity.Information or DiagnosticSeverity.Hint;
}

/// <summary>A file's problems, worst first, under the file's name and folder.</summary>
public sealed partial class FileProblems(string path, IReadOnlyList<ProblemItem> items) : ObservableObject
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileName(Path);

    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    public IReadOnlyList<ProblemItem> Items { get; } = items;

    public string Count => Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [ObservableProperty]
    private bool _isExpanded = true;
}

/// <summary>
/// Every file's problems from every server, by file (VS Code's Problems panel): counts for the status bar and the panel's
/// tab, a filter, and a click that opens the file at the problem.
/// </summary>
public sealed partial class ProblemsViewModel : ObservableObject
{
    private readonly LanguageFeatures _owner;
    private readonly Dictionary<string, IReadOnlyList<ProblemItem>> _byFile = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<ProblemItem>> _byOwner = new(StringComparer.Ordinal);

    public ProblemsViewModel(LanguageFeatures owner) => _owner = owner;

    public ObservableCollection<FileProblems> Files { get; } = [];

    /// <summary>What the list shows, flat so it virtualises: each file's header, then its problems unless collapsed.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    [ObservableProperty]
    private int _errors;

    [ObservableProperty]
    private int _warnings;

    [ObservableProperty]
    private int _infos;

    /// <summary>Text the messages, sources and file names are filtered by.</summary>
    [ObservableProperty]
    private string _filter = "";

    /// <summary>A problem was picked: the shell opens its file there.</summary>
    public event Action<ProblemItem>? Opened;

    public int Total => Errors + Warnings + Infos;

    public bool IsEmpty => Files.Count == 0;

    partial void OnFilterChanged(string value) => Rebuild();

    /// <summary>A document's problems as a server last sent them (none clears the file). UI thread.</summary>
    public void Set(string uri, DocumentDiagnostics? found)
    {
        var path = LspPaths.ToPath(uri) ?? uri;
        if (found is null || found.Items.Count == 0) _byFile.Remove(path);
        else
        {
            _byFile[path] = [.. found.Items
                .Select(d => new ProblemItem(path, d.Severity ?? DiagnosticSeverity.Error, d.Message, d.Source, CodeOf(d), d.Range.Start.Line, d.Range.Start.Character))
                .OrderBy(p => p.Severity).ThenBy(p => p.Line).ThenBy(p => p.Column)];
        }

        Rebuild();
    }

    /// <summary>
    /// A task's problems (what its problem matchers found), by the task's label: they replace that task's earlier ones, so
    /// a new run starts clean; none clears them. They show beside the language servers' problems. UI thread.
    /// </summary>
    public void SetOwner(string owner, IReadOnlyList<ProblemItem> items)
    {
        if (items.Count == 0) _byOwner.Remove(owner);
        else _byOwner[owner] = items;
        Rebuild();
    }

    private static string? CodeOf(Diagnostic d) => d.Code is { } code
        ? code.ValueKind == System.Text.Json.JsonValueKind.String ? code.GetString() : code.ValueKind == System.Text.Json.JsonValueKind.Number ? code.GetRawText() : null
        : null;

    private void Rebuild()
    {
        var byFile = new Dictionary<string, IReadOnlyList<ProblemItem>>(_byFile, StringComparer.Ordinal);
        foreach (var group in _byOwner.Values.SelectMany(x => x).GroupBy(p => p.Path, StringComparer.Ordinal))
        {
            byFile[group.Key] = [.. (byFile.TryGetValue(group.Key, out var known) ? known : []).Concat(group).OrderBy(p => p.Severity).ThenBy(p => p.Line).ThenBy(p => p.Column)];
        }

        var all = byFile.Values.SelectMany(x => x).ToList();
        Errors = all.Count(p => p.IsError);
        Warnings = all.Count(p => p.IsWarning);
        Infos = all.Count(p => p.IsInfo);
        var filter = Filter.Trim();
        Files.Clear();
        foreach (var (path, items) in byFile.OrderBy(kv => kv.Value.Min(p => p.Severity)).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var shown = filter.Length == 0 ? items : [.. items.Where(p =>
                p.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) || (p.Origin.Contains(filter, StringComparison.OrdinalIgnoreCase)) || path.Contains(filter, StringComparison.OrdinalIgnoreCase))];
            if (shown.Count > 0) Files.Add(new FileProblems(path, shown));
        }

        FillRows();
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(IsEmpty));
        CountsChanged?.Invoke();
    }

    /// <summary>The counts changed (the panel's tab badge and the status bar follow).</summary>
    public event Action? CountsChanged;

    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);

    private void FillRows()
    {
        Rows.Clear();
        foreach (var file in Files)
        {
            file.IsExpanded = !_collapsed.Contains(file.Path);
            Rows.Add(file);
            if (!file.IsExpanded) continue;
            foreach (var item in file.Items) Rows.Add(item);
        }
    }

    /// <summary>A file's header clicked: its problems folded away or shown again.</summary>
    [RelayCommand]
    private void Toggle(FileProblems file)
    {
        if (!_collapsed.Remove(file.Path)) _collapsed.Add(file.Path);
        FillRows();
    }

    [RelayCommand]
    private void Open(ProblemItem item) => Opened?.Invoke(item);

    /// <summary>F8 / Shift+F8: the next or previous problem after a place, across files in the panel's order.</summary>
    public ProblemItem? Next(string? path, int line, int column, int direction)
    {
        var list = Files.SelectMany(f => f.Items).ToList();
        if (list.Count == 0) return null;
        var at = list.FindIndex(p => string.Equals(p.Path, path, StringComparison.Ordinal) && (p.Line > line || (p.Line == line && p.Column > column)));
        if (direction > 0) return at >= 0 ? list[at] : list[0];
        var before = list.FindLastIndex(p => string.Equals(p.Path, path, StringComparison.Ordinal) && (p.Line < line || (p.Line == line && p.Column < column)));
        return before >= 0 ? list[before] : list[^1];
    }
}
