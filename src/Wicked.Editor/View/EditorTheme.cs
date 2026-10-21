namespace Wicked.Editor.View;

/// <summary>The editor's own colours (0xAARRGGBB), apart from the code's, which come from the syntax theme.</summary>
public sealed record EditorTheme
{
    public uint Background { get; init; } = 0xFF111216;

    /// <summary>Text the syntax theme gives no colour.</summary>
    public uint Foreground { get; init; } = 0xFFD4D4D4;

    public uint CurrentLine { get; init; } = 0xFF181A20;

    public uint Selection { get; init; } = 0x598B7BF7;

    public uint SelectionUnfocused { get; init; } = 0x332C3038;

    public uint Caret { get; init; } = 0xFFA194FA;

    public uint LineNumber { get; init; } = 0xFF4B5160;

    public uint LineNumberActive { get; init; } = 0xFFC9CED8;

    public uint IndentGuide { get; init; } = 0xFF23262D;

    public uint IndentGuideActive { get; init; } = 0xFF3A3F49;

    /// <summary>The box drawn round a bracket and its partner at the caret.</summary>
    public uint BracketMatch { get; init; } = 0x998B7BF7;

    /// <summary>Spaces and tabs made visible (in a selection, by default).</summary>
    public uint Whitespace { get; init; } = 0x66A2A8B4;

    /// <summary>Bracket-pair colours by depth (VS Code's defaults), and the colour of a bracket with no partner.</summary>
    public uint[] BracketColors { get; init; } = [0xFFFFD700, 0xFFDA70D6, 0xFF179FFF];

    public uint BracketUnmatched { get; init; } = 0xFFFF1212;

    /// <summary>Every find match, and the one the find widget is on (amber, as editors mark them).</summary>
    public uint FindMatch { get; init; } = 0x40F2B84B;

    public uint FindMatchCurrent { get; init; } = 0x99F2B84B;

    /// <summary>Diagnostics (VS Code's editorError/Warning/Info foregrounds), and a hint's dots.</summary>
    public uint ErrorColor { get; init; } = 0xFFF14C4C;

    public uint WarningColor { get; init; } = 0xFFCCA700;

    public uint InfoColor { get; init; } = 0xFF3794FF;

    public uint HintColor { get; init; } = 0xB3EEEEEE;

    /// <summary>Other uses of the symbol at the caret, and the places it is written.</summary>
    public uint WordHighlight { get; init; } = 0x40575E6E;

    public uint WordHighlightWrite { get; init; } = 0x55606A80;

    /// <summary>Diff backgrounds: whole lines, and the characters that changed within them.</summary>
    public uint DiffInsertedLine { get; init; } = 0x1F2EA043;

    public uint DiffRemovedLine { get; init; } = 0x1FF85149;

    public uint DiffInserted { get; init; } = 0x402EA043;

    public uint DiffRemoved { get; init; } = 0x40F85149;

    /// <summary>A merge conflict's sides (VS Code's merge colours), and its base.</summary>
    public uint ConflictCurrent { get; init; } = 0x3340C8AE;

    public uint ConflictIncoming { get; init; } = 0x3340A6FF;

    public uint ConflictBase { get; init; } = 0x33A0A0A0;

    /// <summary>The line the debugger stopped on.</summary>
    public uint StoppedLine { get; init; } = 0x33FFCC00;

    public uint StoppedGlyph { get; init; } = 0xFFFFCC00;

    /// <summary>Source-control marks beside the line numbers.</summary>
    public uint GutterAdded { get; init; } = 0xFF2EA043;

    public uint GutterModified { get; init; } = 0xFF0078D4;

    public uint GutterDeleted { get; init; } = 0xFFF85149;

    public uint Breakpoint { get; init; } = 0xFFE51400;

    public uint BreakpointDisabled { get; init; } = 0xFF848484;

    public uint Lightbulb { get; init; } = 0xFFFFCC00;

    public uint BookmarkColor { get; init; } = 0xFF4FC1FF;

    /// <summary>Inlay hints: text and pill, and ghost text a completion suggests.</summary>
    public uint InlayForeground { get; init; } = 0xFF969696;

    public uint InlayBackground { get; init; } = 0x1A969696;

    public uint GhostText { get; init; } = 0x80FFFFFF;

    public uint Link { get; init; } = 0xFF4E94CE;

    public uint ScrollbarThumb { get; init; } = 0x26FFFFFF;

    public uint ScrollbarThumbActive { get; init; } = 0x40FFFFFF;

    /// <summary>The caret's width in pixels (VS Code's default is 2).</summary>
    public float CaretWidth { get; init; } = 2;

    public static readonly EditorTheme WickedDark = new();

    /// <summary>
    /// This theme with the editor surface a code palette defines (background, text, selection, current line, line
    /// numbers, caret), so a light palette reads on its own light background. Colours the palette leaves out stay.
    /// </summary>
    public EditorTheme WithPalette(IReadOnlyDictionary<string, uint> gui)
    {
        uint Pick(string key, uint fallback) => gui.TryGetValue(key, out var c) && c != 0 ? c : fallback;
        var background = Pick("editor.background", Background);
        return this with
        {
            Background = background,
            Foreground = Pick("editor.foreground", Foreground),
            CurrentLine = Pick("editor.lineHighlightBackground", Pick("editor.lineHighlightBorder", CurrentLine)),
            Selection = Pick("editor.selectionBackground", Selection),
            SelectionUnfocused = Pick("editor.inactiveSelectionBackground", SelectionUnfocused),
            Caret = Pick("editorCursor.foreground", Caret),
            LineNumber = Pick("editorLineNumber.foreground", LineNumber),
            LineNumberActive = Pick("editorLineNumber.activeForeground", LineNumberActive),
            IndentGuide = Pick("editorIndentGuide.background", IndentGuide),
            ErrorColor = Pick("editorError.foreground", ErrorColor),
            WarningColor = Pick("editorWarning.foreground", WarningColor),
            InfoColor = Pick("editorInfo.foreground", InfoColor),
            GutterAdded = Pick("editorGutter.addedBackground", GutterAdded),
            GutterModified = Pick("editorGutter.modifiedBackground", GutterModified),
            GutterDeleted = Pick("editorGutter.deletedBackground", GutterDeleted),
            InlayForeground = Pick("editorInlayHint.foreground", InlayForeground),
            InlayBackground = Pick("editorInlayHint.background", InlayBackground),
            GhostText = Pick("editorGhostText.foreground", GhostText),
            WordHighlight = Pick("editor.wordHighlightBackground", WordHighlight),
            WordHighlightWrite = Pick("editor.wordHighlightStrongBackground", WordHighlightWrite),
        };
    }
}
