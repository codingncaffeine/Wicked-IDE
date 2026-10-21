using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Wicked.Core.Lsp;

/// <summary>Where a server stands in this workspace, for the status bar and the Output log.</summary>
public enum ServerState
{
    /// <summary>No server is installed for the language (the catalog knows one that could be).</summary>
    Missing,

    Starting,

    Running,

    /// <summary>It crashed too often (five times in three minutes, VS Code's rule) or would not start; it stays down until asked again.</summary>
    Failed,

    Stopped,
}

/// <summary>A server's state in this workspace, and what to tell the user about it.</summary>
public sealed record ServerStatus(string ServerId, string Name, ServerState State, string? Detail = null);

/// <summary>A document's problems as its server last published them, with the server that sent them.</summary>
public sealed record DocumentDiagnostics(string Uri, string ServerId, int? Version, IReadOnlyList<Diagnostic> Items);

/// <summary>
/// Finds the program for a server: the user's own command first (<c>lsp.&lt;id&gt;.command</c>), then what Wicked installed
/// into its servers folder, then the system's PATH.
/// </summary>
public sealed class ServerLocator(string serversFolder, Func<string, string?>? env = null, IReadOnlyDictionary<string, IReadOnlyList<string>>? userCommands = null)
{
    private readonly Func<string, string?> _env = env ?? Environment.GetEnvironmentVariable;

    public string ServersFolder { get; } = serversFolder;

    /// <summary>The command to run <paramref name="spec"/> here, or null when it is not installed.</summary>
    public LanguageServerCommand? Find(LanguageServerSpec spec)
    {
        if (userCommands is not null && userCommands.TryGetValue(spec.Id, out var own) && own.Count > 0)
        {
            var program = Resolve(own[0]);
            return program is null ? null : new LanguageServerCommand(program, [.. own.Skip(1)]);
        }

        foreach (var name in spec.Programs)
        {
            var installed = Installed(spec.Id, name);
            if (installed is not null) return new LanguageServerCommand(installed, spec.Arguments);
            var onPath = OnPath(name);
            if (onPath is not null) return new LanguageServerCommand(onPath, spec.Arguments);
        }

        return null;
    }

    /// <summary>A server Wicked installed: its folder's <c>bin/&lt;name&gt;</c> (a launcher the installer wrote) or the program itself.</summary>
    private string? Installed(string id, string name)
    {
        var folder = Path.Combine(ServersFolder, id);
        if (!Directory.Exists(folder)) return null;
        foreach (var candidate in new[] { Path.Combine(folder, "bin", name), Path.Combine(folder, name) })
        {
            foreach (var withExe in Executables(candidate))
            {
                if (File.Exists(withExe)) return withExe;
            }
        }

        return null;
    }

    private string? Resolve(string program) => Path.IsPathRooted(program) ? (File.Exists(program) ? program : null) : OnPath(program);

    private string? OnPath(string name)
    {
        var path = _env("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in Executables(Path.Combine(dir, name)))
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Executables(string path)
    {
        yield return path;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) yield break;
        foreach (var ext in new[] { ".exe", ".cmd", ".bat" }) yield return path + ext;
    }
}

/// <summary>
/// The language servers of one open folder: a server starts the first time a document of its language opens, one process
/// per server for the whole folder; a server that dies is started again (and told about the open documents again by
/// whoever listens to <see cref="Restarted"/>) until it has crashed five times in three minutes. Diagnostics from every
/// server are kept by document. Nothing here runs on the caller's thread for long: starts and stops are tasks.
/// </summary>
public sealed class LanguageServerHost : IAsyncDisposable
{
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(3);
    private const int CrashLimit = 5;

    private readonly ServerLocator _locator;
    private readonly string _logs;
    private readonly ConcurrentDictionary<string, Lazy<Task<LanguageClient?>>> _clients = new();
    private readonly ConcurrentDictionary<string, List<DateTime>> _crashes = new();
    private readonly ConcurrentDictionary<string, ServerStatus> _status = new();
    private readonly ConcurrentDictionary<string, DocumentDiagnostics> _diagnostics = new();
    private readonly TimeProvider _time;
    private int _disposed;

    public LanguageServerHost(string root, ServerLocator locator, string logFolder, TimeProvider? time = null)
    {
        Root = root;
        _locator = locator;
        _logs = logFolder;
        _time = time ?? TimeProvider.System;
    }

