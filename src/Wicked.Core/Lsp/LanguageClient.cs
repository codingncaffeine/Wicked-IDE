using System.Diagnostics;
using System.Text.Json;
using Wicked.Core.Engines;

namespace Wicked.Core.Lsp;

/// <summary>
/// How to run a language server: its program and arguments, and what it is told at <c>initialize</c>. <c>Settings</c> answers
/// <c>workspace/configuration</c>: an object whose dotted paths are the sections the server asks for.
/// </summary>
public sealed record LanguageServerCommand(
    string Executable,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null,
    JsonElement? InitializationOptions = null,
    JsonElement? Settings = null);

/// <summary>File paths and the <c>file://</c> URIs the protocol names documents by.</summary>
public static class LspPaths
{
    public static string ToUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    /// <summary>The local path of a <c>file://</c> URI, or null for any other scheme.</summary>
    public static string? ToPath(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null;
}

/// <summary>
/// One language server process and the client side of its protocol (D14): started over stdio, initialised with the client's
/// capabilities, kept in step with open documents, asked for completions, hovers, definitions and the rest. stderr goes to a
/// rotating log. Every call is async and none waits on the caller's thread; events are raised on the reader task.
/// </summary>
public sealed class LanguageClient : IAsyncDisposable
{
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(3);

    private readonly Process _process;
    private readonly JsonRpcConnection _rpc;
    private readonly RotatingLog _log;
    private readonly LanguageServerCommand _command;
    private readonly List<WorkspaceFolder> _folders;
    private int _disposed;

    private LanguageClient(Process process, JsonRpcConnection rpc, RotatingLog log, LanguageServerCommand command, List<WorkspaceFolder> folders)
    {
        _process = process;
        _rpc = rpc;
        _log = log;
        _command = command;
        _folders = folders;
    }

    /// <summary>The diagnostics of one document, replacing its previous set.</summary>
    public event EventHandler<PublishDiagnosticsParams>? Diagnostics;

    /// <summary><c>window/logMessage</c> and <c>window/showMessage</c> lines (type 1 error … 4 log).</summary>
    public event EventHandler<(int Type, string Message)>? Message;

    /// <summary>The server's process ended, whoever ended it.</summary>
    public event EventHandler<int?>? Exited;

    /// <summary>
    /// Answers <c>workspace/applyEdit</c> (a rename or code action the server applies itself): true when the edit was applied.
    /// Unset, every such request is refused.
    /// </summary>
    public Func<WorkspaceEdit, Task<bool>>? ApplyEdit { get; set; }

    public InitializeResult Initialized { get; private set; } = null!;

    public string ServerName => Initialized.ServerInfo?.Name ?? Path.GetFileName(_command.Executable);

    /// <summary>1 full documents, 2 incremental changes (0 none), from the server's <c>textDocumentSync</c>.</summary>
    public int SyncKind { get; private set; }

    public bool IsRunning => _disposed == 0 && !_rpc.IsClosed;

    /// <summary>Starts the server in <paramref name="root"/>, initialises it, and returns once it said <c>initialized</c>.</summary>
    public static async Task<LanguageClient> StartAsync(LanguageServerCommand command, string root, string logPath, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(command.Executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = root,
        };
        foreach (var a in command.Arguments) info.ArgumentList.Add(a);
        if (command.Environment is not null)
        {
            foreach (var (k, v) in command.Environment) info.Environment[k] = v;
        }

        var log = new RotatingLog(logPath);
        var process = await Task.Run(() => Process.Start(info) ?? throw new IOException($"{command.Executable} did not start"), ct).ConfigureAwait(false);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) log.Append(e.Data);
        };
        process.BeginErrorReadLine();

