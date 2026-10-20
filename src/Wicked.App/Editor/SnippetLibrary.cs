using System.Collections.Immutable;
using Avalonia.Threading;
using Wicked.Core;
using Wicked.Editor.Features;

namespace Wicked.App.Editor;

/// <summary>
/// The user's snippets, in VS Code's files: <c>&lt;language&gt;.json</c> for one language and <c>*.code-snippets</c> for
/// any (narrowed by each snippet's "scope"), in the config folder's <c>snippets</c>. Read on a worker, read again when a
/// file there changes.
/// </summary>
public sealed class SnippetLibrary : IDisposable
{
    private readonly string _folder;
    private FileSystemWatcher? _watcher;
    private ImmutableArray<SnippetDefinition> _all = [];

    public SnippetLibrary(string folder) => _folder = folder;

    public string Folder => _folder;

    /// <summary>Read from any thread: the array is swapped whole when a file changes.</summary>
    public ImmutableArray<SnippetDefinition> All => ImmutableInterlocked.InterlockedCompareExchange(ref _all, default, default);

    /// <summary>The snippets for a language: its own file's and the global files' whose scope takes it.</summary>
    public IEnumerable<SnippetDefinition> For(string? languageId) =>
        All.Where(s => s.Scopes.IsDefaultOrEmpty || (languageId is not null && s.Scopes.Contains(languageId, StringComparer.OrdinalIgnoreCase)));

    public async Task LoadAsync()
    {
        ImmutableInterlocked.InterlockedExchange(ref _all, await Task.Run(Read));
        Watch();
    }

    private ImmutableArray<SnippetDefinition> Read()
    {
        if (!Directory.Exists(_folder)) return [];
        var all = ImmutableArray.CreateBuilder<SnippetDefinition>();
        foreach (var file in Directory.EnumerateFiles(_folder))
        {
            var name = Path.GetFileName(file);
            var global = name.EndsWith(".code-snippets", StringComparison.OrdinalIgnoreCase);
            if (!global && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var definitions = Snippets.LoadJson(File.ReadAllText(file));
                // A language file's snippets belong to that language, whatever their own scope says.
                var language = Path.GetFileNameWithoutExtension(name);
                all.AddRange(global ? definitions : definitions.Select(d => d with { Scopes = [language] }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Warn($"Snippets in {name} could not be read: {ex.Message}");
            }
        }

        return all.ToImmutable();
    }

    private void Watch()
    {
        if (_watcher is not null) return;
        try
        {
            Directory.CreateDirectory(_folder);
            _watcher = new FileSystemWatcher(_folder) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() => _ = Task.Run(() => ImmutableInterlocked.InterlockedExchange(ref _all, Read())));
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, e) => changed(this, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Cannot watch the snippets folder: {ex.Message}");
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