    public string Root { get; }

    /// <summary>Servers the user turned off (<c>lsp.&lt;id&gt;.enabled: false</c>) are never started.</summary>
    public IReadOnlySet<string> Disabled { get; set; } = new HashSet<string>();

    /// <summary>A document's diagnostics changed (its URI).</summary>
    public event Action<string>? DiagnosticsChanged;

    /// <summary>A server's state changed.</summary>
    public event Action<ServerStatus>? StatusChanged;

    /// <summary>A server came back after it died: its documents must be opened in it again.</summary>
    public event Action<string, LanguageClient>? Restarted;

    /// <summary>A server's log or message line (for the Output panel), with the server's id.</summary>
    public event Action<string, string>? Output;

    /// <summary>The command a server starts with, given its settings and start options (the user's, and Wicked's own for some).</summary>
    public Func<LanguageServerSpec, LanguageServerCommand, LanguageServerCommand>? Configure { get; set; }

    /// <summary>A server asked the client to apply an edit (a refactoring it runs itself).</summary>
    public Func<WorkspaceEdit, Task<bool>>? ApplyEdit { get; set; }

    public IReadOnlyCollection<ServerStatus> Statuses => [.. _status.Values];

    public ServerStatus? StatusOf(string serverId) => _status.TryGetValue(serverId, out var s) ? s : null;

    /// <summary>The server that would serve <paramref name="languageId"/>: the first installed one in the catalog, else the first known (missing).</summary>
    public (LanguageServerSpec Spec, LanguageServerCommand? Command)? Pick(string languageId)
    {
        LanguageServerSpec? first = null;
        foreach (var spec in LanguageServerCatalog.For(languageId))
        {
            if (Disabled.Contains(spec.Id)) continue;
            first ??= spec;
            if (_locator.Find(spec) is { } command) return (spec, command);
        }

        return first is null ? null : (first, null);
    }

    /// <summary>The running client for a language, starting its server on first use; null when none is installed or it failed.</summary>
    public async Task<LanguageClient?> ClientForAsync(string languageId, CancellationToken ct = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || Pick(languageId) is not { } pick) return null;
        if (pick.Command is null)
        {
            SetStatus(new ServerStatus(pick.Spec.Id, pick.Spec.Name, ServerState.Missing, MissingHint(pick.Spec)));
            return null;
        }

        var command = Configure?.Invoke(pick.Spec, pick.Command) ?? pick.Command;
        var lazy = _clients.GetOrAdd(pick.Spec.Id, _ => new Lazy<Task<LanguageClient?>>(() => StartAsync(pick.Spec, command)));
        var client = await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        return client is { IsRunning: true } ? client : null;
    }

    /// <summary>What to install, for a server that is missing.</summary>
    public static string MissingHint(LanguageServerSpec spec)
    {
        var how = spec.Install switch
        {
            InstallKind.GitHubRelease => "Wicked can install it",
            InstallKind.Npm => $"Wicked can install it with npm ({spec.Package})",
            InstallKind.DotnetTool => $"Wicked can install it with dotnet tool ({spec.Package})",
            InstallKind.Go => $"Wicked can install it with go install ({spec.Package})",
            _ => "install it with your package manager",
        };
        var packages = spec.SystemPackages.Count == 0 ? "" : " — packages: " + string.Join(", ", spec.SystemPackages.Select(kv => $"{kv.Key} {kv.Value}"));
        return $"{spec.Name} is not installed: {how}{packages}";
    }

    private async Task<LanguageClient?> StartAsync(LanguageServerSpec spec, LanguageServerCommand command)
    {
        SetStatus(new ServerStatus(spec.Id, spec.Name, ServerState.Starting));
        try
        {
            Directory.CreateDirectory(_logs);
            var client = await LanguageClient.StartAsync(command, Root, Path.Combine(_logs, $"lsp-{spec.Id}.log")).ConfigureAwait(false);
            client.ApplyEdit = ApplyEdit;
            client.Diagnostics += (_, d) => OnDiagnostics(spec.Id, d);
            client.Message += (_, m) => Output?.Invoke(spec.Id, m.Message);
            client.Exited += (_, code) => OnExited(spec, command, client, code);
            SetStatus(new ServerStatus(spec.Id, client.ServerName, ServerState.Running, command.Executable));
            Output?.Invoke(spec.Id, $"Started {client.ServerName} ({command.Executable}) in {Root}");
            return client;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException or OperationCanceledException)
        {
            Log.Warn($"Language server {spec.Id} did not start: {ex.Message}");
            Output?.Invoke(spec.Id, $"Could not start {spec.Name}: {ex.Message}");
            SetStatus(new ServerStatus(spec.Id, spec.Name, ServerState.Failed, ex.Message));
            _clients.TryRemove(spec.Id, out _);
            return null;
        }
    }

