using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.Editor;
using Wicked.App.Shell;
using Wicked.App.Workbench;
using Wicked.Core;
using Wicked.Core.Lsp;
using Wicked.Core.Settings;
using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Text;
using Bench = Wicked.App.Workbench.Workbench;

namespace Wicked.App.Language;

/// <summary>
/// The language servers as the window uses them (Phase 9): a <see cref="LanguageServerHost"/> per folder, every open file
/// of a served language kept in step with its server, the diagnostics drawn in the editors and listed in Problems, and
/// the requests the editor features make (completion, hover, go to, rename, format, …). Lives on the UI thread; every
/// conversion of a server's answer runs on a worker and posts its result.
/// </summary>
public sealed partial class LanguageFeatures : ObservableObject, IAsyncDisposable
{
    public const string DiagnosticsLayer = "diagnostics";

    private readonly Bench _bench;
    private readonly WickedPaths _paths;
    private readonly OutputLog _output;
    private readonly NoticeBoard _notices;
    private readonly Action<Action> _post;
    private readonly Dictionary<string, LanguageServerHost> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<Document, LanguageDocument> _documents = [];
    private WickedSettings _settings = new();
    private readonly HashSet<string> _toldMissing = [];

    public LanguageFeatures(Bench bench, WickedPaths paths, OutputLog output, NoticeBoard notices, Action<Action>? post = null)
    {
        _bench = bench;
        _paths = paths;
        _output = output;
        _notices = notices;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        Problems = new ProblemsViewModel(this);
        References = new ReferencesViewModel((path, line, column, endLine, endColumn) =>
            OpenLocation?.Invoke(path, new Wicked.Core.Lsp.Range(new Position(line, column), new Position(endLine, endColumn))) ?? Task.CompletedTask);
        bench.TabOpened += OnTabOpened;
        bench.TabClosed += OnTabClosed;
        bench.Saved += tab =>
        {
            if (_documents.TryGetValue(tab.Document, out var doc)) doc.Saved();
        };
        bench.BeforeSave = BeforeSaveAsync;
    }

    public ProblemsViewModel Problems { get; }

    /// <summary>A problem's "Fix" or "Explain" in its hover card, handed to the assistant (the shell sets it).</summary>
    public Action<Wicked.App.Editor.CodeEditor, Diagnostic, string>? ProblemAction { get; set; }

    /// <summary>Where Wicked installs servers of its own (the installer writes here; the locator looks here first).</summary>
    public string ServersFolder => Path.Combine(_paths.Data, "servers");

    /// <summary>The servers' states, newest change last, for the status bar.</summary>
    [ObservableProperty]
    private string _statusText = "";

    public void ApplySettings(WickedSettings settings)
    {
        var serversChanged = !Equals(settings.Servers, _settings.Servers) || settings.LanguageServers != _settings.LanguageServers;
        // Called after the editor services take the settings' code colours (and again by a theme preview).
        var recolour = PaletteIsStale || settings.SemanticHighlighting != _settings.SemanticHighlighting;
        _settings = settings;
        if (recolour)
        {
            foreach (var tab in _bench.AllTabs) _ = UiFor(tab.Editor)?.RefreshSemanticAsync();
        }

        if (!serversChanged) return;
        foreach (var host in _hosts.Values) host.Disabled = DisabledServers();
    }

    public WickedSettings Settings => _settings;

    private HashSet<string> DisabledServers() =>
        _settings.Servers is { } servers ? [.. servers.Where(kv => !kv.Value.Enabled).Select(kv => kv.Key)] : [];

    // ── Documents ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The language-server side of a tab's document, when its language has a server.</summary>
    public LanguageDocument? DocumentFor(Document document) => _documents.GetValueOrDefault(document);

    public LanguageDocument? DocumentFor(EditorTab tab) => DocumentFor(tab.Document);

