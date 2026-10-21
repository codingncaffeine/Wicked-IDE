using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wicked.Core.Settings;

/// <summary>
/// The user's settings, under VS Code's own names so a settings.json from VS Code mostly reads as-is. Everything here
/// has a control in the Settings page; the file is for those who prefer to type. Immutable: a change makes a new one.
/// </summary>
public sealed record WickedSettings
{
    [Setting("The size of the code's letters, in pixels.", Minimum = 6, Maximum = 72)]
    [JsonPropertyName("editor.fontSize")]
    public double FontSize { get; init; } = 14;

    [Setting("The height of a line as a multiple of the font size.", Minimum = 1, Maximum = 3)]
    [JsonPropertyName("editor.lineHeight")]
    public double LineHeight { get; init; } = 1.5;

    [Setting("Joins letter pairs the font draws as one (=> and != in fonts that have them).")]
    [JsonPropertyName("editor.fontLigatures")]
    public bool FontLigatures { get; init; } = true;

    [Setting("How many columns a tab is, and how far Tab indents with spaces.", Minimum = 1, Maximum = 16)]
    [JsonPropertyName("editor.tabSize")]
    public int TabSize { get; init; } = 4;

    [Setting("Tab types spaces instead of a tab character.")]
    [JsonPropertyName("editor.insertSpaces")]
    public bool InsertSpaces { get; init; } = true;

    /// <summary>A file's own indentation (tabs or spaces, and how many) wins over the two above, as in VS Code.</summary>
    [Setting("A file's own indentation (tabs or spaces, and how many) wins over Tab Size and Insert Spaces.")]
    [JsonPropertyName("editor.detectIndentation")]
    public bool DetectIndentation { get; init; } = true;

    [Setting("Typing an opening bracket or quote adds its closing one.")]
    [JsonPropertyName("editor.autoClosingBrackets")]
    public bool AutoClosingBrackets { get; init; } = true;

    [Setting("Matching brackets share a colour, a different one for each level.")]
    [JsonPropertyName("editor.bracketPairColorization.enabled")]
    public bool BracketPairColorization { get; init; } = true;

    /// <summary>off, on, wordWrapColumn or bounded, as in VS Code.</summary>
    [Setting("How long lines wrap: not at all, at the window's edge, at Word Wrap Column, or at whichever comes first.", Values = new[] { "off", "on", "wordWrapColumn", "bounded" })]
    [JsonPropertyName("editor.wordWrap")]
    public string WordWrap { get; init; } = "off";

    [Setting("The column long lines wrap at when Word Wrap is wordWrapColumn or bounded.", Minimum = 1)]
    [JsonPropertyName("editor.wordWrapColumn")]
    public int WordWrapColumn { get; init; } = 80;

    /// <summary>none, same, indent or deepIndent: where wrapped rows start.</summary>
    [Setting("Where a wrapped line's continuation starts: at the left edge, under the line, or one or two levels further in.", Values = new[] { "none", "same", "indent", "deepIndent" })]
    [JsonPropertyName("editor.wrappingIndent")]
    public string WrappingIndent { get; init; } = "same";

    /// <summary>off, on or onlySnippets, as VS Code names them: Tab after a snippet's prefix expands it.</summary>
    [Setting("Tab after a snippet's prefix expands it: never, always, or for snippets only.", Values = new[] { "off", "on", "onlySnippets" })]
    [JsonPropertyName("editor.tabCompletion")]
    public string TabCompletion { get; init; } = "off";

    [Setting("The small picture of the whole file beside the scroll bar.")]
    [JsonPropertyName("editor.minimap.enabled")]
    public bool Minimap { get; init; } = true;

    /// <summary>on, off or relative, as VS Code names them (relative: the caret's line shows its own number, the rest their distance).</summary>
    [Setting("Line numbers: shown, hidden, or counted from the caret's line.", Values = new[] { "on", "off", "relative" })]
    [JsonPropertyName("editor.lineNumbers")]
    public string LineNumbers { get; init; } = "on";

    [Setting("The path to the file and the symbol at the caret, above the code.")]
    [JsonPropertyName("breadcrumbs.enabled")]
    public bool Breadcrumbs { get; init; } = true;

