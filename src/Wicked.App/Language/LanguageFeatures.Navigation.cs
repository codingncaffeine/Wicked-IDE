using System.Runtime.CompilerServices;
using Wicked.App.Editor;
using Wicked.App.ViewModels;
using Wicked.App.Workbench;
using Wicked.Core;
using Wicked.Core.Files;
using Wicked.Core.Lsp;

namespace Wicked.App.Language;

public sealed partial class LanguageFeatures
{
    private readonly ConditionalWeakTable<CodeEditor, EditorLanguageUi> _ui = new();

    /// <summary>Opens a place in a file (the shell sets it: a tab opened or brought forward, the range selected).</summary>
    public Func<string, Wicked.Core.Lsp.Range, Task>? OpenLocation { get; set; }

    /// <summary>Shows a list to choose from in the palette (the shell sets it).</summary>
    public Action<string, IReadOnlyList<QuickItem>>? ShowList { get; set; }

    /// <summary>The language UI inside an editor, once its document has a server.</summary>
    public EditorLanguageUi? UiFor(CodeEditor? editor) => editor is not null && _ui.TryGetValue(editor, out var ui) ? ui : null;

    private void AttachUi(EditorTab tab, CodeEditor editor)
    {
        if (_ui.TryGetValue(editor, out _)) return;
        editor.ShowGlyphMargin = tab.Path is not null;
        var ui = new EditorLanguageUi(editor, this, () => GoToAsync(tab, "definition"));
        _ui.Add(editor, ui);
        var previous = editor.GlyphMarginClicked;
        editor.GlyphMarginClicked = line =>
        {
            var key = editor.Document.Text.LineStart(Math.Clamp(line, 0, editor.Document.Text.LineCount - 1));
            if (editor.Decorations[EditorLanguageUi.LightbulbLayer].Gutter.Any(g => g.LineStart == key)) _ = ui.ShowActionsAsync();
            else previous?.Invoke(line);
        };
    }

    private Task<Wicked.Editor.Syntax.SemanticPalette>? _palette;
    private (TextMateSharp.Grammars.ThemeName Theme, string? Custom) _paletteFor;

    /// <summary>The code colours the editors use: the setting's own palette, or the window theme's (and its token colours).</summary>
    private static (TextMateSharp.Grammars.ThemeName Theme, string? Custom) CodeColours() =>
        EditorServices.Ready.IsCompletedSuccessfully ? (EditorServices.Ready.Result.Syntax.Theme, EditorServices.Ready.Result.Syntax.CustomTheme) : (TextMateSharp.Grammars.ThemeName.DarkPlus, null);

    /// <summary>The code colours changed since the semantic colours were worked out (a palette already in use only).</summary>
    private bool PaletteIsStale => _palette is not null && CodeColours() != _paletteFor;

    /// <summary>The semantic token colours of the code theme in force, read once per theme on a worker.</summary>
    internal Task<Wicked.Editor.Syntax.SemanticPalette> SemanticPaletteAsync()
    {
        var wanted = CodeColours();
        if (_palette is null || _paletteFor != wanted)
        {
            _paletteFor = wanted;
            _palette = Task.Run(() => Wicked.Editor.Syntax.SemanticPalette.For(wanted.Theme, wanted.Custom));
        }

        return _palette;
    }

    /// <summary>The text of a file as its editor has it, when it is open (UI thread).</summary>
    internal Wicked.Editor.Text.Rope? OpenDocumentText(string path) => _bench.AllTabs.FirstOrDefault(t => t.Path == path)?.Document.Text;

    internal EditorTab? TabOf(CodeEditor editor) => _bench.AllTabs.FirstOrDefault(t => ReferenceEquals(t.Editor, editor));

    internal FormattingOptions FormattingOf(EditorTab tab) => FormattingFor(tab);

    /// <summary>A passing word in the footer ("No definition found").</summary>
    public void Tell(string message) => _notices.Post(new Notice("lsp:tell", message, NoticeKind.Info));

    // ── Go to ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// F12 and its kin (<c>definition</c>, <c>declaration</c>, <c>typeDefinition</c>, <c>implementation</c>): one place opens
    /// at once, several are listed to choose from.
    /// </summary>
    public async Task GoToAsync(EditorTab? tab, string kind)
    {
        if (tab is null || DocumentFor(tab) is not { } doc) return;
        var caret = tab.Document.Primary.Active;
        IReadOnlyList<Location>? found;
        try
        {
            found = await doc.RequestAsync((c, text, _) => c.Supports(kind + "Provider") ? c.GoToAsync(kind, doc.Uri, LspText.PositionOf(text, caret)) : Task.FromResult<IReadOnlyList<Location>>([]));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Tell($"Go to {Words(kind)} failed: {ex.Message}");
            return;
        }

        if (found is not { Count: > 0 })
        {
            Tell($"No {Words(kind)} found");
            return;
        }

        if (found.Count == 1)
        {
            await OpenAsync(found[0]);
            return;
        }

        await ShowPlacesAsync($"{found.Count} {Words(kind)}s", found);
    }

