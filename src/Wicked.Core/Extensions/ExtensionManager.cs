using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Wicked.Core.Lsp;

namespace Wicked.Core.Extensions;

/// <summary>
/// Finds the installed extensions in <c>extensions/&lt;publisher&gt;.&lt;id&gt;-&lt;version&gt;/</c> under the profile's data folder, puts
/// what they declare in force, loads and activates the ones that run code (each in a collectible load context), and
/// installs, disables and removes them. An extension that throws while activating is switched off with a notice and the
/// rest go on. Every public method works on the thread pool; none waits on the interface thread.
/// </summary>
public sealed class ExtensionManager : IAsyncDisposable
{
    /// <summary>An extension that has not finished activating after this long is treated as failed.</summary>
    public static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan DeactivationTimeout = TimeSpan.FromSeconds(5);

    private readonly string _folder;
    private readonly IHostBridge _host;
    private readonly NoticeBoard _notices;
    private readonly HttpClient _http;
    private readonly Uri _githubApi;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WeakReference> _probes = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private HashSet<string> _disabled = new(StringComparer.Ordinal);
    private bool _started;

    public ExtensionManager(string extensionsFolder, IHostBridge host, NoticeBoard notices, ExtensionRegistry? registry = null, HttpClient? http = null, Uri? githubApi = null)
    {
        _folder = extensionsFolder;
        _host = host;
        _notices = notices;
        Registry = registry ?? new ExtensionRegistry();
        _http = http ?? Http.WickedHttp.Shared;
        _githubApi = githubApi ?? new Uri("https://api.github.com/");
    }

    public string Folder => _folder;

    public ExtensionRegistry Registry { get; }

    /// <summary>Raised (on a worker thread) when the list or any extension's state changed.</summary>
    public event Action? Changed;

    private volatile IReadOnlyList<InstalledExtension> _snapshot = [];

    /// <summary>The installed extensions, by name.</summary>
    public IReadOnlyList<InstalledExtension> Installed => _snapshot;

    /// <summary>A weak reference to the load context of an extension that was deactivated, to see that it was collected (tests, diagnostics).</summary>
    public WeakReference? UnloadProbe(string id) => _probes.GetValueOrDefault(id);

    private sealed class Entry
    {
        public required string Folder { get; init; }

        public ExtensionManifest? Manifest { get; init; }

        public string? Problem { get; set; }

        public ExtensionState State { get; set; }

        public double LoadMs { get; set; }

        public double ActivateMs { get; set; }

        public List<IDisposable> Declared { get; } = [];

        public WickedApi? Api { get; set; }

        public ExtensionLoadContext? Context { get; set; }

        public object? Instance { get; set; }

        public CancellationTokenSource? Cts { get; set; }

        public string Id => Manifest?.FullId ?? Path.GetFileName(Folder);

        public string Title => Manifest?.Name ?? Id;
    }

    private string StateFile => Path.Combine(_folder, "state.json");

    // ── Start ──

    /// <summary>Reads the extensions folder, puts the declarations of the enabled extensions in force and activates the ones that start with the IDE.</summary>
    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stale = await Task.Run(() => Scan(), ct).ConfigureAwait(false);
            foreach (var old in stale) await DeactivateAsync(old).ConfigureAwait(false);
            if (!_started)
            {
                _started = true;
                _host.DocumentOpened += OnDocument;
                _host.ActiveEditorChanged += OnActive;
            }