    /// <summary>Columns to draw a vertical line at; VS Code's numbers or { "column": n } objects are both read.</summary>
    [Setting("Columns to draw a vertical line at, such as [80, 120].")]
    [JsonPropertyName("editor.rulers")]
    [JsonConverter(typeof(RulerColumnsConverter))]
    public int[] Rulers { get; init; } = [];

    /// <summary>The headers of the scopes the top line is in stay at the top as the code scrolls under them.</summary>
    [Setting("The headers of the scopes the top line is in stay at the top as the code scrolls under them.")]
    [JsonPropertyName("editor.stickyScroll.enabled")]
    public bool StickyScroll { get; init; } = true;

    [Setting("The most header lines Sticky Scroll keeps at the top.", Minimum = 1, Maximum = 20)]
    [JsonPropertyName("editor.stickyScroll.maxLineCount")]
    public int StickyScrollMaxLines { get; init; } = 5;

    [Setting("Faint vertical lines at each level of indentation.")]
    [JsonPropertyName("editor.guides.indentation")]
    public bool IndentGuides { get; init; } = true;

    /// <summary>none, boundary, selection, trailing or all, as VS Code names them.</summary>
    [Setting("Where spaces and tabs are drawn as dots and arrows.", Values = new[] { "none", "boundary", "selection", "trailing", "all" })]
    [JsonPropertyName("editor.renderWhitespace")]
    public string RenderWhitespace { get; init; } = "selection";

    [Setting("Saves edited files by itself: after a pause, when the editor loses the keyboard, or when the window does.", Values = new[] { "off", "afterDelay", "onFocusChange", "onWindowChange" })]
    [JsonPropertyName("files.autoSave")]
    public string AutoSave { get; init; } = "off";

    [Setting("The pause before an edited file is saved when Auto Save is afterDelay, in milliseconds.", Minimum = 100)]
    [JsonPropertyName("files.autoSaveDelay")]
    public int AutoSaveDelay { get; init; } = 1000;

    /// <summary>onExit (VS Code's default): unsaved files are kept when the window closes, and come back; off: closing asks.</summary>
    [Setting("Unsaved files are kept when the window closes and come back next time; off asks to save instead.", Values = new[] { "onExit", "off" })]
    [JsonPropertyName("files.hotExit")]
    public string HotExit { get; init; } = "onExit";

    /// <summary>The whole window's zoom as VS Code counts it: 0 is 100%, each step 20% more (1) or less (-1).</summary>
    [Setting("The whole window's zoom: 0 is 100%, each step up or down is 20% more or less (Ctrl+= and Ctrl+-).", Minimum = -5, Maximum = 8)]
    [JsonPropertyName("window.zoomLevel")]
    public double ZoomLevel { get; init; }

    [Setting("The status bar along the bottom of the window.")]
    [JsonPropertyName("workbench.statusBar.visible")]
    public bool StatusBarVisible { get; init; } = true;

    /// <summary>bottom, left or right: where the panel (Problems, Output, Terminal) sits.</summary>
    [Setting("Where the panel (Problems, Output, Terminal) sits.", Values = new[] { "bottom", "left", "right" })]
    [JsonPropertyName("workbench.panel.defaultLocation")]
    public string PanelLocation { get; init; } = "bottom";

    /// <summary>A single click in the explorer opens a preview tab that the next one replaces, until it is edited or kept.</summary>
    [Setting("A single click in the Explorer opens a preview tab that the next click replaces, until it is edited or kept.")]
    [JsonPropertyName("workbench.editor.enablePreview")]
    public bool EnablePreview { get; init; } = true;

    [Setting("Spaces and tabs at the ends of lines are removed when a file is saved.")]
    [JsonPropertyName("files.trimTrailingWhitespace")]
    public bool TrimTrailingWhitespace { get; init; }

    [Setting("A file saved without a line break at its end gets one.")]
    [JsonPropertyName("files.insertFinalNewline")]
    public bool InsertFinalNewline { get; init; }

