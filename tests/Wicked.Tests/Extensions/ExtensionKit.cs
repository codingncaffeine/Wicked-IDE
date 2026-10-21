using System.Collections.Immutable;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Wicked.Core.Extensions;

namespace Wicked.Tests.Extensions;

/// <summary>A folder under the worktree's scratch/ that is removed afterwards (tests never write into the user's profile or /tmp).</summary>
internal sealed class ScratchFolder : IDisposable
{
    public ScratchFolder()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "Wicked.slnx"))) root = Path.GetDirectoryName(root);
        Root = Path.Combine(root ?? Path.GetTempPath(), "scratch", "test-ext", Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Sub(string name)
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Compiles a tiny extension with Roslyn and lays out a package folder for it, the way a real author's build would.</summary>
internal static class ExtensionKit
{
    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        var trusted = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator);
        var list = trusted.Where(p => Path.GetFileName(p) is { } n && (n.StartsWith("System.", StringComparison.Ordinal) || n is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();
        list.Add(MetadataReference.CreateFromFile(typeof(IWickedApi).Assembly.Location));
        list.Add(MetadataReference.CreateFromFile(typeof(Avalonia.Controls.Control).Assembly.Location));
        list.Add(MetadataReference.CreateFromFile(typeof(Avalonia.AvaloniaObject).Assembly.Location));
        list.Add(MetadataReference.CreateFromFile(typeof(Avalonia.Application).Assembly.Location));
        return [.. list];
    });

    public const string HelloSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using Wicked.Core.Extensions;

        [assembly: WickedExtension(typeof(Hello.HelloExtension))]

        namespace Hello
        {
            public sealed class HelloExtension : IWickedExtension, IAsyncDisposable
            {
                public static int Disposed;

                public Task ActivateAsync(IWickedApi api, CancellationToken ct)
                {
                    api.Commands.Register("hello.say", "Say Hello", () => api.Notices.Post("Hello from the extension", NoticeLevel.Success, "hello"), "Ctrl+Alt+H", "Hello");
                    api.Events.DocumentOpened += e =>
                        api.Editors.SetDecorationsAsync(e.Document.Id, "hello",
                            new Decorations(Ranges: new[] { new RangeMark(0, Math.Min(5, e.Document.Text.Length), MarkStyle.Warning) }));
                    return Task.CompletedTask;
                }

                public ValueTask DisposeAsync()
                {
                    Disposed++;
                    return default;
                }
            }
        }
        """;

    /// <summary>Compiles <paramref name="source"/> to <c>folder/name.dll</c>.</summary>
    public static void Compile(string source, string folder, string name)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(name, [tree], References.Value, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, nullableContextOptions: NullableContextOptions.Enable));
        var path = Path.Combine(folder, name + ".dll");
        var result = compilation.Emit(path);
        if (!result.Success) throw new InvalidOperationException(string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
    }

    public static string Manifest(string id = "hello", string version = "1.0.0", string publisher = "acme", string? main = "Hello.dll", string engine = "^1.0", string? contributes = null, string[]? activation = null) =>
        $$"""
        {
          "id": "{{id}}", "name": "{{id}} extension", "version": "{{version}}", "publisher": "{{publisher}}",
          "description": "A test extension.", "engines": { "wicked": "{{engine}}" },
          {{(main is null ? "" : $"\"main\": \"{main}\",")}}
          {{(activation is null ? "" : $"\"activationEvents\": [{string.Join(", ", activation.Select(a => $"\"{a}\""))}],")}}
          "contributes": {{contributes ?? "{}"}}
        }
        """;

    /// <summary>A source folder holding a compiled Hello extension (and its manifest), ready for "Install from folder".</summary>
    public static string HelloFolder(ScratchFolder scratch, string folderName = "hello-src", string version = "1.0.0", string? source = null, string? contributes = null, string[]? activation = null, string id = "hello")
    {
        var folder = scratch.Sub(folderName);
        Compile(source ?? HelloSource, folder, "Hello");
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), Manifest(id: id, version: version, contributes: contributes, activation: activation));
        return folder;
    }

    public static string Zip(string folder, string zipPath, bool wrapInFolder = false)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            zip.CreateEntryFromFile(file, wrapInFolder ? "pack/" + relative : relative);
        }

        return zipPath;
    }

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static Task<bool> Yes(InstallRequest _) => Task.FromResult(true);
}

/// <summary>A host with nothing behind it: it records what an extension registers and lets the test raise events.</summary>
internal sealed class FakeBridge : IHostBridge
{
    private readonly Lock _gate = new();

    public Dictionary<string, (string Title, string? Keys, Func<Task> Run)> Commands { get; } = [];

    public List<string> Keybindings { get; } = [];

    public List<string> Output { get; } = [];

    public List<string> Themes { get; } = [];

    public List<string> Snippets { get; } = [];

    public List<GrammarSpec> Grammars { get; } = [];

    public List<(string Owner, string Document, string Layer, Decorations Marks)> Decorated { get; } = [];

    public Dictionary<string, JsonElement> Settings { get; } = [];

    public List<EditorSnapshot> Open { get; } = [];

    public IDisposable AddCommand(string id, string category, string title, string? keys, Func<Task> run)
    {
        lock (_gate)
        {
            if (Commands.ContainsKey(id)) throw new InvalidOperationException($"The command {id} exists");
            Commands[id] = (category.Length > 0 ? $"{category}: {title}" : title, keys, run);
        }

        return new Undo(() =>
        {
            lock (_gate) Commands.Remove(id);
        });
    }

    public IDisposable AddKeybinding(string commandId, string keys)
    {
        lock (_gate) Keybindings.Add($"{commandId}={keys}");
        return new Undo(() =>
        {
            lock (_gate) Keybindings.Remove($"{commandId}={keys}");
        });
    }

    public async Task<bool> RunCommandAsync(string id)
    {
        Func<Task> run;
        lock (_gate)
        {
            if (!Commands.TryGetValue(id, out var command)) return false;
            run = command.Run;
        }

        await run().ConfigureAwait(false);
        return true;
    }

    public IReadOnlyList<string> CommandIds()
    {
        lock (_gate) return [.. Commands.Keys];
    }

    public void WriteOutput(string channel, string line)
    {
        lock (_gate) Output.Add($"{channel}: {line}");
    }

    public void ClearOutput(string channel)
    {
    }

    public Task ShowOutputAsync(string channel) => Task.CompletedTask;

    public Task<JsonElement?> GetSettingAsync(string key)
    {
        lock (_gate) return Task.FromResult<JsonElement?>(Settings.TryGetValue(key, out var v) ? v : null);
    }

    public Task SetSettingAsync(string key, JsonElement value)
    {
        lock (_gate) Settings[key] = value;
        SettingsChanged?.Invoke([key]);
        return Task.CompletedTask;
    }

    public event Action<IReadOnlyList<string>>? SettingsChanged;

    public Task<string?> GetRootAsync() => Task.FromResult<string?>(null);

    public Task OpenFileAsync(string path, int? line, int? column) => Task.CompletedTask;

    public Task<EditorSnapshot?> GetActiveAsync() => Task.FromResult(Open.LastOrDefault());

    public Task<IReadOnlyList<EditorSnapshot>> GetOpenAsync()
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<EditorSnapshot>>([.. Open]);
    }

    public Task<EditorSnapshot?> GetDocumentAsync(string documentId) => Task.FromResult(Open.FirstOrDefault(d => d.Id == documentId));

    public Task<bool> ReplaceAsync(string documentId, TextRange range, string text, long? expectedVersion) => Task.FromResult(false);

    public Task<bool> InsertAtCaretsAsync(string text) => Task.FromResult(false);

    public Task SetSelectionsAsync(string documentId, IReadOnlyList<TextRange> selections) => Task.CompletedTask;

    public Task SetDecorationsAsync(string owner, string documentId, string layer, Decorations marks, long? forVersion)
    {
        lock (_gate) Decorated.Add((owner, documentId, layer, marks));
        return Task.CompletedTask;
    }

    public Task ClearDecorationsAsync(string owner, string documentId, string? layer) => Task.CompletedTask;

    public event Action<EditorSnapshot>? DocumentOpened;

    public event Action<EditorSnapshot>? DocumentSaved;

    public event Action<EditorSnapshot>? DocumentClosed;

    public event Action<EditorSnapshot?>? ActiveEditorChanged;

    public void RaiseOpened(EditorSnapshot document)
    {
        lock (_gate) Open.Add(document);
        DocumentOpened?.Invoke(document);
        ActiveEditorChanged?.Invoke(document);
    }

    public void RaiseSaved(EditorSnapshot document) => DocumentSaved?.Invoke(document);

    public void RaiseClosed(EditorSnapshot document) => DocumentClosed?.Invoke(document);

    public int HandlerCount => (DocumentOpened?.GetInvocationList().Length ?? 0) + (DocumentSaved?.GetInvocationList().Length ?? 0) + (ActiveEditorChanged?.GetInvocationList().Length ?? 0);

    public Task AddBottomTabAsync(string owner, string id, string title, Func<object> createView) => Task.CompletedTask;

    public Task AddSidePanelAsync(string owner, string id, string title, string icon, Func<object> createView) => Task.CompletedTask;

    public Task ShowPanelAsync(string owner, string id) => Task.CompletedTask;

    public Task RemovePanelAsync(string owner, string id) => Task.CompletedTask;

    public IDisposable AddSnippetFile(string path, string? languageId)
    {
        lock (_gate) Snippets.Add(path);
        return new Undo(() =>
        {
            lock (_gate) Snippets.Remove(path);
        });
    }

    public IDisposable AddThemeFile(string path, string? label)
    {
        lock (_gate) Themes.Add(path);
        return new Undo(() =>
        {
            lock (_gate) Themes.Remove(path);
        });
    }

    public IDisposable AddGrammar(GrammarSpec grammar)
    {
        lock (_gate) Grammars.Add(grammar);
        return new Undo(() =>
        {
            lock (_gate) Grammars.Remove(grammar);
        });
    }

    private sealed class Undo(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}

/// <summary>A GitHub stand-in on loopback: one repository whose latest release carries the given assets.</summary>
internal sealed class FakeGitHub : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, byte[]> _files = [];
    private readonly string _release;

    public FakeGitHub(string repository, string assetName, byte[] zip, string? digest, string? tag = "v1.0.0", bool withAsset = true)
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _files["/download/" + assetName] = zip;
        var asset = withAsset
            ? JsonSerializer.Serialize(new { name = assetName, browser_download_url = new Uri(BaseAddress, "download/" + assetName).ToString(), digest })
            : "";
        _release = $"{{\"tag_name\":\"{tag}\",\"assets\":[{asset}]}}";
        Repository = repository;
        _listener.Prefixes.Add(BaseAddress.ToString());
        _listener.Start();
        _ = Task.Run(Serve);
    }

    public Uri BaseAddress { get; }

    public string Repository { get; }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            var path = context.Request.Url!.AbsolutePath;
            byte[] body;
            if (path == $"/repos/{Repository}/releases/latest")
            {
                body = Encoding.UTF8.GetBytes(_release);
                context.Response.ContentType = "application/json";
            }
            else if (_files.TryGetValue(path, out var file))
            {
                body = file;
            }
            else
            {
                context.Response.StatusCode = 404;
                body = [];
            }

            await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
    }
}

internal static class GcProbe
{
    /// <summary>Collects until the weak reference dies or the time is up.</summary>
    public static async Task<bool> CollectedAsync(WeakReference? probe, int seconds = 20)
    {
        if (probe is null) return true;
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (probe.IsAlive && DateTime.UtcNow < until)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(50);
        }

        return !probe.IsAlive;
    }
}