            var starting = _entries.Values.Where(e => e.State == ExtensionState.Inactive && e.Manifest!.ActivationLanguages.Count == 0).ToList();
            await Task.WhenAll(starting.Select(e => ActivateAsync(e, ct))).ConfigureAwait(false);
            Publish();
        }
        finally
        {
            _lock.Release();
        }

        await ActivateForOpenDocumentsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the folder; returns the entries that were running and are no longer wanted (their folder is gone or changed).</summary>
    private List<Entry> Scan()
    {
        var stale = new List<Entry>();
        Directory.CreateDirectory(_folder);
        _disabled = ReadDisabled();
        var found = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(_folder).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(directory);
            if (name.StartsWith('.')) continue;
            var manifest = ReadManifest(directory, out var problem, checkFolderName: true);
            if (manifest is null)
            {
                found[name] = new Entry { Folder = directory, State = ExtensionState.Broken, Problem = problem };
                continue;
            }

            // Several versions of one extension: the newest wins, the older folders are left alone.
            if (found.TryGetValue(manifest.FullId, out var other) && other.Manifest is { } o && CompareVersions(o.Version, manifest.Version) >= 0) continue;
            found[manifest.FullId] = new Entry { Folder = directory, Manifest = manifest, State = ExtensionState.Disabled };
        }

        foreach (var (id, old) in _entries.ToList())
        {
            if (found.TryGetValue(id, out var fresh) && fresh.Folder == old.Folder && old.Manifest is not null)
            {
                found[id] = old; // already running: keep its state
                continue;
            }

            Undeclare(old);
            stale.Add(old);
        }

        _entries.Clear();
        foreach (var (id, entry) in found)
        {
            _entries[id] = entry;
            if (entry.Manifest is null || entry.Declared.Count > 0 || entry.State is ExtensionState.Active or ExtensionState.Inactive or ExtensionState.Failed) continue;
            if (_disabled.Contains(id)) continue;
            Declare(entry);
            entry.State = ExtensionState.Inactive;
        }

        return stale;
    }

    private static int CompareVersions(string a, string b)
    {
        static Version V(string s) => Version.TryParse(s.Split('-')[0], out var v) ? v : new Version(0, 0);
        return V(a).CompareTo(V(b));
    }

    private static ExtensionManifest? ReadManifest(string directory, out string? problem, bool checkFolderName)
    {
        var path = Path.Combine(directory, ExtensionManifest.FileName);
        if (!File.Exists(path))
        {
            problem = $"{ExtensionManifest.FileName} is missing";
            return null;
        }

        try
        {
            var (manifest, why) = ExtensionManifest.Parse(File.ReadAllText(path));
            problem = why;
            if (checkFolderName && manifest is not null && manifest.FolderName != Path.GetFileName(directory))
            {
                problem = $"The folder is called {Path.GetFileName(directory)}, but the manifest says {manifest.FolderName}";
                return null;
            }

            return manifest;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = ex.Message;
            return null;
        }
    }

    private HashSet<string> ReadDisabled()
    {
        try
        {
            if (!File.Exists(StateFile)) return new HashSet<string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(File.ReadAllText(StateFile));
            return doc.RootElement.TryGetProperty("disabled", out var list) && list.ValueKind == JsonValueKind.Array
                ? [.. list.EnumerateArray().Select(e => e.GetString()).OfType<string>()]
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"The extensions' state file could not be read: {ex.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private void WriteDisabled()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(new { disabled = _disabled.Order(StringComparer.Ordinal).ToArray() }, new JsonSerializerOptions { WriteIndented = true, NewLine = "\n" }) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The extensions' state file could not be written: {ex.Message}");
        }
    }

    // ── Declarations ──

    /// <summary>Puts an extension's declared contributions in force (themes, grammars, snippets, servers, keys, setting defaults).</summary>
    private void Declare(Entry entry)
    {
        var manifest = entry.Manifest!;
        var c = manifest.Contributes;
        string Abs(string relative) => Path.GetFullPath(Path.Combine(entry.Folder, relative));
        void Try(string what, Func<IDisposable> add)
        {
            try
            {
                entry.Declared.Add(add());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or JsonException)
            {
                _host.WriteOutput(WickedApi.ErrorChannel, $"[{entry.Id}] {what} could not be added: {ex.Message}");
            }
        }

        foreach (var theme in c.Themes)
        {
            var path = Abs(theme.Path);
            if (!File.Exists(path)) _host.WriteOutput(WickedApi.ErrorChannel, $"[{entry.Id}] theme file {theme.Path} is missing");
            else Try($"theme {theme.Label}", () => _host.AddThemeFile(path, theme.Label.Length == 0 ? null : theme.Label));
        }

        foreach (var grammar in c.Grammars)
        {
            var path = Abs(grammar.Path);
            if (!File.Exists(path)) _host.WriteOutput(WickedApi.ErrorChannel, $"[{entry.Id}] grammar file {grammar.Path} is missing");
            else Try($"grammar {grammar.Language}", () => _host.AddGrammar(new GrammarSpec(grammar.Language, grammar.ScopeName, path, grammar.Extensions, grammar.FileNames, grammar.Configuration is null ? null : Abs(grammar.Configuration))));
        }

        foreach (var snippet in c.Snippets)
        {
            var path = Abs(snippet.Path);
            if (!File.Exists(path)) _host.WriteOutput(WickedApi.ErrorChannel, $"[{entry.Id}] snippet file {snippet.Path} is missing");
            else Try("snippets", () => _host.AddSnippetFile(path, snippet.Language));
        }

        foreach (var server in c.LanguageServers) Try($"language server {server.Id}", () => LanguageServerCatalog.Register(entry.Id, ToSpec(server, entry.Folder)));
        foreach (var key in c.Keybindings) Try($"key binding for {key.Command}", () => _host.AddKeybinding(key.Command, key.Key));
        if (c.Settings.Count > 0) Registry.SetDefaults(entry.Id, c.Settings);
    }

    private static LanguageServerSpec ToSpec(LanguageServerContribution server, string folder)
    {
        string Program(string p) => p.StartsWith("./", StringComparison.Ordinal) ? Path.GetFullPath(Path.Combine(folder, p)) : p; // not a path
        var install = server.Install.ToLowerInvariant() switch
        {
            "github" => InstallKind.GitHubRelease,
            "npm" => InstallKind.Npm,
            "dotnet" => InstallKind.DotnetTool,
            "go" => InstallKind.Go,
            _ => InstallKind.System,
        };
        return new LanguageServerSpec(
            server.Id, server.Name.Length == 0 ? server.Id : server.Name, server.Languages, [.. server.Programs.Select(Program)], server.Arguments, install,
            server.Package, server.Repository, server.Assets?.Select(a => new ReleaseAsset(a.Platform, a.Pattern, a.Packing, a.Program)).ToList(), server.Homepage, server.RootMarkers);
    }

    private void Undeclare(Entry entry)
    {
        foreach (var declared in entry.Declared.AsEnumerable().Reverse())
        {
            try
            {
                declared.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn($"Taking back a contribution of {entry.Id} failed: {ex.Message}");
            }
        }

        entry.Declared.Clear();
        LanguageServerCatalog.Unregister(entry.Id);
        Registry.Remove(entry.Id);
    }

    // ── Activation ──

    private void OnDocument(EditorSnapshot document) => ActivateForLanguage(document.LanguageId);

    private void OnActive(EditorSnapshot? document) => ActivateForLanguage(document?.LanguageId);

    private void ActivateForLanguage(string? language)
    {
        if (language is null || _stop.IsCancellationRequested) return;
        var waiting = Installed.Where(i => i.State == ExtensionState.Inactive && i.Manifest!.ActivationLanguages.Contains(language)).Select(i => i.Id).ToList();
        if (waiting.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await _lock.WaitAsync(_stop.Token).ConfigureAwait(false);
                try
                {
                    foreach (var id in waiting)
                    {
                        if (_entries.TryGetValue(id, out var entry) && entry.State == ExtensionState.Inactive) await ActivateAsync(entry, _stop.Token).ConfigureAwait(false);
                    }

                    Publish();
                }
                finally
                {
                    _lock.Release();
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task ActivateForOpenDocumentsAsync(CancellationToken ct)
    {
        try
        {
            foreach (var document in await _host.GetOpenAsync().WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false)) ActivateForLanguage(document.LanguageId);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
        }
    }

    /// <summary>Loads the entry's assembly and runs its <c>ActivateAsync</c>; an extension without code is simply active. Called with the lock held.</summary>
    private async Task ActivateAsync(Entry entry, CancellationToken ct)
    {
        var manifest = entry.Manifest!;
        if (manifest.Main is null)
        {
            entry.State = ExtensionState.Active;
            return;
        }

        var total = Stopwatch.StartNew();
        try
        {
            var mainPath = Path.GetFullPath(Path.Combine(entry.Folder, manifest.Main));
            if (!File.Exists(mainPath)) throw new FileNotFoundException($"{manifest.Main} is not in the extension");
            var (context, instance, loadMs) = await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var context = new ExtensionLoadContext(entry.Folder, mainPath, entry.Id);
                entry.Context = context;
                var assembly = context.LoadMain(mainPath);
                var type = FindMainType(assembly);
                var made = (IWickedExtension)Activator.CreateInstance(type)!;
                return (context, made, clock.Elapsed.TotalMilliseconds);
            }, ct).ConfigureAwait(false);
            entry.Instance = instance;
            entry.LoadMs = loadMs;
            entry.Cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            entry.Api = new WickedApi(entry.Id, entry.Folder, _host, _notices, Registry);
            var clockActivate = Stopwatch.StartNew();
            await Task.Run(() => instance.ActivateAsync(entry.Api, entry.Cts.Token), ct).WaitAsync(ActivationTimeout, ct).ConfigureAwait(false);
            entry.ActivateMs = clockActivate.Elapsed.TotalMilliseconds;
            entry.State = ExtensionState.Active;
            Log.Info($"Extension {entry.Id} activated in {total.Elapsed.TotalMilliseconds:F1} ms (load {loadMs:F1} ms)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DeactivateAsync(entry).ConfigureAwait(false);
            entry.State = ExtensionState.Inactive;
            throw;
        }
        catch (Exception ex)
        {
            var why = ex is TimeoutException ? $"did not finish starting in {ActivationTimeout.TotalSeconds:0} s" : ex.GetBaseException().Message;
            await DeactivateAsync(entry).ConfigureAwait(false);
            entry.State = ExtensionState.Failed;
            entry.Problem = why;
            _host.WriteOutput(WickedApi.ErrorChannel, $"[{entry.Id}] activation failed: {ex}");
            Log.Warn($"Extension {entry.Id} failed to activate: {ex}");
            _notices.Post(new Notice($"ext:{entry.Id}:failed", $"{entry.Title} was switched off: {why}", NoticeKind.Error, "panel/extensions"));
        }
    }

    private static Type FindMainType(Assembly assembly)
    {
        if (assembly.GetCustomAttribute<WickedExtensionAttribute>() is { } named)
        {
            if (!typeof(IWickedExtension).IsAssignableFrom(named.Type)) throw new InvalidOperationException($"{named.Type.Name} does not implement IWickedExtension");
            return named.Type;
        }

        var candidates = assembly.GetTypes().Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IWickedExtension).IsAssignableFrom(t)).ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new InvalidOperationException("The assembly has no type implementing IWickedExtension"),
            _ => throw new InvalidOperationException("The assembly has several IWickedExtension types; name one with [assembly: WickedExtension(typeof(...))]"),
        };
    }

    /// <summary>Stops the extension's code and takes back what it registered; its load context is released for collection.</summary>
    private async Task DeactivateAsync(Entry entry)
    {
        var hadCode = entry.Context is not null;
        try
        {
            entry.Cts?.Cancel();
            switch (entry.Instance)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().AsTask().WaitAsync(DeactivationTimeout).ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Extension {entry.Id} threw while stopping: {ex.Message}");
        }

        entry.Api?.Dispose();
        entry.Api = null;
        entry.Instance = null;
        entry.Cts?.Dispose();
        entry.Cts = null;
        if (entry.Context is { } context)
        {
            _probes[entry.Id] = new WeakReference(context, trackResurrection: true);
            entry.Context = null;
            context.Unload();
        }

        if (hadCode) entry.LoadMs = entry.ActivateMs = 0;
    }

    // ── Enable, disable, uninstall ──

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(id, out var entry) || entry.Manifest is null) return;
            if (enabled)
            {
                _disabled.Remove(id);
            }
            else
            {
                _disabled.Add(id);
                await DeactivateAsync(entry).ConfigureAwait(false);
                Undeclare(entry);
                entry.State = ExtensionState.Disabled;
                entry.Problem = null;
                _notices.Clear($"ext:{id}:failed");
            }

            await Task.Run(WriteDisabled, ct).ConfigureAwait(false);
            if (enabled && entry.State is ExtensionState.Disabled or ExtensionState.Failed)
            {
                Undeclare(entry);
                Declare(entry);
                entry.State = ExtensionState.Inactive;
                entry.Problem = null;
                _notices.Clear($"ext:{id}:failed");
                if (entry.Manifest.ActivationLanguages.Count == 0) await ActivateAsync(entry, ct).ConfigureAwait(false);
            }

            Publish();
        }
        finally
        {
            _lock.Release();
        }

        if (enabled) await ActivateForOpenDocumentsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stops an extension and deletes its folder.</summary>
    public async Task UninstallAsync(string id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_entries.TryGetValue(id, out var entry)) return;
            await DeactivateAsync(entry).ConfigureAwait(false);
            Undeclare(entry);
            _entries.Remove(id);
            _notices.Clear($"ext:{id}:failed");
            await Task.Run(() =>
            {
                if (entry.Manifest is not null && _disabled.Remove(id)) WriteDisabled();
                DeleteFolder(entry.Folder);
            }, ct).ConfigureAwait(false);
            Publish();
        }
        finally
        {
            _lock.Release();
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExtensionInstallException($"The extension's folder could not be removed: {ex.Message}", ex);
        }
    }

    // ── Install ──

    /// <summary>Installs an extension from a folder holding <c>wicked-extension.json</c>; <paramref name="confirm"/> is asked first.</summary>
    public Task<InstalledExtension?> InstallFromFolderAsync(string source, Func<InstallRequest, Task<bool>> confirm, CancellationToken ct = default) =>
        InstallAsync(stage => ExtensionInstaller.StageFolder(source, stage, ct), source, confirm, ct);

    public Task<InstalledExtension?> InstallFromZipAsync(string zip, Func<InstallRequest, Task<bool>> confirm, CancellationToken ct = default) =>
        InstallAsync(stage => ExtensionInstaller.StageZip(zip, stage, ct), zip, confirm, ct);

    /// <summary>Installs the newest release of <c>owner/repo</c> on GitHub that carries an asset named <c>*.wicked.zip</c>, checked against its published sha256.</summary>
    public async Task<InstalledExtension?> InstallFromGitHubAsync(string repository, Func<InstallRequest, Task<bool>> confirm, CancellationToken ct = default)
    {
        var staging = NewStaging();
        try
        {
            var download = await ExtensionInstaller.DownloadReleaseAsync(_http, _githubApi, repository, staging, ct).ConfigureAwait(false);
            return await InstallAsync(stage => ExtensionInstaller.StageZip(download.Path, stage, ct), "github.com/" + repository, confirm, ct, download.Verified).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    private string NewStaging()
    {
        var path = Path.Combine(_folder, ".staging-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not remove {folder}: {ex.Message}");
        }
    }

    /// <summary>Stages the files, reads the manifest, asks, and only then moves the extension into place and starts it.</summary>
    private async Task<InstalledExtension?> InstallAsync(Action<string> stageInto, string source, Func<InstallRequest, Task<bool>> confirm, CancellationToken ct, bool verified = false)
    {
        var staging = NewStaging();
        try
        {
            var stage = Path.Combine(staging, "files");
            var manifest = await Task.Run(() =>
            {
                stageInto(stage);
                return ReadManifest(stage, out var problem, checkFolderName: false) ?? throw new ExtensionInstallException(problem ?? "The extension has no manifest");
            }, ct).ConfigureAwait(false);
            if (!await confirm(new InstallRequest(manifest, source, verified)).ConfigureAwait(false)) return null;

            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_entries.TryGetValue(manifest.FullId, out var running))
                {
                    await DeactivateAsync(running).ConfigureAwait(false);
                    Undeclare(running);
                    _entries.Remove(manifest.FullId);
                }

                await Task.Run(() =>
                {
                    // Every older copy of this extension goes: an upgrade replaces, it does not stack.
                    foreach (var directory in Directory.EnumerateDirectories(_folder).Where(d => !Path.GetFileName(d).StartsWith('.')).ToList())
                    {
                        if (ReadManifest(directory, out _, checkFolderName: false) is { } other && other.FullId == manifest.FullId) DeleteFolder(directory);
                    }

                    Directory.Move(stage, Path.Combine(_folder, manifest.FolderName));
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }

            await LoadAsync(ct).ConfigureAwait(false);
            return Installed.FirstOrDefault(i => i.Id == manifest.FullId);
        }
        finally
        {
            TryDelete(staging);
        }
    }

    /// <summary>Makes the list the panel reads from the entries. The lock must be held.</summary>
    private void Publish()
    {
        var list = new List<InstalledExtension>();
        foreach (var entry in _entries.Values.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new InstalledExtension(entry.Folder, entry.Manifest, entry.State, entry.Problem, entry.LoadMs, entry.ActivateMs, Describe(entry)));
        }

        _snapshot = list;
        Changed?.Invoke();
    }

    private static IReadOnlyList<string> Describe(Entry entry)
    {
        if (entry.Manifest is not { } m) return [];
        var lines = new List<string>();
        var c = m.Contributes;
        void Count(int n, string one, string many)
        {
            if (n > 0) lines.Add($"{n} {(n == 1 ? one : many)}");
        }

        Count(c.Themes.Count, "theme", "themes");
        Count(c.Grammars.Count, "grammar", "grammars");
        Count(c.Snippets.Count, "snippet file", "snippet files");
        Count(c.LanguageServers.Count, "language server", "language servers");
        Count(c.Keybindings.Count, "key binding", "key bindings");
        Count(c.Settings.Count, "setting", "settings");
        if (entry.Api is { } api)
        {
            lines.AddRange(api.CommandTitles.Select(t => "command " + t));
            lines.AddRange(api.PanelIds.Select(p => "panel " + p));
        }

        if (m.Main is not null && lines.Count == 0) lines.Add("code only");
        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_started)
            {
                _host.DocumentOpened -= OnDocument;
                _host.ActiveEditorChanged -= OnActive;
            }

            foreach (var entry in _entries.Values)
            {
                await DeactivateAsync(entry).ConfigureAwait(false);
                Undeclare(entry);
            }
        }
        finally
        {
            _lock.Release();
        }
    }
}