    /// <summary>The title bar: "custom" (Wicked draws it, with the command centre) or "native" (the desktop's frame).</summary>
    [Setting("Wicked draws the title bar (with the menus, the command centre and the run controls), or the desktop draws its own.", Values = new[] { "custom", "native" })]
    [JsonPropertyName("window.titleBarStyle")]
    public string TitleBarStyle { get; init; } = "custom";

    /// <summary>
    /// The menu bar (VS Code's window.menuBarVisibility): "classic" (File, Edit... in the title bar), "compact" (one ☰ menu),
    /// "toggle" (hidden until Alt is pressed) or "hidden".
    /// </summary>
    [Setting("The menu bar: in the title bar, folded into one menu, hidden until Alt is pressed, or hidden.", Values = new[] { "classic", "compact", "toggle", "hidden" })]
    [JsonPropertyName("window.menuBarVisibility")]
    public string MenuBarVisibility { get; init; } = "classic";

    /// <summary>The keys: VS Code's, or a JetBrains IDE's laid over them (keybindings.json wins over both).</summary>
    [Setting("The keys you know: Visual Studio Code's, or those of JetBrains IDEs (IntelliJ IDEA, Rider, PyCharm) where they differ. keybindings.json wins over both.",
        Values = new[] { "vscode", "jetbrains" })]
    [JsonPropertyName("workbench.keymap")]
    public string Keymap { get; init; } = "vscode";

    /// <summary>The caret: "blink" (the default) or "solid" (a steady caret, also what reduced motion asks for).</summary>
    [Setting("The caret blinks, or stays solid (also what reduced motion asks for).", Values = new[] { "blink", "solid" })]
    [JsonPropertyName("editor.cursorBlinking")]
    public string CursorBlinking { get; init; } = "blink";

    /// <summary>The window theme by name (VS Code's workbench.colorTheme): a built-in one or a theme.json in config/themes.</summary>
    [Setting("The window's theme, by name: a built-in one or a theme in the themes folder.")]
    [JsonPropertyName("workbench.colorTheme")]
    public string ColorTheme { get; init; } = "Wicked Dark";

    /// <summary>The code's colours, chosen apart from the window's theme (D28); empty means the theme's own.</summary>
    [Setting("The code's colours, chosen apart from the window's theme; empty uses the theme's own.")]
    [JsonPropertyName("workbench.codeColorTheme")]
    public string CodeColorTheme { get; init; } = "";

    /// <summary>The theme follows the desktop's light or dark choice (the two themes below), as VS Code's window.autoDetectColorScheme.</summary>
    [Setting("The window follows the desktop's light or dark choice, with the Preferred Dark and Preferred Light themes.")]
    [JsonPropertyName("window.autoDetectColorScheme")]
    public bool AutoDetectColorScheme { get; init; }

    [Setting("The theme used when the desktop asks for dark and the window follows it.")]
    [JsonPropertyName("workbench.preferredDarkColorTheme")]
    public string PreferredDarkColorTheme { get; init; } = "Wicked Dark";

    [Setting("The theme used when the desktop asks for light and the window follows it.")]
    [JsonPropertyName("workbench.preferredLightColorTheme")]
    public string PreferredLightColorTheme { get; init; } = "Wicked Light";

    [Setting("The folder and the files open when Wicked last closed open again when it starts.")]
    [JsonPropertyName("window.restoreWindows")]
    public bool RestoreSession { get; init; } = true;

    /// <summary>Where new model installs go; empty means the default folder under the data directory. Models already installed stay where they are.</summary>
    [Setting("Where new models are installed; empty uses the default folder. Models already installed stay where they are.")]
    [JsonPropertyName("models.storeLocation")]
    public string StoreLocation { get; init; } = "";

    /// <summary>The Hugging Face access token (for gated models and higher rate limits), kept here in plain text as the Hub's own tools keep theirs.</summary>
    [Setting("A Hugging Face access token, for gated models and higher download limits.", Secret = true)]
    [JsonPropertyName("huggingface.token")]
    public string HuggingFaceToken { get; init; } = "";

    /// <summary>Total download speed cap in megabytes per second; 0 is unlimited.</summary>
    [Setting("The most all downloads together may use, in megabytes a second; 0 is no limit.", Minimum = 0)]
    [JsonPropertyName("models.downloadLimitMBps")]
    public double DownloadLimit { get; init; }

