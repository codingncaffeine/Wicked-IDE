using System.Text.Json;

namespace Wicked.Core.Extensions;

/// <summary>
/// What the extension host needs from the application around it. The app implements it over the window (commands, editors,
/// panels, settings); tests implement it over nothing. Every member may be called from any thread, and the asynchronous ones
/// do their window work on the interface thread themselves.
/// </summary>
public interface IHostBridge
{
    // ── Commands ──

    /// <summary>Adds a command to the palette and the key map. Throws <see cref="InvalidOperationException"/> when <paramref name="id"/> is taken.</summary>
    IDisposable AddCommand(string id, string category, string title, string? keys, Func<Task> run);

    IDisposable AddKeybinding(string commandId, string keys);

    Task<bool> RunCommandAsync(string id);

    IReadOnlyList<string> CommandIds();

    // ── Output ──

    void WriteOutput(string channel, string line);

    void ClearOutput(string channel);

    Task ShowOutputAsync(string channel);

    // ── Settings ──

    /// <summary>The user's value for a key (Wicked's own settings included), or null when settings.json says nothing and the built-in default applies.</summary>
    Task<JsonElement?> GetSettingAsync(string key);

    Task SetSettingAsync(string key, JsonElement value);

    /// <summary>Raised with the keys that changed when settings.json is applied.</summary>
    event Action<IReadOnlyList<string>>? SettingsChanged;

    // ── Workspace and editors ──

    Task<string?> GetRootAsync();

    Task OpenFileAsync(string path, int? line, int? column);

    Task<EditorSnapshot?> GetActiveAsync();

    Task<IReadOnlyList<EditorSnapshot>> GetOpenAsync();

    Task<EditorSnapshot?> GetDocumentAsync(string documentId);

    Task<bool> ReplaceAsync(string documentId, TextRange range, string text, long? expectedVersion);

    Task<bool> InsertAtCaretsAsync(string text);

    Task SetSelectionsAsync(string documentId, IReadOnlyList<TextRange> selections);

    /// <summary>Marks for one owner's layer; the app prefixes the layer with the owner so two extensions never meet.</summary>
    Task SetDecorationsAsync(string owner, string documentId, string layer, Decorations marks, long? forVersion);

    Task ClearDecorationsAsync(string owner, string documentId, string? layer);

    event Action<EditorSnapshot>? DocumentOpened;

    event Action<EditorSnapshot>? DocumentSaved;

    event Action<EditorSnapshot>? DocumentClosed;

    event Action<EditorSnapshot?>? ActiveEditorChanged;

    // ── Panels ──

    Task AddBottomTabAsync(string owner, string id, string title, Func<object> createView);

    Task AddSidePanelAsync(string owner, string id, string title, string icon, Func<object> createView);

    Task ShowPanelAsync(string owner, string id);

    Task RemovePanelAsync(string owner, string id);

    // ── Registries ──

    IDisposable AddSnippetFile(string path, string? languageId);

    IDisposable AddThemeFile(string path, string? label);

    IDisposable AddGrammar(GrammarSpec grammar);
}
