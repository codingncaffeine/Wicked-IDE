using System.Collections.Immutable;
using Wicked.Core;
using Wicked.Editor.Features;

namespace Wicked.App.Editor;

/// <summary>Snippet files that extensions add, kept beside the user's own folder.</summary>
public sealed partial class SnippetLibrary
{
    private readonly Lock _addedGate = new();
    private ImmutableArray<(string Path, string? Language)> _added = [];

    /// <summary>
    /// Adds a snippet file (VS Code's format) from outside the snippets folder. <paramref name="languageId"/> scopes a file
    /// whose snippets name none. The library reads it again on a worker; disposing the handle takes it back.
    /// </summary>
    public IDisposable AddFile(string path, string? languageId)
    {
        var entry = (path, languageId);
        lock (_addedGate) _added = _added.Add(entry);
        Reload();
        return new Removal(this, entry);
    }

    private sealed class Removal(SnippetLibrary library, (string Path, string? Language) entry) : IDisposable
    {
        public void Dispose()
        {
            lock (library._addedGate) library._added = library._added.Remove(entry);
            library.Reload();
        }
    }

    private void Reload() => _ = Task.Run(() => ImmutableInterlocked.InterlockedExchange(ref _all, Read()));

    private IEnumerable<SnippetDefinition> ReadAdded()
    {
        ImmutableArray<(string Path, string? Language)> files;
        lock (_addedGate) files = _added;
        foreach (var (path, language) in files)
        {
            IReadOnlyList<SnippetDefinition> definitions;
            try
            {
                definitions = Snippets.LoadJson(File.ReadAllText(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Log.Warn($"Snippets in {path} could not be read: {ex.Message}");
                continue;
            }

            foreach (var d in definitions) yield return language is not null && d.Scopes.IsDefaultOrEmpty ? d with { Scopes = [language] } : d;
        }
    }
}