        var rpc = new JsonRpcConnection(process.StandardOutput.BaseStream, process.StandardInput.BaseStream);
        var client = new LanguageClient(process, rpc, log, command, [new WorkspaceFolder(LspPaths.ToUri(root), Path.GetFileName(Path.TrimEndingDirectorySeparator(root)))]);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => client.Exited?.Invoke(client, SafeExitCode(process));
        client.Wire();
        rpc.Start();
        try
        {
            await client.InitializeAsync(root, ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return client;
    }

    private void Wire()
    {
        _rpc.OnNotification("textDocument/publishDiagnostics", p => Diagnostics?.Invoke(this, LspShapes.Read<PublishDiagnosticsParams>(p)));
        _rpc.OnNotification("window/logMessage", p => OnMessage(p));
        _rpc.OnNotification("window/showMessage", p => OnMessage(p));
        _rpc.OnNotification("$/progress", _ => { });
        _rpc.OnNotification("telemetry/event", _ => { });
        _rpc.Unhandled += (_, method) => _log.Append($"[wicked] unhandled notification {method}");

        _rpc.OnRequest("workspace/configuration", (p, _) => Task.FromResult(Configuration(p)));
        _rpc.OnRequest("workspace/workspaceFolders", (_, _) => Task.FromResult(LspShapes.Write(_folders)));
        _rpc.OnRequest("window/workDoneProgress/create", (_, _) => Task.FromResult(default(JsonElement)));
        _rpc.OnRequest("client/registerCapability", (_, _) => Task.FromResult(default(JsonElement)));
        _rpc.OnRequest("client/unregisterCapability", (_, _) => Task.FromResult(default(JsonElement)));
        _rpc.OnRequest("window/showMessageRequest", (p, _) =>
        {
            OnMessage(p);
            return Task.FromResult(default(JsonElement));
        });
        _rpc.OnRequest("workspace/applyEdit", async (p, _) =>
        {
            var applied = ApplyEdit is { } apply && p.TryGetProperty("edit", out var e) && LspShapes.Edit(e) is { } edit && await apply(edit).ConfigureAwait(false);
            return JsonDocument.Parse(applied ? """{"applied":true}""" : """{"applied":false}""").RootElement.Clone();
        });
    }

    private void OnMessage(JsonElement p)
    {
        var type = p.TryGetProperty("type", out var t) && t.TryGetInt32(out var n) ? n : 4;
        var text = p.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        _log.Append($"[{type}] {text}");
        Message?.Invoke(this, (type, text));
    }

    /// <summary>One answer per item asked for: the value at its dotted <c>section</c> in the settings, else null.</summary>
    private JsonElement Configuration(JsonElement p)
    {
        var answers = new List<string>();
        var asked = p.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray().ToList() : [];
        foreach (var item in asked)
        {
            JsonElement? at = _command.Settings;
            if (item.TryGetProperty("section", out var section) && section.GetString() is { Length: > 0 } path)
            {
                foreach (var part in path.Split('.'))
                {
                    at = at is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(part, out var next) ? next : null;
                }
            }

            answers.Add(at is { } value ? value.GetRawText() : "null");
        }

        return JsonDocument.Parse($"[{string.Join(',', answers)}]").RootElement.Clone();
    }

    private async Task InitializeAsync(string root, CancellationToken ct)
    {
        var parameters = $$"""
            {
              "processId": {{Environment.ProcessId}},
              "clientInfo": { "name": "Wicked IDE", "version": "{{BuildInfo.Version}}" },
              "locale": "en",
              "rootPath": {{JsonSerializer.Serialize(root, LspJson.Default.String)}},
              "rootUri": {{JsonSerializer.Serialize(LspPaths.ToUri(root), LspJson.Default.String)}},
              "workspaceFolders": {{JsonSerializer.Serialize(_folders, LspJson.Default.ListWorkspaceFolder)}},
              "initializationOptions": {{_command.InitializationOptions?.GetRawText() ?? "null"}},
              "capabilities": {{ClientCapabilities}},
              "trace": "off"
            }
            """;
        var answer = await _rpc.RequestAsync("initialize", JsonDocument.Parse(parameters).RootElement.Clone(), ct).ConfigureAwait(false);
        Initialized = LspShapes.Read<InitializeResult>(answer);
        SyncKind = ReadSyncKind(Initialized.Capabilities);
        _rpc.Notify("initialized", JsonDocument.Parse("{}").RootElement.Clone());
        if (_command.Settings is { } settings)
        {
            _rpc.Notify("workspace/didChangeConfiguration", JsonDocument.Parse($$"""{"settings":{{settings.GetRawText()}}}""").RootElement.Clone());
        }
    }

    private static int ReadSyncKind(JsonElement capabilities)
    {
        if (!capabilities.TryGetProperty("textDocumentSync", out var sync)) return 0;
        if (sync.ValueKind == JsonValueKind.Number) return sync.GetInt32();
        return sync.ValueKind == JsonValueKind.Object && sync.TryGetProperty("change", out var change) && change.TryGetInt32(out var kind) ? kind : 0;
    }

    /// <summary>Whether the server announced a capability, by its name in <c>ServerCapabilities</c> (e.g. <c>hoverProvider</c>).</summary>
    public bool Supports(string capability) =>
        Initialized.Capabilities.TryGetProperty(capability, out var value) && value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null);