    /// <summary>The References panel: references, implementations and call hierarchies show there.</summary>
    public ReferencesViewModel References { get; }

    /// <summary>The panel has something new to show (the shell brings it forward).</summary>
    public event Action? ReferencesShown;

    private async Task ShowPlacesAsync(string title, IReadOnlyList<Location> places)
    {
        await References.ShowAsync(title, places, _bench.Root);
        ReferencesShown?.Invoke();
    }

    /// <summary>Show Call Hierarchy (Shift+Alt+H): who calls the function at the caret, or (outgoing) what it calls.</summary>
    public async Task CallHierarchyAsync(EditorTab? tab, bool incoming = true)
    {
        if (tab is null || DocumentFor(tab) is not { } doc) return;
        var caret = tab.Document.Primary.Active;
        IReadOnlyList<CallHierarchyItem>? items;
        try
        {
            items = await doc.RequestAsync((c, text, _) => c.Supports("callHierarchyProvider") ? c.PrepareCallHierarchyAsync(doc.Uri, LspText.PositionOf(text, caret)) : Task.FromResult<IReadOnlyList<CallHierarchyItem>>([]));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Tell($"Call hierarchy failed: {ex.Message}");
            return;
        }

        if (items is not { Count: > 0 } || doc.Client is not { } client)
        {
            Tell("No call hierarchy here");
            return;
        }

        await References.ShowCallsAsync(incoming ? $"Callers of {items[0].Name}" : $"Calls from {items[0].Name}", items[0], item => client.CallsAsync(item, incoming));
        ReferencesShown?.Invoke();
    }

    private static string Words(string kind) => kind switch
    {
        "typeDefinition" => "type definition",
        _ => kind,
    };

    private Task OpenAsync(Location location) =>
        LspPaths.ToPath(location.Uri) is { } path && OpenLocation is { } open ? open(path, location.Range) : Task.CompletedTask;

    /// <summary>Shift+F12: every reference, to choose from (the References panel comes with Phase 12).</summary>
    public async Task ReferencesAsync(EditorTab? tab)
    {
        if (tab is null || DocumentFor(tab) is not { } doc) return;
        var caret = tab.Document.Primary.Active;
        IReadOnlyList<Location>? found;
        try
        {
            found = await doc.RequestAsync((c, text, _) => c.ReferencesAsync(doc.Uri, LspText.PositionOf(text, caret), includeDeclaration: true));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            Tell($"Find references failed: {ex.Message}");
            return;
        }

        if (found is not { Count: > 0 })
        {
            Tell("No references found");
            return;
        }

        LastReferences = found;
        ReferencesFound?.Invoke(found);
        var (s, e) = Wicked.Editor.Model.TextNavigation.WordAt(tab.Document.Text, caret);
        await ShowPlacesAsync($"{found.Count} reference{(found.Count == 1 ? "" : "s")} to {tab.Document.Text.Substring(s, e - s)}", found);
    }

    /// <summary>The last references found, for the References panel.</summary>
    public IReadOnlyList<Location> LastReferences { get; private set; } = [];

    public event Action<IReadOnlyList<Location>>? ReferencesFound;

