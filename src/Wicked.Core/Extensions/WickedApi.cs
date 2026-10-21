using System.Text.Json;
using Wicked.Core.Lsp;

namespace Wicked.Core.Extensions;

/// <summary>Runs handlers one after another on the thread pool, so an extension sees events in order and never on the interface thread.</summary>
internal sealed class SerialQueue
{
    private readonly Lock _gate = new();
    private Task _tail = Task.CompletedTask;
    private bool _closed;

    public void Enqueue(Func<Task> work)
    {
        lock (_gate)
        {
            if (_closed) return;
            _tail = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    public void Close()
    {
        lock (_gate) _closed = true;
    }
}

/// <summary>
/// The <see cref="IWickedApi"/> one extension (or the user's init.csx) is given: it forwards to the host, remembers
/// everything registered through it, and takes it all back on <see cref="Dispose"/>. Handlers run on the thread pool; a
/// handler that throws is reported to the Output panel and the extension goes on.
/// </summary>
public sealed class WickedApi : IWickedApi, IDisposable
{
    private readonly IHostBridge _host;
    private readonly NoticeBoard _notices;
    private readonly ExtensionRegistry _registry;
    private readonly List<IDisposable> _registrations = [];
    private readonly HashSet<string> _noticeKeys = [];
    private readonly HashSet<string> _panels = [];
    private readonly Lock _gate = new();
    private readonly SerialQueue _queue = new();
    private readonly List<Action> _unhook = [];
    private readonly Dictionary<string, string> _commandTitles = [];
    private bool _disposed;

    public WickedApi(string extensionId, string folder, IHostBridge host, NoticeBoard notices, ExtensionRegistry registry)
    {
        ExtensionId = extensionId;
        ExtensionFolder = folder;
        _host = host;
        _notices = notices;
        _registry = registry;
        Commands = new CommandApi(this);
        Notices = new NoticeApi(this);
        Output = new OutputApi(this);
        Settings = new SettingsApi(this);
        Workspace = new WorkspaceApi(this);
        Editors = new EditorApi(this);
        Events = new EventApi(this);
        Views = new ViewApi(this);
        Contributions = new ContributionApi(this);
    }

    public Version ApiVersion => WickedApiVersion.Current;

    public string ExtensionId { get; }

    public string ExtensionFolder { get; }

    public ICommandApi Commands { get; }

    public INoticeApi Notices { get; }

    public IOutputApi Output { get; }

    public ISettingsApi Settings { get; }

    public IWorkspaceApi Workspace { get; }

    public IEditorApi Editors { get; }

    public IEventApi Events { get; }

    public IViewHost Views { get; }

    public IContributionApi Contributions { get; }

    /// <summary>The commands registered now, as "title".</summary>
    internal IReadOnlyList<string> CommandTitles
    {
        get
        {
            lock (_gate) return [.. _commandTitles.Values];
        }
    }

    /// <summary>The panels added now.</summary>
    internal IReadOnlyList<string> PanelIds
    {
        get
        {
            lock (_gate) return [.. _panels];
        }
    }

    /// <summary>The channel an extension's own errors go to.</summary>
    public static string ErrorChannel => "Extensions";

    internal void Report(string what, Exception ex)
    {
        _host.WriteOutput(ErrorChannel, $"[{ExtensionId}] {what}: {ex.GetType().Name}: {ex.Message}");
        Log.Warn($"Extension {ExtensionId}: {what}: {ex}");
        _notices.Post(new Notice($"ext:{ExtensionId}:error", $"{ExtensionId}: {what} failed ({ex.Message})", NoticeKind.Warning, "output/" + ErrorChannel));
    }

    /// <summary>Runs an extension's callback on the pool and reports a failure instead of letting it escape.</summary>
    internal Task RunGuarded(string what, Func<Task> body) => Task.Run(async () =>
    {
        try
        {
            await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Report(what, ex);
        }
    });

    private T Track<T>(T registration)
        where T : IDisposable
    {
        lock (_gate)
        {
            if (_disposed)
            {
                registration.Dispose();
                throw new ObjectDisposedException(ExtensionId, "the extension was deactivated");
            }

            _registrations.Add(registration);
        }

        return registration;
    }

    private IDisposable Tracked(IDisposable inner)
    {
        var handle = new Handle(inner, this);
        return Track(handle);
    }

    private void Forget(IDisposable handle)
    {
        lock (_gate) _registrations.Remove(handle);
    }

    private sealed class Handle(IDisposable inner, WickedApi owner) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            owner.Forget(this);
            inner.Dispose();
        }
    }

    /// <summary>Takes back everything the extension registered: commands, keys, panels, marks, notices, handlers.</summary>
    public void Dispose()
    {
        List<IDisposable> registrations;
        List<string> panels;
        List<Action> unhook;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            registrations = [.. _registrations];
            _registrations.Clear();
            panels = [.. _panels];
            unhook = [.. _unhook];
        }