    /// <summary>The characters that open completion by themselves (e.g. <c>.</c>), from <c>completionProvider</c>.</summary>
    public IReadOnlyList<string> CompletionTriggers => Initialized.Capabilities.TryGetProperty("completionProvider", out var c)
        && c.ValueKind == JsonValueKind.Object && c.TryGetProperty("triggerCharacters", out var t) && t.ValueKind == JsonValueKind.Array
            ? [.. t.EnumerateArray().Select(x => x.GetString()!)]
            : [];

    // ── Documents ────────────────────────────────────────────────────────────────────────────────

    public void Open(string uri, string languageId, int version, string text) =>
        _rpc.Notify("textDocument/didOpen", LspShapes.Write(new DidOpenTextDocumentParams(new TextDocumentItem(uri, languageId, version, text))));

    /// <summary>
    /// A document changed to <paramref name="version"/>: the incremental <paramref name="changes"/> (applied in order, each against the
    /// text the one before left) when the server takes them, else the whole of <paramref name="fullText"/>.
    /// </summary>
    public void Change(string uri, int version, IReadOnlyList<TextDocumentContentChangeEvent> changes, Func<string> fullText)
    {
        if (SyncKind == 0) return;
        IReadOnlyList<TextDocumentContentChangeEvent> sent = SyncKind == 2 ? changes : [new TextDocumentContentChangeEvent(fullText())];
        _rpc.Notify("textDocument/didChange", LspShapes.Write(new DidChangeTextDocumentParams(new VersionedTextDocumentIdentifier(uri, version), sent)));
    }

    public void Save(string uri, string? text = null) =>
        _rpc.Notify("textDocument/didSave", LspShapes.Write(new DidSaveTextDocumentParams(new TextDocumentIdentifier(uri), IncludesTextOnSave ? text : null)));

    public void Close(string uri) => _rpc.Notify("textDocument/didClose", LspShapes.Write(new DidCloseTextDocumentParams(new TextDocumentIdentifier(uri))));

    private bool IncludesTextOnSave => Initialized.Capabilities.TryGetProperty("textDocumentSync", out var sync) && sync.ValueKind == JsonValueKind.Object
        && sync.TryGetProperty("save", out var save) && save.ValueKind == JsonValueKind.Object
        && save.TryGetProperty("includeText", out var include) && include.ValueKind == JsonValueKind.True;

    // ── Requests ─────────────────────────────────────────────────────────────────────────────────

    private static JsonElement At(string uri, Position position) =>
        LspShapes.Write(new TextDocumentPositionParams(new TextDocumentIdentifier(uri), position));

    public async Task<CompletionList> CompletionAsync(string uri, Position position, string? triggerCharacter, CancellationToken ct = default)
    {
        var context = triggerCharacter is null ? new CompletionContext(1) : new CompletionContext(2, triggerCharacter);
        var result = await _rpc.RequestAsync("textDocument/completion", LspShapes.Write(new CompletionParams(new TextDocumentIdentifier(uri), position, context)), ct).ConfigureAwait(false);
        return LspShapes.Completions(result);
    }