    /// <summary>Places listed with the line each sits on (read on a worker), the current file's first.</summary>
    private async Task ListLocationsAsync(string title, IReadOnlyList<Location> locations)
    {
        var rows = await Task.Run(() => locations.Take(500).Select(l =>
        {
            var path = LspPaths.ToPath(l.Uri) ?? l.Uri;
            var line = "";
            try
            {
                line = File.ReadLines(path).Skip(l.Range.Start.Line).FirstOrDefault()?.Trim() ?? "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            return (Location: l, Path: path, Line: line);
        }).ToList());
        var items = rows.Select(r => new QuickItem(
            $"{Path.GetFileName(r.Path)}:{r.Location.Range.Start.Line + 1}  {r.Line}",
            RelativeToRoot(r.Path),
            "",
            () => OpenAsync(r.Location))).ToList();
        ShowList?.Invoke(title, items);
    }

    private string RelativeToRoot(string path) => _bench.Root is { } root && IsUnder(path, root) ? Path.GetRelativePath(root, Path.GetDirectoryName(path)!) : Path.GetDirectoryName(path) ?? "";

    // ── Symbols ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>The symbol kinds' names, as Go to Symbol groups them.</summary>
    internal static string KindName(int kind) => kind switch
    {
        1 => "file", 2 => "module", 3 => "namespace", 4 => "package", 5 => "class", 6 => "method", 7 => "property", 8 => "field",
        9 => "constructor", 10 => "enum", 11 => "interface", 12 => "function", 13 => "variable", 14 => "constant", 15 => "string",
        16 => "number", 17 => "boolean", 18 => "array", 19 => "object", 20 => "key", 21 => "null", 22 => "enum member",
        23 => "struct", 24 => "event", 25 => "operator", 26 => "type parameter", _ => "symbol",
    };

    /// <summary>The active file's symbols as a tree, for the Outline and the breadcrumbs.</summary>
    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(EditorTab tab, CancellationToken ct = default)
    {
        if (DocumentFor(tab) is not { } doc) return [];
        try
        {
            return await doc.RequestAsync((c, _, _) => c.Supports("documentSymbolProvider") ? c.DocumentSymbolsAsync(doc.Uri, ct) : Task.FromResult<IReadOnlyList<DocumentSymbol>>([]), ct) ?? [];
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return [];
        }
    }

    /// <summary>Ctrl+Shift+O ("@" in the palette): the file's symbols, filtered by what is typed, nested names indented.</summary>
    public async Task<List<QuickItem>> SymbolItemsAsync(EditorTab? tab, string query, CancellationToken ct)
    {
        if (tab is null) return [];
        var symbols = await DocumentSymbolsAsync(tab, ct);
        var flat = new List<(DocumentSymbol Symbol, int Depth)>();
        void Walk(IEnumerable<DocumentSymbol> list, int depth)
        {
            foreach (var s in list)
            {
                flat.Add((s, depth));
                Walk(s.Children, depth + 1);
            }
        }

        Walk(symbols, 0);
        return [.. flat
            .Select(f => (f.Symbol, f.Depth, Score: query.Length == 0 ? 0 : FuzzyMatch.Score(query, f.Symbol.Name)))
            .Where(f => f.Score >= 0)
            .OrderByDescending(f => query.Length == 0 ? 0 : f.Score)
            .Select(f => new QuickItem(new string(' ', query.Length == 0 ? f.Depth * 2 : 0) + f.Symbol.Name, KindName(f.Symbol.Kind) + (f.Symbol.Detail is { Length: > 0 } d ? "  " + d : ""), "",
                () => tab.Path is { } path && OpenLocation is { } open ? open(path, f.Symbol.SelectionRange) : Task.CompletedTask))];
    }

    /// <summary>Ctrl+T ("#" in the palette): symbols across the workspace, from every running server.</summary>
    public async Task<List<QuickItem>> WorkspaceSymbolItemsAsync(string query, CancellationToken ct)
    {
        var results = new List<WorkspaceSymbol>();
        var clients = _documents.Values.Select(d => d.Client).Where(c => c is { IsRunning: true } && c.Supports("workspaceSymbolProvider")).Distinct().ToList();
        foreach (var client in clients)
        {
            try
            {
                results.AddRange(await client!.WorkspaceSymbolsAsync(query, ct));
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
            }
        }

        return [.. results
            .Select(s => (Symbol: s, Score: query.Length == 0 ? 0 : FuzzyMatch.Score(query, s.Name)))
            .Where(p => p.Score >= 0)
            .OrderByDescending(p => p.Score)
            .Take(200)
            .Select(p => new QuickItem(p.Symbol.Name, $"{KindName(p.Symbol.Kind)}  {p.Symbol.ContainerName}  {Path.GetFileName(LspPaths.ToPath(p.Symbol.Location.Uri) ?? "")}", "",
                () => OpenAsync(p.Symbol.Location)))];
    }

    // ── Problems, one by one ─────────────────────────────────────────────────────────────────────

    /// <summary>F8 / Shift+F8: the next or previous problem, in this file and then the others, its card shown.</summary>
    public async Task NextProblemAsync(EditorTab? tab, int direction)
    {
        var (line, column) = tab is null ? (-1, -1) : tab.Document.Text.Position(tab.Document.Primary.Active);
        if (Problems.Next(tab?.Path, line, column, direction) is not { } p || OpenLocation is not { } open) return;
        var at = new Position(p.Line, p.Column);
        await open(p.Path, new Wicked.Core.Lsp.Range(at, at));
        var editor = _bench.ActiveTab?.Editor;
        if (UiFor(editor) is { } ui) await ui.ShowHoverAtCaretAsync();
    }
}