        _queue.Close();
        foreach (var undo in unhook) undo();
        foreach (var registration in registrations.AsEnumerable().Reverse())
        {
            try
            {
                registration.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Extension {ExtensionId}: taking a registration back failed: {ex.Message}");
            }
        }

        foreach (var panel in panels) _ = _host.RemovePanelAsync(ExtensionId, panel);
        _ = _host.ClearDecorationsAsync(ExtensionId, "*", null);
        foreach (var key in _noticeKeys) _notices.Clear(key);
    }

    private sealed class CommandApi(WickedApi api) : ICommandApi
    {
        public IDisposable Register(string id, string title, Func<CancellationToken, Task> run, string? keys = null, string category = "")
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            ArgumentNullException.ThrowIfNull(run);
            var cts = new CancellationTokenSource();
            var inner = api._host.AddCommand(id, category, title, keys, () => api.RunGuarded($"command {id}", () => run(cts.Token)));
            lock (api._gate) api._commandTitles[id] = title;
            return api.Tracked(new Composite(inner, cts, () =>
            {
                lock (api._gate) api._commandTitles.Remove(id);
            }));
        }

        public IDisposable Register(string id, string title, Action run, string? keys = null, string category = "") =>
            Register(id, title, _ =>
            {
                run();
                return Task.CompletedTask;
            }, keys, category);

        public IDisposable BindKeys(string commandId, string keys) => api.Tracked(api._host.AddKeybinding(commandId, keys));

        public Task<bool> ExecuteAsync(string commandId, CancellationToken ct = default) => api._host.RunCommandAsync(commandId).WaitAsync(ct);

        public Task<IReadOnlyList<string>> ListAsync() => Task.FromResult(api._host.CommandIds());
    }

    private sealed class Composite(IDisposable inner, CancellationTokenSource cts, Action forget) : IDisposable
    {
        public void Dispose()
        {
            forget();
            cts.Cancel();
            inner.Dispose();
            cts.Dispose();
        }
    }

    private sealed class NoticeApi(WickedApi api) : INoticeApi
    {
        public void Post(string text, NoticeLevel level = NoticeLevel.Info, string? key = null, string? route = null)
        {
            var full = $"ext:{api.ExtensionId}:{key ?? "notice"}";
            lock (api._gate)
            {
                if (api._disposed) return;
                api._noticeKeys.Add(full);
            }

            var kind = level switch { NoticeLevel.Success => NoticeKind.Success, NoticeLevel.Warning => NoticeKind.Warning, NoticeLevel.Error => NoticeKind.Error, _ => NoticeKind.Info };
            api._notices.Post(new Notice(full, text, kind, route));
        }

        public void Clear(string key) => api._notices.Clear($"ext:{api.ExtensionId}:{key}");
    }

    private sealed class OutputApi(WickedApi api) : IOutputApi
    {
        public IOutputChannel Channel(string name) => new OutputChannelHandle(api._host, name);
    }

    private sealed class OutputChannelHandle(IHostBridge host, string name) : IOutputChannel
    {
        public string Name => name;

        public void WriteLine(string line) => host.WriteOutput(name, line);

        public void Clear() => host.ClearOutput(name);

        public Task ShowAsync() => host.ShowOutputAsync(name);
    }

    private sealed class SettingsApi : ISettingsApi
    {
        private readonly WickedApi _api;
        private Action<IReadOnlyList<string>>? _changed;

        public SettingsApi(WickedApi api)
        {
            _api = api;
            api._host.SettingsChanged += OnChanged;
            api._unhook.Add(() => api._host.SettingsChanged -= OnChanged);
        }

        public event Action<IReadOnlyList<string>>? Changed
        {
            add => _changed += value;
            remove => _changed -= value;
        }

        private void OnChanged(IReadOnlyList<string> keys)
        {
            if (_changed is not { } handlers) return;
            _api._queue.Enqueue(() => _api.RunGuarded("settings changed handler", () =>
            {
                handlers(keys);
                return Task.CompletedTask;
            }));
        }

        public async Task<T?> GetAsync<T>(string key, T? fallback = default)
        {
            var user = await _api._host.GetSettingAsync(key).ConfigureAwait(false);
            var value = user ?? _api._registry.SettingDefault(key);
            if (value is not { } element) return fallback;
            try
            {
                return element.Deserialize<T>(ExtensionManifest.Json);
            }
            catch (JsonException)
            {
                return fallback;
            }
        }

        public Task SetAsync<T>(string key, T value) =>
            _api._host.SetSettingAsync(key, JsonSerializer.SerializeToElement(value, ExtensionManifest.Json));
    }

    private sealed class WorkspaceApi(WickedApi api) : IWorkspaceApi
    {
        public Task<string?> GetRootAsync() => api._host.GetRootAsync();

        public Task OpenFileAsync(string path, int? line = null, int? column = null) => api._host.OpenFileAsync(path, line, column);
    }

    private sealed class EditorApi(WickedApi api) : IEditorApi
    {
        public Task<EditorSnapshot?> GetActiveAsync() => api._host.GetActiveAsync();

        public Task<IReadOnlyList<EditorSnapshot>> GetOpenAsync() => api._host.GetOpenAsync();

        public Task<EditorSnapshot?> GetAsync(string documentId) => api._host.GetDocumentAsync(documentId);

        public Task<bool> ReplaceAsync(string documentId, TextRange range, string text, long? expectedVersion = null) =>
            api._host.ReplaceAsync(documentId, range, text, expectedVersion);

        public Task<bool> InsertAtCaretsAsync(string text) => api._host.InsertAtCaretsAsync(text);

        public Task SetSelectionsAsync(string documentId, IReadOnlyList<TextRange> selections) => api._host.SetSelectionsAsync(documentId, selections);

        public Task SetDecorationsAsync(string documentId, string layer, Decorations marks, long? forVersion = null) =>
            api._host.SetDecorationsAsync(api.ExtensionId, documentId, layer, marks, forVersion);

        public Task ClearDecorationsAsync(string documentId, string? layer = null) => api._host.ClearDecorationsAsync(api.ExtensionId, documentId, layer);
    }

    private sealed class EventApi : IEventApi
    {
        private readonly WickedApi _api;

        public EventApi(WickedApi api)
        {
            _api = api;
            var host = api._host;
            Action<EditorSnapshot> opened = d => Raise(DocumentOpened, d);
            Action<EditorSnapshot> saved = d => Raise(DocumentSaved, d);
            Action<EditorSnapshot> closed = d => Raise(DocumentClosed, d);
            Action<EditorSnapshot?> active = d => RaiseActive(d);
            host.DocumentOpened += opened;
            host.DocumentSaved += saved;
            host.DocumentClosed += closed;
            host.ActiveEditorChanged += active;
            api._unhook.Add(() =>
            {
                host.DocumentOpened -= opened;
                host.DocumentSaved -= saved;
                host.DocumentClosed -= closed;
                host.ActiveEditorChanged -= active;
            });
        }

        public event Action<DocumentEvent>? DocumentOpened;

        public event Action<DocumentEvent>? DocumentSaved;

        public event Action<DocumentEvent>? DocumentClosed;

        public event Action<DocumentEvent?>? ActiveEditorChanged;

        private void Raise(Action<DocumentEvent>? handlers, EditorSnapshot document)
        {
            if (handlers is null) return;
            _api._queue.Enqueue(() => _api.RunGuarded("document event handler", () =>
            {
                handlers(new DocumentEvent(document));
                return Task.CompletedTask;
            }));
        }

        private void RaiseActive(EditorSnapshot? document)
        {
            var handlers = ActiveEditorChanged;
            if (handlers is null) return;
            _api._queue.Enqueue(() => _api.RunGuarded("active editor handler", () =>
            {
                handlers(document is null ? null : new DocumentEvent(document));
                return Task.CompletedTask;
            }));
        }
    }

    private sealed class ViewApi(WickedApi api) : IViewHost
    {
        public Task AddBottomTabAsync(string id, string title, Func<object> createView)
        {
            lock (api._gate) api._panels.Add(id);
            return api._host.AddBottomTabAsync(api.ExtensionId, id, title, createView);
        }

        public Task AddSidePanelAsync(string id, string title, string icon, Func<object> createView)
        {
            lock (api._gate) api._panels.Add(id);
            return api._host.AddSidePanelAsync(api.ExtensionId, id, title, icon, createView);
        }

        public Task ShowAsync(string id) => api._host.ShowPanelAsync(api.ExtensionId, id);

        public Task RemoveAsync(string id)
        {
            lock (api._gate) api._panels.Remove(id);
            return api._host.RemovePanelAsync(api.ExtensionId, id);
        }
    }

    private sealed class ContributionApi(WickedApi api) : IContributionApi
    {
        public IDisposable AddLanguageServer(LanguageServerSpec spec) => api.Tracked(LanguageServerCatalog.Register(api.ExtensionId, spec));

        public IDisposable AddSnippetFile(string path, string? languageId = null) => api.Tracked(api._host.AddSnippetFile(api.Resolve(path), languageId));

        public IDisposable AddThemeFile(string path, string? label = null) => api.Tracked(api._host.AddThemeFile(api.Resolve(path), label));

        public IDisposable AddGrammar(GrammarSpec grammar) =>
            api.Tracked(api._host.AddGrammar(grammar with { Path = api.Resolve(grammar.Path), Configuration = grammar.Configuration is null ? null : api.Resolve(grammar.Configuration) }));
    }

    private string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(ExtensionFolder, path));
}