    /// <summary>The engine build to use: auto (what this machine calls for), cuda, vulkan, rocm or cpu.</summary>
    [Setting("The engine build: what this machine calls for, or CUDA, Vulkan, ROCm or the processor only.", Values = new[] { "auto", "cuda", "vulkan", "rocm", "cpu" })]
    [JsonPropertyName("runtime.backend")]
    public string RuntimeBackend { get; init; } = "auto";

    // ── Language servers (Phase 9) ──

    /// <summary>Language servers start for the languages they serve; off, no server is ever started.</summary>
    [Setting("Language servers start for the languages they serve; off, none is ever started.")]
    [JsonPropertyName("lsp.enabled")]
    public bool LanguageServers { get; init; } = true;

    /// <summary>Per server (by catalog id): its own command line, or <c>enabled: false</c> to keep it off.</summary>
    [Setting("Each language server's own command line, its settings, or enabled: false to keep it off, by its id.")]
    [JsonPropertyName("lsp.servers")]
    public Dictionary<string, LanguageServerSetting>? Servers { get; init; }

    [Setting("The language server formats a file when it is saved.")]
    [JsonPropertyName("editor.formatOnSave")]
    public bool FormatOnSave { get; init; }

    [Setting("The language server formats a line as it is typed (after ; or } in languages that do that).")]
    [JsonPropertyName("editor.formatOnType")]
    public bool FormatOnType { get; init; }

    /// <summary>Suggestions while typing (VS Code's editor.quickSuggestions, simplified to on or off).</summary>
    [Setting("Suggestions open by themselves while typing.")]
    [JsonPropertyName("editor.quickSuggestions")]
    public bool QuickSuggestions { get; init; } = true;

    /// <summary>off, currentDocument, matchingDocuments or allDocuments: where words come from when no language server suggests anything.</summary>
    [Setting("Where words are suggested from when no language server suggests anything.", Values = new[] { "off", "currentDocument", "matchingDocuments", "allDocuments" })]
    [JsonPropertyName("editor.wordBasedSuggestions")]
    public string WordBasedSuggestions { get; init; } = "matchingDocuments";

    [Setting("Suggestions open after a character that starts a member or an argument (. or ( in most languages).")]
    [JsonPropertyName("editor.suggestOnTriggerCharacters")]
    public bool SuggestOnTriggerCharacters { get; init; } = true;

    [Setting("Enter takes the chosen suggestion as well as Tab.", Values = new[] { "on", "off" })]
    [JsonPropertyName("editor.acceptSuggestionOnEnter")]
    public string AcceptSuggestionOnEnter { get; init; } = "on";

    [Setting("A function's parameters are shown while its arguments are typed.")]
    [JsonPropertyName("editor.parameterHints.enabled")]
    public bool ParameterHints { get; init; } = true;

    [Setting("Resting the pointer on code shows what the language server knows about it.")]
    [JsonPropertyName("editor.hover.enabled")]
    public bool Hover { get; init; } = true;

    [Setting("How long the pointer rests before the hover shows, in milliseconds.", Minimum = 0)]
    [JsonPropertyName("editor.hover.delay")]
    public int HoverDelay { get; init; } = 300;

    /// <summary>on, off, onUnlessPressed, offUnlessPressed (Ctrl+Alt held), as VS Code has it.</summary>
    [Setting("Parameter names and inferred types drawn in the code: always, never, or flipped while Ctrl+Alt is held.", Values = new[] { "on", "off", "onUnlessPressed", "offUnlessPressed" })]
    [JsonPropertyName("editor.inlayHints.enabled")]
    public string InlayHints { get; init; } = "on";

    [Setting("The language server's colours (what is a type, a parameter, a field) on top of the grammar's.")]
    [JsonPropertyName("editor.semanticHighlighting.enabled")]
    public bool SemanticHighlighting { get; init; } = true;

    /// <summary>Other uses of the symbol at the caret, highlighted.</summary>
    [Setting("Other uses of the symbol at the caret are highlighted.")]
    [JsonPropertyName("editor.occurrencesHighlight")]
    public bool OccurrencesHighlight { get; init; } = true;

