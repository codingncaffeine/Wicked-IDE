using Wicked.Core.Lsp;

namespace Wicked.Core.Extensions;

/// <summary>
/// The version of the extension API. An extension's manifest asks for a range of it (<c>"engines": { "wicked": "^1.0" }</c>);
/// a minor version only ever adds members, a major version may remove them.
/// </summary>
public static class WickedApiVersion
{
    public static readonly Version Current = new(1, 0);

    public const string Text = "1.0";
}

/// <summary>
/// What an extension's main type implements. <see cref="ActivateAsync"/> runs on a worker thread once the extension is
/// enabled and one of its activation events has happened; it registers what the extension offers through the api it is given and returns.
/// If the type also implements <see cref="IAsyncDisposable"/> or <see cref="IDisposable"/>
/// it is disposed when the extension is disabled or uninstalled, after everything it registered has been taken back.
/// </summary>
public interface IWickedExtension
{
    Task ActivateAsync(IWickedApi api, CancellationToken ct);
}

/// <summary>Names the main type of an extension assembly: <c>[assembly: WickedExtension(typeof(MyExtension))]</c>. Without it the assembly's single <see cref="IWickedExtension"/> is used.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class WickedExtensionAttribute(Type type) : Attribute
{
    public Type Type { get; } = type;
}

/// <summary>
/// Everything an extension can do. Calls may be made from any thread and none of them blocks the user interface: the ones
/// that touch the window are asynchronous and do their window work on the interface thread themselves. Handlers an
/// extension registers (commands, events) are run on worker threads, never on the interface thread. Whatever an extension
/// registers is taken back when it is disabled or uninstalled.
/// </summary>
public interface IWickedApi
{
    /// <summary>The API version this host speaks.</summary>
    Version ApiVersion { get; }

    /// <summary>The extension's id (<c>publisher.name</c>), or <c>init.csx</c> for the user's script.</summary>
    string ExtensionId { get; }

    /// <summary>The folder the extension was installed to (the script's folder for init.csx); read its own files from here.</summary>
    string ExtensionFolder { get; }

    ICommandApi Commands { get; }

    INoticeApi Notices { get; }

    IOutputApi Output { get; }

    ISettingsApi Settings { get; }

    IWorkspaceApi Workspace { get; }

    IEditorApi Editors { get; }

    IEventApi Events { get; }

    IViewHost Views { get; }

    IContributionApi Contributions { get; }
}

/// <summary>Commands appear in the palette ("Category: Title"), run from key bindings, and can be run by other extensions.</summary>
public interface ICommandApi
{
    /// <summary>
    /// Registers a command. <paramref name="keys"/> is a default shortcut written as VS Code writes them (<c>Ctrl+K Ctrl+H</c>),
    /// which the user's keybindings.json can override. Throws when another extension already owns <paramref name="id"/>.
    /// The returned handle takes the command back.
    /// </summary>
    IDisposable Register(string id, string title, Func<CancellationToken, Task> run, string? keys = null, string category = "");

    /// <summary>A synchronous convenience for commands that do small work.</summary>
    IDisposable Register(string id, string title, Action run, string? keys = null, string category = "");

    /// <summary>Adds a key binding for a command (its own or another's) on top of its defaults; the handle removes it.</summary>
    IDisposable BindKeys(string commandId, string keys);

    /// <summary>Runs a command by id; false when there is none.</summary>
    Task<bool> ExecuteAsync(string commandId, CancellationToken ct = default);

    /// <summary>The ids of the commands registered now.</summary>
    Task<IReadOnlyList<string>> ListAsync();
}

public enum NoticeLevel
{
    /// <summary>A passing word; clears itself.</summary>
    Info,

    /// <summary>Something finished well; clears itself.</summary>
    Success,

    Warning,

    Error,
}

/// <summary>The footer's notices.</summary>
public interface INoticeApi
{
    /// <summary>
    /// Posts a notice. A later notice with the same <paramref name="key"/> from this extension replaces it. A
    /// <paramref name="route"/> (a command-less place such as <c>panel/extensions</c> or <c>output/Name</c>) is where a click goes.
    /// </summary>
    void Post(string text, NoticeLevel level = NoticeLevel.Info, string? key = null, string? route = null);

    /// <summary>Takes a notice this extension posted back.</summary>
    void Clear(string key);
}

/// <summary>A channel of the Output panel.</summary>
public interface IOutputChannel
{
    string Name { get; }

    void WriteLine(string line);

    void Clear();