    private void OnTabOpened(EditorTab tab)
    {
        tab.EditorCreated += editor => OnEditorCreated(tab, editor);
        if (tab.Editor is { } made) OnEditorCreated(tab, made);
        if (!_settings.LanguageServers || tab.Path is not { } path || tab.Language is not { } language) return;
        if (_documents.TryGetValue(tab.Document, out var known))
        {
            known.Tabs++;
            return;
        }

        if (!LanguageServerCatalog.For(language.Id).Any()) return;
        var host = HostFor(path);
        var doc = new LanguageDocument(tab.Document, path, language.Id) { Tabs = 1 };
        _documents[tab.Document] = doc;
        tab.Document.Changed += doc.Changed;
        _ = Task.Run(async () =>
        {
            var client = await host.ClientForAsync(language.Id).ConfigureAwait(false);
            _post(() =>
            {
                if (_documents.GetValueOrDefault(tab.Document) != doc) return;
                doc.Bind(client);
                if (client is null) return;
                ShowDiagnostics(doc);
                foreach (var editor in EditorsOf(doc.Document))
                {
                    _ = UiFor(editor)?.RefreshSemanticAsync();
                    _ = UiFor(editor)?.RefreshInlayHintsAsync();
                }
            });
        });
    }

    private void OnTabClosed(EditorTab tab)
    {
        if (!_documents.TryGetValue(tab.Document, out var doc) || --doc.Tabs > 0) return;
        _documents.Remove(tab.Document);
        tab.Document.Changed -= doc.Changed;
        _ = doc.DisposeAsync().AsTask();
    }

    /// <summary>A tab's editor made after its document opened (a background tab shown): it gets the marks already known.</summary>
    private void OnEditorCreated(EditorTab tab, CodeEditor editor)
    {
        editor.LanguageService = this;
        AttachUi(tab, editor);
        if (!_documents.TryGetValue(tab.Document, out var doc)) return;
        ShowDiagnostics(doc);
        if (doc.Client is null) return;
        _ = UiFor(editor)?.RefreshSemanticAsync();
        _ = UiFor(editor)?.RefreshInlayHintsAsync();
    }

    /// <summary>
    /// The texts word-based suggestions draw from, by editor.wordBasedSuggestions: this document, the open documents of its
    /// language (matchingDocuments, the default), or every open document.
    /// </summary>
    internal IReadOnlyList<Wicked.Editor.Text.Rope> WordSources(Document document, string mode)
    {
        if (mode == "currentDocument") return [document.Text];
        var language = _bench.AllTabs.FirstOrDefault(t => ReferenceEquals(t.Document, document))?.Language?.Id;
        var others = _bench.AllTabs.Where(t => !ReferenceEquals(t.Document, document) && t.Path is not null && (mode == "allDocuments" || t.Language?.Id == language))
            .Select(t => t.Document).Distinct().Select(d => d.Text);
        return [document.Text, .. others];
    }

    /// <summary>The editors that show a document (one per tab, a split shows it twice).</summary>
    internal IEnumerable<CodeEditor> EditorsOf(Document document) =>
        _bench.AllTabs.Where(t => ReferenceEquals(t.Document, document) && t.Editor is not null).Select(t => t.Editor!);