    /// <summary>The lightbulb beside a line with quick fixes.</summary>
    [Setting("A lightbulb beside a line that has quick fixes.")]
    [JsonPropertyName("editor.lightbulb.enabled")]
    public bool Lightbulb { get; init; } = true;

    // ── AI assistance (Phase 7) ──

    /// <summary>Ghost-text suggestions from a local coding model as you type (VS Code's editor.inlineSuggest.enabled).</summary>
    [Setting("Ghost-text suggestions from a local coding model as you type; Tab takes them.")]
    [JsonPropertyName("editor.inlineSuggest.enabled")]
    public bool InlineSuggest { get; init; } = true;

    /// <summary>The model that writes them (a runtime id); empty picks an installed coding model.</summary>
    [Setting("The model that writes ghost-text suggestions, by its id; empty picks an installed coding model.")]
    [JsonPropertyName("wicked.completionModel")]
    public string CompletionModel { get; init; } = "";

    /// <summary>How long typing pauses before a suggestion is asked for, in milliseconds.</summary>
    [Setting("How long typing pauses before a suggestion is asked for, in milliseconds.", Minimum = 0)]
    [JsonPropertyName("wicked.inlineSuggest.delay")]
    public int InlineSuggestDelay { get; init; } = 200;

    /// <summary>The model the assistant's chat, edits and agent use for code (a runtime id); empty uses the chat's choice.</summary>
    [Setting("The model the assistant's chat, edits and agent use for code, by its id; empty uses the chat's choice.")]
    [JsonPropertyName("wicked.assistantModel")]
    public string AssistantModel { get; init; } = "";

    // ── Updates and serving (Phases 5 and 6) ──

    /// <summary>Whether Wicked looks for newer models, engines and releases by itself (on start and every day). "Check now" works either way.</summary>
    [Setting("Wicked looks for newer models, engines and releases by itself, when it starts and every day.")]
    [JsonPropertyName("models.checkForUpdates")]
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>What a model without a policy of its own does about a newer upload: notify, auto or pin.</summary>
    [Setting("What a model without a policy of its own does about a newer upload: says so, updates by itself, or stays as it is.", Values = new[] { "notify", "auto", "pin" })]
    [JsonPropertyName("models.updatePolicy")]
    public string UpdatePolicy { get; init; } = "notify";

    /// <summary>Whether the daemon serves the OpenAI and Anthropic APIs to other programs (Continue, Zed, Cursor, Aider, curl).</summary>
    [Setting("The daemon serves the OpenAI and Anthropic APIs to other programs on this machine.")]
    [JsonPropertyName("server.enabled")]
    public bool ServerEnabled { get; init; } = true;

    [Setting("The port the server listens on.", Minimum = 1, Maximum = 65535)]
    [JsonPropertyName("server.port")]
    public int ServerPort { get; init; } = 1337;

    /// <summary>Also listen on the local network, not just this machine. Needs an API key for anything but this machine.</summary>
    [Setting("The server also listens on the local network; anything but this machine needs an API key.")]
    [JsonPropertyName("server.listenOnLan")]
    public bool ServerListenOnLan { get; init; }

    /// <summary>Load a model on the first request that names it, as LM Studio does.</summary>
    [Setting("A model loads on the first request that names it.")]
    [JsonPropertyName("server.autoLoad")]
    public bool ServerAutoLoad { get; init; } = true;

    /// <summary>Web origins that may call the server from a browser page (e.g. <c>http://localhost:3000</c>); empty allows none.</summary>
    [Setting("Web origins that may call the server from a browser page, such as http://localhost:3000; empty allows none.")]
    [JsonPropertyName("server.cors")]
    public string[] ServerCors { get; init; } = [];

    /// <summary>The largest request the server reads, in megabytes (images in a chat can be big).</summary>
    [Setting("The largest request the server reads, in megabytes (pictures in a chat can be big).", Minimum = 1, Maximum = 1024)]
    [JsonPropertyName("server.maxRequestMB")]
    public int ServerMaxRequestMB { get; init; } = 64;

    // ── The integrated terminal ──

    /// <summary>The shell to start, by name or path; empty means the user's own.</summary>
    [Setting("The shell a new terminal starts, by name or path; empty uses your own.")]
    [JsonPropertyName("terminal.integrated.defaultProfile")]
    public string TerminalProfile { get; init; } = "";