    /// <summary>Opens the Output panel on this channel.</summary>
    Task ShowAsync();
}

public interface IOutputApi
{
    /// <summary>The channel called <paramref name="name"/>, made on first use.</summary>
    IOutputChannel Channel(string name);
}

/// <summary>settings.json, by key as VS Code names them (<c>editor.fontSize</c>), and the extension's own defaults.</summary>
public interface ISettingsApi
{
    /// <summary>
    /// The value of a setting: the user's, else the default the extension declared in its manifest, else
    /// <paramref name="fallback"/>. Also reads Wicked's own settings.
    /// </summary>
    Task<T?> GetAsync<T>(string key, T? fallback = default);

    /// <summary>Writes a setting into the user's settings.json (and applies it).</summary>
    Task SetAsync<T>(string key, T value);

    /// <summary>Raised on a worker thread with the keys that changed whenever settings.json changes.</summary>
    event Action<IReadOnlyList<string>>? Changed;
}

/// <summary>The open folder and the files in it.</summary>
public interface IWorkspaceApi
{
    /// <summary>The open folder, or null.</summary>
    Task<string?> GetRootAsync();

    /// <summary>Opens a file in an editor; <paramref name="line"/> and <paramref name="column"/> are zero-based.</summary>
    Task OpenFileAsync(string path, int? line = null, int? column = null);
}

/// <summary>[Start, End) of a document's text in UTF-16 code units; an empty range is a caret.</summary>
public readonly record struct TextRange(int Start, int End)
{
    public int Length => End - Start;

    public bool IsEmpty => Start == End;
}

/// <summary>An open document as the editor stood at one moment: an immutable copy an extension can read at leisure.</summary>
/// <param name="Id">The document's id: its path, or <c>untitled:N</c> for a document with none. Pass it back to <see cref="IEditorApi"/>.</param>
/// <param name="Path">The file's path, or null for an unsaved document.</param>
/// <param name="LanguageId">The language id ("csharp"), or null for plain text.</param>
/// <param name="Version">Goes up with every edit; a result computed for an older version is carried forward to the newer text.</param>
/// <param name="Text">The whole text.</param>
/// <param name="Selections">The selections, the primary first.</param>
/// <param name="IsDirty">Whether the document has unsaved changes.</param>
public sealed record EditorSnapshot(string Id, string? Path, string? LanguageId, long Version, string Text, IReadOnlyList<TextRange> Selections, bool IsDirty);

/// <summary>How a range is drawn (the editor's own styles, so themes colour them).</summary>
public enum MarkStyle
{
    Error,
    Warning,
    Info,
    Hint,
    Unnecessary,
    Deprecated,
    Highlight,
    Inserted,
    Removed,
    Link,
}

/// <summary>How a whole line is drawn.</summary>
public enum LineMarkStyle
{
    Inserted,
    Removed,
    Highlight,
}

/// <summary>A mark in the gutter beside a line.</summary>
public enum GutterMarkKind
{
    Added,
    Modified,
    Deleted,
    Bookmark,
}

/// <summary>Text drawn inside a line that is not in the document.</summary>
public enum InlineMarkStyle
{
    /// <summary>A small label on a tinted pill.</summary>
    Hint,

    /// <summary>Faded text in the code's font.</summary>
    Ghost,
}

public readonly record struct RangeMark(int Start, int End, MarkStyle Style);

/// <summary>A line's background; <c>Line</c> is zero-based.</summary>
public readonly record struct LineMark(int Line, LineMarkStyle Style);

public readonly record struct GutterMark(int Line, GutterMarkKind Kind);

public readonly record struct InlineMark(int Offset, string Text, InlineMarkStyle Style = InlineMarkStyle.Hint);

/// <summary>One extension's marks on one document, by layer. Every mark follows its text through edits until the layer is set again.</summary>
public sealed record Decorations(
    IReadOnlyList<RangeMark>? Ranges = null,
    IReadOnlyList<LineMark>? Lines = null,
    IReadOnlyList<GutterMark>? Gutter = null,
    IReadOnlyList<InlineMark>? Inline = null)
{
    public static readonly Decorations None = new();
}

/// <summary>The editors. A document may be open in none, one or several views; these calls work on the document.</summary>
public interface IEditorApi
{
    /// <summary>The active editor's document, or null when none is open.</summary>
    Task<EditorSnapshot?> GetActiveAsync();

    /// <summary>The documents open now.</summary>
    Task<IReadOnlyList<EditorSnapshot>> GetOpenAsync();