    private void OnExited(LanguageServerSpec spec, LanguageServerCommand command, LanguageClient client, int? code)
    {
        if (Volatile.Read(ref _disposed) != 0 || client.Stopping) return;
        ClearDiagnosticsOf(spec.Id);
        var now = _time.GetUtcNow().UtcDateTime;
        var crashes = _crashes.GetOrAdd(spec.Id, _ => []);
        int recent;
        lock (crashes)
        {
            crashes.Add(now);
            crashes.RemoveAll(t => now - t > CrashWindow);
            recent = crashes.Count;
        }

        Output?.Invoke(spec.Id, $"{spec.Name} exited (code {code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}).");
        if (recent >= CrashLimit)
        {
            _clients.TryRemove(spec.Id, out _);
            SetStatus(new ServerStatus(spec.Id, spec.Name, ServerState.Failed, $"{spec.Name} crashed {recent} times in the last 3 minutes and was not started again."));
            return;
        }

        // Started again in its place; the documents follow when it is up.
        var lazy = new Lazy<Task<LanguageClient?>>(() => StartAsync(spec, command));
        _clients[spec.Id] = lazy;
        _ = lazy.Value.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } fresh) Restarted?.Invoke(spec.Id, fresh);
        }, TaskScheduler.Default);
    }

    /// <summary>Stops one server and forgets its crashes (Restart Language Server); the next document starts it fresh.</summary>
    public async Task RestartAsync(string serverId)
    {
        _crashes.TryRemove(serverId, out _);
        if (_clients.TryRemove(serverId, out var lazy) && lazy.IsValueCreated && await lazy.Value.ConfigureAwait(false) is { } client)
        {
            client.Stopping = true;
            await client.DisposeAsync().ConfigureAwait(false);
        }

        ClearDiagnosticsOf(serverId);
        SetStatus(new ServerStatus(serverId, LanguageServerCatalog.ById(serverId)?.Name ?? serverId, ServerState.Stopped));
    }

    // ── Diagnostics ──────────────────────────────────────────────────────────────────────────────

    public IReadOnlyCollection<DocumentDiagnostics> AllDiagnostics => [.. _diagnostics.Values];

    public DocumentDiagnostics? DiagnosticsFor(string uri) => _diagnostics.TryGetValue(Key(uri), out var d) ? d : null;

    private void OnDiagnostics(string serverId, PublishDiagnosticsParams p)
    {
        var key = Key(p.Uri);
        if (p.Diagnostics.Count == 0) _diagnostics.TryRemove(key, out _);
        else _diagnostics[key] = new DocumentDiagnostics(p.Uri, serverId, p.Version, p.Diagnostics);
        DiagnosticsChanged?.Invoke(p.Uri);
    }

    private void ClearDiagnosticsOf(string serverId)
    {
        foreach (var (key, d) in _diagnostics)
        {
            if (d.ServerId != serverId || !_diagnostics.TryRemove(key, out _)) continue;
            DiagnosticsChanged?.Invoke(d.Uri);
        }
    }

    // Servers spell the same file's URI differently (an escaped colon, a drive letter's case); one key per file.
    private static string Key(string uri) => LspPaths.ToPath(uri) is { } path ? Path.GetFullPath(path) : uri;

    private void SetStatus(ServerStatus status)
    {
        _status[status.ServerId] = status;
        StatusChanged?.Invoke(status);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var running = new List<LanguageClient>();
        foreach (var lazy in _clients.Values)
        {
            if (!lazy.IsValueCreated) continue;
            try
            {
                if (await lazy.Value.ConfigureAwait(false) is { } c) running.Add(c);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
            }
        }

        _clients.Clear();
        foreach (var c in running) c.Stopping = true;
        await Task.WhenAll(running.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
