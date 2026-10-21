using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using Wicked.Core;
using Wicked.Core.Extensions;
using Wicked.Core.Files;

namespace Wicked.App.Extensions;

/// <summary>
/// init.csx (D16 layer 4): the user's own C#, run at start on a worker with the extension API as <c>wicked</c>. It lives in
/// the profile's config folder. Compiled once and kept by hash in the cache folder, so a start with an unchanged script
/// loads a finished assembly and never starts the compiler. Errors go to the Output channel "init.csx", with the line, and
/// to a notice. Running it again first takes back everything the last run registered.
/// </summary>
public sealed class InitScript : IAsyncDisposable
{
    public const string ChannelName = "init.csx";

    private const string NoticeKey = "init.csx";
    private static readonly TimeSpan RunLimit = TimeSpan.FromSeconds(30);

    private readonly WickedPaths _paths;
    private readonly IHostBridge _host;
    private readonly NoticeBoard _notices;
    private readonly ExtensionRegistry _registry;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WickedApi? _api;
    private AssemblyLoadContext? _context;

    public InitScript(WickedPaths paths, IHostBridge host, NoticeBoard notices, ExtensionRegistry registry)
    {
        _paths = paths;
        _host = host;
        _notices = notices;
        _registry = registry;
        _host.DocumentSaved += OnSaved;
    }

    /// <summary>Saving init.csx in the editor runs it again.</summary>
    private void OnSaved(EditorSnapshot document)
    {
        if (string.Equals(document.Path, Path, StringComparison.Ordinal)) _ = RunAsync();
    }

    public string Path => System.IO.Path.Combine(_paths.Config, "init.csx");

    private string CacheFolder => System.IO.Path.Combine(_paths.Cache, "init-csx");

    /// <summary>Compilations this process has made (tests watch it to see that the cache was used).</summary>
    public int Compilations { get; private set; }

    /// <summary>Whether the last run finished without an error (true too when there is no script).</summary>
    public bool Succeeded { get; private set; } = true;