    // ── Hosts ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The host for a file: the open folder's when the file is inside it, else one for the file's own folder.</summary>
    private LanguageServerHost HostFor(string path)
    {
        var root = _bench.Root is { } r && IsUnder(path, r) ? r : Path.GetDirectoryName(path)!;
        if (_hosts.TryGetValue(root, out var host)) return host;
        var commands = _settings.Servers?.Where(kv => kv.Value.Command is { Count: > 0 })
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Command!);
        host = new LanguageServerHost(root, new ServerLocator(ServersFolder, userCommands: commands), Path.Combine(_paths.Logs, "lsp")) { Disabled = DisabledServers() };
        host.DiagnosticsChanged += uri => OnDiagnosticsChanged(host, uri);
        host.StatusChanged += status => _post(() => OnStatus(status));
        host.Output += (id, line) => _output.Write(ChannelName(id), line);
        host.Restarted += (id, client) => _post(() => Rebind(host, id, client));
        host.ApplyEdit = edit =>
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _post(() => _ = ApplyWorkspaceEditAsync(edit).ContinueWith(t => done.TrySetResult(t.IsCompletedSuccessfully && t.Result), TaskScheduler.Default));
            return done.Task;
        };
        _hosts[root] = host;
        return host;
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(root);
        if (!full.EndsWith(Path.DirectorySeparatorChar)) full += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(full, StringComparison.Ordinal);
    }

    private static string ChannelName(string serverId) => LanguageServerCatalog.ById(serverId)?.Name is { } name ? $"{name} (language server)" : serverId;

    /// <summary>A server that came back: every document it served opens in it again.</summary>
    private void Rebind(LanguageServerHost host, string serverId, LanguageClient client)
    {
        foreach (var doc in _documents.Values)
        {
            if (host.Pick(doc.LanguageId)?.Spec.Id == serverId && HostFor(doc.Path) == host) doc.Bind(client);
        }
    }

    private void OnStatus(ServerStatus status)
    {
        StatusText = status.State switch
        {
            ServerState.Running => status.Name,
            ServerState.Starting => $"{status.Name}: starting…",
            ServerState.Failed => $"{status.Name}: stopped",
            ServerState.Missing => "",
            _ => "",
        };
        var key = $"lsp:{status.ServerId}";
        switch (status.State)
        {
            case ServerState.Missing:
                // Said once per session, in passing: a file of a language without its server still edits fine.
                if (_toldMissing.Add(status.ServerId)) _notices.Post(new Notice(key, status.Detail ?? $"{status.Name} is not installed", NoticeKind.Info, $"lsp/install/{status.ServerId}"));
                StatusText = $"{status.Name}: not installed";
                break;
            case ServerState.Failed:
                _notices.Post(new Notice(key, status.Detail ?? $"{status.Name} stopped", NoticeKind.Error, $"output/{ChannelName(status.ServerId)}"));
                break;
            default:
                _notices.Clear(key);
                break;
        }
    }

    /// <summary>Restart Language Server: the active file's server stopped and started fresh, its documents opened again.</summary>
    public async Task RestartAsync(EditorTab? tab)
    {
        if (tab?.Path is not { } path || tab.Language is not { } language) return;
        var host = HostFor(path);
        if (host.Pick(language.Id) is not { } pick) return;
        await host.RestartAsync(pick.Spec.Id);
        var client = await Task.Run(() => host.ClientForAsync(language.Id));
        Rebind(host, pick.Spec.Id, client!);
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────────────────────────

    private void OnDiagnosticsChanged(LanguageServerHost host, string uri)
    {
        var found = host.DiagnosticsFor(uri);
        _post(() =>
        {
            Problems.Set(uri, found);
            if (LspPaths.ToPath(uri) is not { } path) return;
            var doc = _documents.Values.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(path), StringComparison.Ordinal));
            if (doc is not null) ShowDiagnostics(doc, found);
        });
    }

    /// <summary>A document's problems as marks in its editors: worked out on a worker against the text they are about.</summary>
    private void ShowDiagnostics(LanguageDocument doc, DocumentDiagnostics? found = null)
    {
        found ??= _hosts.Values.Select(h => h.DiagnosticsFor(doc.Uri)).FirstOrDefault(d => d is not null);
        var items = found?.Items ?? [];
        // The version the server worked on; without one, the newest text it has.
        var (version, text) = found?.Version is int v && doc.TextAt(v) is { } at ? (v, at) : doc.Latest;
        _ = Task.Run(() => MarksFor(text, items)).ContinueWith(t => _post(() =>
        {
            if (!t.IsCompletedSuccessfully) return;
            foreach (var editor in EditorsOf(doc.Document)) editor.SetDecorations(DiagnosticsLayer, t.Result, version);
        }), TaskScheduler.Default);
    }

    internal static DecorationSet MarksFor(Rope text, IReadOnlyList<Diagnostic> items)
    {
        var ranges = new List<RangeDecoration>(items.Count);
        foreach (var d in items)
        {
            var (start, end) = LspText.OffsetsOf(text, d.Range);
            var style = d.Severity switch
            {
                DiagnosticSeverity.Warning => RangeStyle.Warning,
                DiagnosticSeverity.Information => RangeStyle.Info,
                DiagnosticSeverity.Hint => RangeStyle.Hint,
                _ => RangeStyle.Error,
            };
            ranges.Add(new RangeDecoration(start, end, style));
            if (d.Tags is { } tags)
            {
                if (tags.Contains(1)) ranges.Add(new RangeDecoration(start, end, RangeStyle.Unnecessary));
                if (tags.Contains(2)) ranges.Add(new RangeDecoration(start, end, RangeStyle.Deprecated));
            }
        }

        return DecorationSet.OfRanges(ranges);
    }

    /// <summary>The problems at an offset of a document, for the hover and the quick fixes (worst first).</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsAt(LanguageDocument doc, int offset)
    {
        var found = _hosts.Values.Select(h => h.DiagnosticsFor(doc.Uri)).FirstOrDefault(d => d is not null);
        if (found is null) return [];
        var text = found.Version is int v && doc.TextAt(v) is { } at ? at : doc.Document.Text;
        return [.. found.Items.Where(d =>
        {
            var (s, e) = LspText.OffsetsOf(text, d.Range);
            return offset >= s && offset <= Math.Max(e, s + 1);
        }).OrderBy(d => d.Severity ?? DiagnosticSeverity.Error)];
    }

    /// <summary>All the problems of a document, for code actions over a range.</summary>
    public IReadOnlyList<Diagnostic> DiagnosticsOf(LanguageDocument doc) =>
        _hosts.Values.Select(h => h.DiagnosticsFor(doc.Uri)).FirstOrDefault(d => d is not null)?.Items ?? [];

    /// <summary>Installs a server into Wicked's folder, its progress in the Output panel, then opens the waiting files in it.</summary>
    public async Task InstallAsync(LanguageServerSpec spec)
    {
        var channel = ChannelName(spec.Id);
        var key = $"lsp:install:{spec.Id}";
        _notices.Post(new Notice(key, $"Installing {spec.Name}…", NoticeKind.Progress, $"output/{channel}"));
        var installer = new LanguageServerInstaller(Wicked.Core.Http.WickedHttp.Shared, ServersFolder);
        try
        {
            await Task.Run(() => installer.InstallAsync(spec, new Progress<string>(line => _output.Write(channel, line))));
            _notices.Post(new Notice(key, $"{spec.Name} installed", NoticeKind.Success));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            _output.Write(channel, ex.Message);
            _notices.Post(new Notice(key, $"{spec.Name} was not installed: {ex.Message}", NoticeKind.Error, $"output/{channel}"));
            return;
        }

        _toldMissing.Remove(spec.Id);
        // The documents that were waiting for it: closed and opened again so they bind to the new server.
        foreach (var tab in _bench.AllTabs.Where(t => t.Language is { } l && spec.Languages.Contains(l.Id)).ToList())
        {
            if (_documents.ContainsKey(tab.Document)) continue;
            OnTabOpened(tab);
        }

        foreach (var (document, doc) in _documents.Where(d => spec.Languages.Contains(d.Value.LanguageId)).ToList())
        {
            if (doc.Client is not null) continue;
            var client = await Task.Run(() => HostFor(doc.Path).ClientForAsync(doc.LanguageId));
            doc.Bind(client);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var doc in _documents.Values) await doc.DisposeAsync();
        _documents.Clear();
        foreach (var host in _hosts.Values) await host.DisposeAsync();
        _hosts.Clear();
    }
}