    /// <summary>Fills in a completion's documentation and edits when the server resolves lazily; the item itself otherwise.</summary>
    public async Task<CompletionItem> ResolveAsync(CompletionItem item, CancellationToken ct = default)
    {
        if (!(Initialized.Capabilities.TryGetProperty("completionProvider", out var c) && c.ValueKind == JsonValueKind.Object
            && c.TryGetProperty("resolveProvider", out var r) && r.ValueKind == JsonValueKind.True))
        {
            return item;
        }

        return LspShapes.Read<CompletionItem>(await _rpc.RequestAsync("completionItem/resolve", LspShapes.Write(item), ct).ConfigureAwait(false));
    }

    public async Task<Hover?> HoverAsync(string uri, Position position, CancellationToken ct = default) =>
        LspShapes.Hover(await _rpc.RequestAsync("textDocument/hover", At(uri, position), ct).ConfigureAwait(false));

    public async Task<SignatureHelp?> SignatureHelpAsync(string uri, Position position, CancellationToken ct = default)
    {
        var result = await _rpc.RequestAsync("textDocument/signatureHelp", At(uri, position), ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Object ? LspShapes.Read<SignatureHelp>(result) : null;
    }

    /// <summary><c>definition</c>, <c>declaration</c>, <c>typeDefinition</c> or <c>implementation</c>: where the symbol at a position lives.</summary>
    public async Task<IReadOnlyList<Location>> GoToAsync(string kind, string uri, Position position, CancellationToken ct = default) =>
        LspShapes.Locations(await _rpc.RequestAsync($"textDocument/{kind}", At(uri, position), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<Location>> ReferencesAsync(string uri, Position position, bool includeDeclaration, CancellationToken ct = default) =>
        LspShapes.Locations(await _rpc.RequestAsync("textDocument/references",
            LspShapes.Write(new ReferenceParams(new TextDocumentIdentifier(uri), position, new ReferenceContext(includeDeclaration))), ct).ConfigureAwait(false));

    public async Task<WorkspaceEdit?> RenameAsync(string uri, Position position, string newName, CancellationToken ct = default) =>
        LspShapes.Edit(await _rpc.RequestAsync("textDocument/rename", LspShapes.Write(new RenameParams(new TextDocumentIdentifier(uri), position, newName)), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<TextEdit>> FormatAsync(string uri, FormattingOptions options, Range? range = null, CancellationToken ct = default)
    {
        var doc = new TextDocumentIdentifier(uri);
        var result = range is null
            ? await _rpc.RequestAsync("textDocument/formatting", LspShapes.Write(new DocumentFormattingParams(doc, options)), ct).ConfigureAwait(false)
            : await _rpc.RequestAsync("textDocument/rangeFormatting", LspShapes.Write(new DocumentRangeFormattingParams(doc, range, options)), ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Array ? LspShapes.Read<List<TextEdit>>(result) : [];
    }

    public async Task<IReadOnlyList<DocumentSymbol>> DocumentSymbolsAsync(string uri, CancellationToken ct = default) =>
        LspShapes.Symbols(await _rpc.RequestAsync("textDocument/documentSymbol",
            JsonDocument.Parse($$"""{"textDocument":{{LspShapes.Write(new TextDocumentIdentifier(uri)).GetRawText()}}}""").RootElement.Clone(), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<CodeAction>> CodeActionsAsync(string uri, Range range, IReadOnlyList<Diagnostic> diagnostics, CancellationToken ct = default) =>
        LspShapes.Actions(await _rpc.RequestAsync("textDocument/codeAction",
            LspShapes.Write(new CodeActionParams(new TextDocumentIdentifier(uri), range, new CodeActionContext(diagnostics))), ct).ConfigureAwait(false));

    public async Task<IReadOnlyList<InlayHint>> InlayHintsAsync(string uri, Range range, CancellationToken ct = default) =>
        LspShapes.InlayHints(await _rpc.RequestAsync("textDocument/inlayHint", LspShapes.Write(new InlayHintParams(new TextDocumentIdentifier(uri), range)), ct).ConfigureAwait(false));

    /// <summary>Runs a command a code action or a lens carries (<c>{command, arguments}</c>), as the server defines it.</summary>
    public Task<JsonElement> ExecuteCommandAsync(JsonElement command, CancellationToken ct = default) =>
        _rpc.RequestAsync("workspace/executeCommand", command, ct);

    // ── Ending ───────────────────────────────────────────────────────────────────────────────────

    /// <summary><c>shutdown</c>, then <c>exit</c>; a server that has not ended a few seconds later is killed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (!_rpc.IsClosed)
            {
                using var grace = new CancellationTokenSource(ShutdownGrace);
                await _rpc.RequestAsync("shutdown", null, grace.Token).ConfigureAwait(false);
                _rpc.Notify("exit", null);
            }
        }
        catch (Exception ex) when (ex is JsonRpcException or OperationCanceledException or IOException)
        {
            // A server that cannot shut down cleanly is killed below.
        }

        try
        {
            using var wait = new CancellationTokenSource(ShutdownGrace);
            await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It ended meanwhile.
            }
        }

        await _rpc.DisposeAsync().ConfigureAwait(false);
        _process.Dispose();
        _log.Dispose();
    }

    private static int? SafeExitCode(Process p)
    {
        try
        {
            return p.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>What the client can do, as <c>initialize</c> announces it (LSP 3.17).</summary>
    private const string ClientCapabilities = """
        {
          "general": { "positionEncodings": ["utf-16"] },
          "workspace": {
            "applyEdit": true,
            "workspaceEdit": { "documentChanges": true, "resourceOperations": ["create", "rename", "delete"] },
            "configuration": true,
            "workspaceFolders": true,
            "didChangeConfiguration": { "dynamicRegistration": false },
            "symbol": { "dynamicRegistration": false },
            "executeCommand": { "dynamicRegistration": false }
          },
          "textDocument": {
            "synchronization": { "dynamicRegistration": false, "didSave": true, "willSave": false, "willSaveWaitUntil": false },
            "publishDiagnostics": { "relatedInformation": true, "versionSupport": true, "tagSupport": { "valueSet": [1, 2] }, "codeDescriptionSupport": true },
            "completion": {
              "contextSupport": true,
              "completionItem": {
                "snippetSupport": true, "commitCharactersSupport": true, "deprecatedSupport": true, "preselectSupport": true,
                "insertReplaceSupport": true, "labelDetailsSupport": true,
                "documentationFormat": ["markdown", "plaintext"],
                "resolveSupport": { "properties": ["documentation", "detail", "additionalTextEdits"] },
                "tagSupport": { "valueSet": [1] }
              }
            },
            "hover": { "contentFormat": ["markdown", "plaintext"] },
            "signatureHelp": { "signatureInformation": { "documentationFormat": ["markdown", "plaintext"], "parameterInformation": { "labelOffsetSupport": true }, "activeParameterSupport": true }, "contextSupport": true },
            "definition": { "linkSupport": true },
            "declaration": { "linkSupport": true },
            "typeDefinition": { "linkSupport": true },
            "implementation": { "linkSupport": true },
            "references": {},
            "documentSymbol": { "hierarchicalDocumentSymbolSupport": true },
            "codeAction": {
              "codeActionLiteralSupport": { "codeActionKind": { "valueSet": ["", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll"] } },
              "isPreferredSupport": true,
              "resolveSupport": { "properties": ["edit"] }
            },
            "formatting": {},
            "rangeFormatting": {},
            "rename": { "prepareSupport": false },
            "inlayHint": {}
          },
          "window": { "workDoneProgress": true, "showMessage": {} }
        }
        """;
}

