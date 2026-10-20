using Avalonia.Input;
using Wicked.Editor.Model;

namespace Wicked.App.Editor;

/// <summary>An editor command: an id for keybindings.json and the palette, a title, and what it does.</summary>
public sealed record EditorCommand(string Id, string Title, Action<CodeEditor> Run);

/// <summary>
/// The editor's default keys: VS Code's, for Linux and for Windows where they differ (Linux takes Ctrl+Shift+Up/Down for
/// extra carets because desktops claim Ctrl+Alt+arrows).
/// </summary>
public static class EditorKeymap
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    public static IReadOnlyList<(KeyGesture Gesture, EditorCommand Command)> Defaults { get; } = Build();

    private static List<(KeyGesture, EditorCommand)> Build()
    {
        var map = new List<(KeyGesture, EditorCommand)>();
        void Add(string gesture, string id, string title, Action<CodeEditor> run) => map.Add((KeyGesture.Parse(gesture), new EditorCommand(id, title, run)));

        foreach (var (shift, suffix) in new[] { ("", ""), ("Shift+", "Select") })
        {
            var select = shift.Length > 0;
            Add($"{shift}Left", $"cursorLeft{suffix}", "Caret left", e => Edits.Left(e.Document, select));
            Add($"{shift}Right", $"cursorRight{suffix}", "Caret right", e => Edits.Right(e.Document, select));
            Add($"{shift}Up", $"cursorUp{suffix}", "Caret up", e => Edits.Up(e.Document, select, e.Options));
            Add($"{shift}Down", $"cursorDown{suffix}", "Caret down", e => Edits.Down(e.Document, select, e.Options));
            Add($"{shift}Ctrl+Left", $"cursorWordStartLeft{suffix}", "Word left", e => Edits.WordLeft(e.Document, select));
            Add($"{shift}Ctrl+Right", $"cursorWordEndRight{suffix}", "Word right", e => Edits.WordRight(e.Document, select));
            Add($"{shift}Home", $"cursorHome{suffix}", "Line start", e => Edits.Home(e.Document, select));
            Add($"{shift}End", $"cursorEnd{suffix}", "Line end", e => Edits.End(e.Document, select));
            Add($"{shift}Ctrl+Home", $"cursorTop{suffix}", "Document start", e => Edits.DocumentStart(e.Document, select));
            Add($"{shift}Ctrl+End", $"cursorBottom{suffix}", "Document end", e => Edits.DocumentEnd(e.Document, select));
            Add($"{shift}PageUp", $"cursorPageUp{suffix}", "Page up", e => e.Page(-1, select));
            Add($"{shift}PageDown", $"cursorPageDown{suffix}", "Page down", e => e.Page(1, select));
        }

        Add("Back", "deleteLeft", "Delete left", e => Edits.Backspace(e.Document, e.Options));
        Add("Shift+Back", "deleteLeftShift", "Delete left", e => Edits.Backspace(e.Document, e.Options));
        Add("Delete", "deleteRight", "Delete right", e => Edits.Delete(e.Document));
        Add("Ctrl+Back", "deleteWordLeft", "Delete word left", e => Edits.DeleteWordLeft(e.Document));
        Add("Ctrl+Delete", "deleteWordRight", "Delete word right", e => Edits.DeleteWordRight(e.Document));
        Add("Enter", "newLine", "New line", e => Edits.NewLine(e.Document, e.Options));
        Add("Tab", "tab", "Indent", e => Edits.Tab(e.Document, e.Options));
        Add("Shift+Tab", "outdent", "Outdent", e => Edits.OutdentLines(e.Document, e.Options));
        Add("Ctrl+OemCloseBrackets", "indentLines", "Indent lines", e => Edits.IndentLines(e.Document, e.Options));
        Add("Ctrl+OemOpenBrackets", "outdentLines", "Outdent lines", e => Edits.OutdentLines(e.Document, e.Options));
        Add("Ctrl+Z", "undo", "Undo", e => e.Document.Undo());
        Add("Ctrl+Shift+Z", "redo", "Redo", e => e.Document.Redo());
        Add("Ctrl+Y", "redoAlt", "Redo", e => e.Document.Redo());
        Add("Ctrl+A", "selectAll", "Select all", e => Edits.SelectAll(e.Document));
        Add("Escape", "removeSecondaryCursors", "Single caret", e =>
        {
            if (e.Find.IsOpen) e.Find.Close();
            else Edits.SingleCaret(e.Document);
        });
        Add("Ctrl+F", "actions.find", "Find", e => e.Find.Open(replace: false));
        Add("Ctrl+H", "editor.action.startFindReplaceAction", "Replace", e => e.Find.Open(replace: true));
        Add("F3", "editor.action.nextMatchFindAction", "Find next", e => e.Find.Next());
        Add("Shift+F3", "editor.action.previousMatchFindAction", "Find previous", e => e.Find.Previous());
        Add("Ctrl+C", "clipboardCopy", "Copy", e => _ = e.CopyAsync());
        Add("Ctrl+Insert", "clipboardCopyAlt", "Copy", e => _ = e.CopyAsync());
        Add("Ctrl+X", "clipboardCut", "Cut", e => _ = e.CutAsync());
        Add("Shift+Delete", "clipboardCutAlt", "Cut", e => _ = e.CutAsync());
        Add("Ctrl+V", "clipboardPaste", "Paste", e => _ = e.PasteAsync());
        Add("Shift+Insert", "clipboardPasteAlt", "Paste", e => _ = e.PasteAsync());
        Add("Ctrl+D", "addSelectionToNextFindMatch", "Add next occurrence", e => Edits.AddNextOccurrence(e.Document));
        Add("Ctrl+Shift+L", "selectHighlights", "Select all occurrences", e => Edits.SelectAllOccurrences(e.Document));
        Add(Windows ? "Ctrl+Alt+Up" : "Ctrl+Shift+Up", "insertCursorAbove", "Add caret above", e => Edits.AddCaretVertically(e.Document, -1, e.Options));
        Add(Windows ? "Ctrl+Alt+Down" : "Ctrl+Shift+Down", "insertCursorBelow", "Add caret below", e => Edits.AddCaretVertically(e.Document, 1, e.Options));
        Add("Alt+Up", "moveLinesUp", "Move lines up", e => Edits.MoveLines(e.Document, -1));
        Add("Alt+Down", "moveLinesDown", "Move lines down", e => Edits.MoveLines(e.Document, 1));
        Add(Windows ? "Shift+Alt+Up" : "Ctrl+Shift+Alt+Up", "copyLinesUp", "Copy lines up", e => Edits.CopyLines(e.Document, -1));
        Add(Windows ? "Shift+Alt+Down" : "Ctrl+Shift+Alt+Down", "copyLinesDown", "Copy lines down", e => Edits.CopyLines(e.Document, 1));
        Add("Ctrl+Shift+K", "deleteLines", "Delete lines", e => Edits.DeleteLines(e.Document, e.Options));
        Add("Ctrl+Oem2", "commentLine", "Toggle line comment", e => Edits.ToggleLineComment(e.Document, e.Options));
        Add(Windows ? "Shift+Alt+A" : "Ctrl+Shift+A", "blockComment", "Toggle block comment", e => Edits.ToggleBlockComment(e.Document, e.Options));
        return map;
    }
}