    /// <summary>A document by id, or null.</summary>
    Task<EditorSnapshot?> GetAsync(string documentId);

    /// <summary>
    /// Replaces [<paramref name="range"/>) of a document, as one undo step. Returns false when the document is closed, is
    /// read-only, or changed since <paramref name="expectedVersion"/> (when given), so a slow extension never edits text
    /// that moved under it.
    /// </summary>
    Task<bool> ReplaceAsync(string documentId, TextRange range, string text, long? expectedVersion = null);

    /// <summary>Inserts text at every caret of the active editor (a selection is replaced), as one undo step.</summary>
    Task<bool> InsertAtCaretsAsync(string text);

    /// <summary>Selects ranges in a document (the first becomes the primary selection).</summary>
    Task SetSelectionsAsync(string documentId, IReadOnlyList<TextRange> selections);

    /// <summary>
    /// Replaces this extension's marks in the layer called <paramref name="layer"/> on a document. The marks were worked
    /// out for <paramref name="forVersion"/> of the text (carried forward through newer edits); null means the current text.
    /// </summary>
    Task SetDecorationsAsync(string documentId, string layer, Decorations marks, long? forVersion = null);

    /// <summary>Takes this extension's marks off a document (every layer when <paramref name="layer"/> is null).</summary>
    Task ClearDecorationsAsync(string documentId, string? layer = null);
}

/// <summary>What the events say about a document; the snapshot is taken when the event is raised.</summary>
public sealed record DocumentEvent(EditorSnapshot Document);

/// <summary>Things that happen. Handlers run on worker threads, one event at a time per extension, and an exception in one is logged and goes no further.</summary>
public interface IEventApi
{
    event Action<DocumentEvent>? DocumentOpened;

    event Action<DocumentEvent>? DocumentSaved;

    /// <summary>The document is gone; its text is whatever it last was.</summary>
    event Action<DocumentEvent>? DocumentClosed;

    /// <summary>The active editor changed; null when the last one closed.</summary>
    event Action<DocumentEvent?>? ActiveEditorChanged;
}

/// <summary>
/// Adds panels of the extension's own. The views are Avalonia controls (<c>Avalonia.Controls.Control</c>) made on the
/// interface thread by the factory given here, which should only build the control and return; do work on workers and
/// post results back with <c>Dispatcher.UIThread.Post</c>. This interface keeps extensions from touching the window itself.
/// </summary>
public interface IViewHost
{
    /// <summary>A tab in the panel under the editors; <paramref name="createView"/> returns an Avalonia <c>Control</c>.</summary>
    Task AddBottomTabAsync(string id, string title, Func<object> createView);

    /// <summary>A button on the rail and a side panel; <paramref name="icon"/> is the name of a built-in icon ("puzzle", "boxes").</summary>
    Task AddSidePanelAsync(string id, string title, string icon, Func<object> createView);

    /// <summary>Shows a panel this extension added.</summary>
    Task ShowAsync(string id);

    /// <summary>Takes a panel back.</summary>
    Task RemoveAsync(string id);
}

/// <summary>A TextMate grammar: <c>Path</c> is the grammar file (.tmLanguage.json or .plist) relative to the extension.</summary>
/// <param name="LanguageId">The language id the grammar serves ("foolang").</param>
/// <param name="Scope">The grammar's scope name ("source.foo").</param>
/// <param name="Path">The grammar file.</param>
/// <param name="Extensions">File extensions with the dot (".foo").</param>
/// <param name="FileNames">Whole file names ("Foofile").</param>
/// <param name="Configuration">An optional language configuration (comments, brackets), relative to the extension.</param>
public sealed record GrammarSpec(string LanguageId, string Scope, string Path, IReadOnlyList<string>? Extensions = null, IReadOnlyList<string>? FileNames = null, string? Configuration = null);

/// <summary>Things an extension adds to the registries the IDE reads: servers, snippets, themes, grammars.</summary>
public interface IContributionApi
{
    /// <summary>A language server the IDE can start and install (it takes the place ahead of the built-in ones for its languages).</summary>
    IDisposable AddLanguageServer(LanguageServerSpec spec);

    /// <summary>A snippet file in VS Code's format. <paramref name="languageId"/> scopes a file that has no scopes of its own; null for a global file.</summary>
    IDisposable AddSnippetFile(string path, string? languageId = null);

    /// <summary>A theme file (VS Code's format).</summary>
    IDisposable AddThemeFile(string path, string? label = null);

    IDisposable AddGrammar(GrammarSpec grammar);
}