    [Setting("The size of the terminal's letters, in pixels.", Minimum = 6, Maximum = 72)]
    [JsonPropertyName("terminal.integrated.fontSize")]
    public double TerminalFontSize { get; init; } = 14;

    /// <summary>Lines kept above the screen of each terminal.</summary>
    [Setting("The lines each terminal keeps above its screen.", Minimum = 0)]
    [JsonPropertyName("terminal.integrated.scrollback")]
    public int TerminalScrollback { get; init; } = 10_000;

    /// <summary>block, line or underline, as VS Code names them.</summary>
    [Setting("The terminal's cursor shape.", Values = new[] { "block", "line", "underline" })]
    [JsonPropertyName("terminal.integrated.cursorStyle")]
    public string TerminalCursorStyle { get; init; } = "block";

    [Setting("The terminal's cursor blinks.")]
    [JsonPropertyName("terminal.integrated.cursorBlinking")]
    public bool TerminalCursorBlinking { get; init; }

    [Setting("Text selected in the terminal is copied at once.")]
    [JsonPropertyName("terminal.integrated.copyOnSelection")]
    public bool TerminalCopyOnSelection { get; init; }

    /// <summary>Whether a program may put text on the clipboard (OSC 52). Off: it could replace what the user copied.</summary>
    [Setting("Programs in the terminal may put text on the clipboard; off, they cannot replace what you copied.")]
    [JsonPropertyName("terminal.integrated.allowClipboardWrite")]
    public bool TerminalAllowClipboardWrite { get; init; }

    /// <summary>Marks round prompts and commands, for the decorations and command navigation.</summary>
    [Setting("The terminal marks prompts and commands, for the decorations and moving between commands.")]
    [JsonPropertyName("terminal.integrated.shellIntegration.enabled")]
    public bool TerminalShellIntegration { get; init; } = true;
    // ── Debugging (Phase 13) ──

    /// <summary>The value of each variable drawn after the line that uses it, while stopped.</summary>
    [Setting("Each variable's value is drawn after the line that uses it while the program is stopped.")]
    [JsonPropertyName("debug.inlineValues")]
    public bool DebugInlineValues { get; init; } = true;

    /// <summary>When the Run and Debug panel opens by itself: neverOpen, openOnSessionStart, openOnFirstSessionStart or openOnDebugBreak.</summary>
    [Setting("When the Run and Debug panel opens by itself.", Values = new[] { "neverOpen", "openOnSessionStart", "openOnFirstSessionStart", "openOnDebugBreak" })]
    [JsonPropertyName("debug.openDebug")]
    public string DebugOpenDebug { get; init; } = "openOnFirstSessionStart";

    /// <summary>Keys this build does not know, kept so a save never drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // The same bytes on every platform (the catalog is signed over them): no CRLF on Windows.
        NewLine = "\n",
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>One language server's own settings: a command line to run instead of the one found, or off.</summary>
public sealed record LanguageServerSetting
{
    [JsonPropertyName("command")]
    public List<string>? Command { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>Answers the server's workspace/configuration requests (its own settings, by section).</summary>
    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; init; }
}

/// <summary>
/// Reads and writes settings.json. Reading tolerates comments and trailing commas (the file is JSONC, as VS Code's is);
/// a file that does not parse leaves the previous settings in force and says why. Disk work only: call off the UI thread.
/// </summary>
public static class SettingsFile
{
    public static async Task<(WickedSettings Settings, string? Problem)> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return (new WickedSettings(), null);
        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<WickedSettings>(stream, WickedSettings.Json, ct).ConfigureAwait(false);
            return (settings ?? new WickedSettings(), null);
        }
        catch (JsonException ex)
        {
            return (new WickedSettings(), $"settings.json could not be read: {ex.Message}");
        }
    }

    public static Task WriteAsync(string path, WickedSettings settings, CancellationToken ct) =>
        Files.TextFile.WriteAsync(path, JsonSerializer.Serialize(settings, WickedSettings.Json) + "\n", Files.FileEncoding.Utf8, ct);
}