    /// <summary>Takes back what the last run registered, then runs the script again.</summary>
    public Task RunAsync(CancellationToken ct = default) => Task.Run(async () =>
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
            _notices.Clear(NoticeKey);
            if (!File.Exists(Path))
            {
                Succeeded = true;
                return;
            }

            await RunCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }, ct);

    private async Task RunCoreAsync(CancellationToken ct)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail($"init.csx could not be read: {ex.Message}", [ex.Message]);
            return;
        }

        if (IsBlank(text))
        {
            Succeeded = true;
            return;
        }

        var key = Hash(text);
        var cached = System.IO.Path.Combine(CacheFolder, key + ".dll");
        byte[]? assembly = null;
        if (File.Exists(cached))
        {
            try
            {
                assembly = await File.ReadAllBytesAsync(cached, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        if (assembly is null)
        {
            var (compiled, errors) = Compile(text);
            if (compiled is null)
            {
                Fail($"init.csx has {errors.Count} error{(errors.Count == 1 ? "" : "s")}: {errors[0]}", errors);
                return;
            }

            assembly = compiled;
            try
            {
                Directory.CreateDirectory(CacheFolder);
                var temp = cached + "." + Guid.NewGuid().ToString("N")[..6] + ".tmp";
                await File.WriteAllBytesAsync(temp, assembly, ct).ConfigureAwait(false);
                File.Move(temp, cached, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"The compiled init.csx could not be cached: {ex.Message}");
            }
        }

        var api = new WickedApi("init.csx", System.IO.Path.GetDirectoryName(Path)!, _host, _notices, _registry);
        var context = new AssemblyLoadContext("init.csx", isCollectible: true);
        try
        {
            var factory = FindFactory(context, assembly);
            if (factory is null && File.Exists(cached))
            {
                // A cached build from a different layout: compile afresh.
                File.Delete(cached);
                context.Unload();
                api.Dispose();
                await RunCoreAsync(ct).ConfigureAwait(false);
                return;
            }

            _api = api;
            _context = context;
            var task = (Task<object?>)factory!.Invoke(null, [new object?[] { new InitGlobals(api), null }])!;
            await task.WaitAsync(RunLimit, ct).ConfigureAwait(false);
            Succeeded = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            var line = inner is TimeoutException ? $"init.csx did not finish in {RunLimit.TotalSeconds:0} s" : $"init.csx failed: {inner.Message}";
            Fail(line, [inner.ToString()]);
            await StopAsync().ConfigureAwait(false);
        }
    }

    private static MethodInfo? FindFactory(AssemblyLoadContext context, byte[] assembly)
    {
        using var stream = new MemoryStream(assembly);
        var loaded = context.LoadFromStream(stream);
        var submission = loaded.GetTypes().FirstOrDefault(t => t.Name.StartsWith("Submission#", StringComparison.Ordinal));
        return submission?.GetMethod("<Factory>", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
    }

    /// <summary>Roslyn lives behind this call: a start with a cached build never reaches it.</summary>
    private (byte[]? Assembly, IReadOnlyList<string> Errors) Compile(string text)
    {
        Compilations++;
        return InitScriptCompiler.Compile(text, Path);
    }

    /// <summary>Only blank lines and line comments (the starter file): nothing to compile, and no reason to start Roslyn.</summary>
    internal static bool IsBlank(string text) => text.Split('\n').All(line => line.AsSpan().Trim().IsEmpty || line.AsSpan().TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static string Hash(string text)
    {
        // The compiled build depends on the script and on the API it was compiled against.
        var api = typeof(IWickedApi).Assembly.ManifestModule.ModuleVersionId;
        var host = typeof(InitScript).Assembly.ManifestModule.ModuleVersionId;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{api}|{host}|{text}")))[..32];
    }

    private void Fail(string summary, IReadOnlyList<string> detail)
    {
        Succeeded = false;
        _host.WriteOutput(ChannelName, $"--- {DateTime.Now:HH:mm:ss} ---");
        foreach (var line in detail) _host.WriteOutput(ChannelName, line);
        _notices.Post(new Notice(NoticeKey, summary, NoticeKind.Error, "output/" + ChannelName));
    }

    private Task StopAsync()
    {
        _api?.Dispose();
        _api = null;
        _context?.Unload();
        _context = null;
        return Task.CompletedTask;
    }

    /// <summary>The script file, started with a commented example when it does not exist yet.</summary>
    public async Task<string> EnsureStarterAsync(CancellationToken ct = default)
    {
        if (!File.Exists(Path))
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await TextFile.WriteAsync(Path, Starter, FileEncoding.Utf8, ct).ConfigureAwait(false);
        }

        return Path;
    }

    public const string Starter = """
        // init.csx: C# that runs each time Wicked starts, and again when you run "Reload init.csx".
        // `wicked` is the extension API (see docs/extensions.md). These namespaces are already imported:
        // System, System.IO, System.Linq, System.Threading.Tasks, Avalonia.Controls and Wicked.Core.Extensions.
        // Everything you register here is taken back when the script is reloaded.
        //
        // A command in the palette with a shortcut:
        //
        // wicked.Commands.Register("init.hello", "Say Hello", () => wicked.Notices.Post("Hello from init.csx", NoticeLevel.Success), "Ctrl+Alt+H", "My Commands");
        //
        // React to events (these run on a worker thread, never on the interface thread):
        //
        // wicked.Events.DocumentSaved += e => wicked.Output.Channel("init.csx").WriteLine($"saved {e.Document.Path}");
        //
        // Insert text at the carets:
        //
        // wicked.Commands.Register("init.stamp", "Insert Date", async ct => await wicked.Editors.InsertAtCaretsAsync(DateTime.Now.ToString("yyyy-MM-dd")), null, "My Commands");

        """;

    public async ValueTask DisposeAsync()
    {
        _host.DocumentSaved -= OnSaved;
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }
}
