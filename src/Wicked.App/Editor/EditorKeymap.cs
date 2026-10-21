using Avalonia.Input;
using Wicked.Editor.Model;

namespace Wicked.App.Editor;

/// <summary>An editor command: VS Code's id (so a keybindings.json from VS Code binds the same command), a title, and what it does.</summary>
public sealed record EditorCommand(string Id, string Title, Action<CodeEditor> Run);

/// <summary>
/// The editor's default keys: VS Code's, for Linux and for Windows where they differ (Linux takes Ctrl+Shift+Up/Down for
/// extra carets because desktops claim Ctrl+Alt+arrows).
/// </summary>
public static class EditorKeymap
{
    private static readonly bool Windows = OperatingSystem.IsWindows();

    public static IReadOnlyList<(KeyGesture Gesture, EditorCommand Command)> Defaults { get; } = Build();

    private static volatile IReadOnlyList<(KeyGesture Gesture, EditorCommand Command)> _effective = Defaults;

    /// <summary>The keys in force: the defaults with keybindings.json applied (its bindings first, its removals gone).</summary>
    public static IReadOnlyList<(KeyGesture Gesture, EditorCommand Command)> Effective => _effective;

    /// <summary>Applies keybindings.json to the editor's commands (chords and unknown commands are for the workbench).</summary>
    public static void Apply(IReadOnlyList<Wicked.Core.Settings.Keybinding> bindings)
    {
        var byId = Defaults.Select(d => d.Command).DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        var list = Defaults.ToList();
        foreach (var b in bindings.Where(b => b.Remove && byId.ContainsKey(b.Command)))
        {
            list.RemoveAll(d => d.Command.Id == b.Command && (b.Keys is null || d.Gesture.Equals(KeyGesture.Parse(b.Keys))));
        }

        var added = bindings.Where(b => !b.Remove && b.Keys is { } keys && !keys.Contains(' ') && byId.ContainsKey(b.Command))
            .Select(b => (KeyGesture.Parse(b.Keys!), byId[b.Command])).ToList();
        _effective = [.. added, .. list];
    }

    private static List<(KeyGesture, EditorCommand)> Build()
    {
        var map = new List<(KeyGesture, EditorCommand)>();
        void Add(string gesture, string id, string title, Action<CodeEditor> run) => map.Add((KeyGesture.Parse(gesture), new EditorCommand(id, title, run)));

        foreach (var (shift, suffix) in new[] { ("", ""), ("Shift+", "Select") })
        {
            var select = shift.Length > 0;
            Add($"{shift}Left", $"cursorLeft{suffix}", "Caret left", e => Edits.Left(e.Document, select));
            Add($"{shift}Right", $"cursorRight{suffix}", "Caret right", e => Edits.Right(e.Document, select));
            Add($"{shift}Up", $"cursorUp{suffix}", "Caret up", e => e.CaretRows(-1, select));
            Add($"{shift}Down", $"cursorDown{suffix}", "Caret down", e => e.CaretRows(1, select));
            Add($"{shift}Ctrl+Left", $"cursorWordStartLeft{suffix}", "Word left", e => Edits.WordLeft(e.Document, select));
            Add($"{shift}Ctrl+Right", $"cursorWordEndRight{suffix}", "Word right", e => Edits.WordRight(e.Document, select));
            Add($"{shift}Home", $"cursorHome{suffix}", "Line start", e => e.LineHome(select));
            Add($"{shift}End", $"cursorEnd{suffix}", "Line end", e => e.LineEnd(select));
            Add($"{shift}Ctrl+Home", $"cursorTop{suffix}", "Document start", e => Edits.DocumentStart(e.Document, select));
            Add($"{shift}Ctrl+End", $"cursorBottom{suffix}", "Document end", e => Edits.DocumentEnd(e.Document, select));
            Add($"{shift}PageUp", $"cursorPageUp{suffix}", "Page up", e => e.Page(-1, select));
            Add($"{shift}PageDown", $"cursorPageDown{suffix}", "Page down", e => e.Page(1, select));
        }

        Add("Back", "deleteLeft", "Delete left", e => Edits.Backspace(e.Document, e.Options));
        Add("Shift+Back", "deleteLeft", "Delete left", e => Edits.Backspace(e.Document, e.Options));
        Add("Delete", "deleteRight", "Delete right", e => Edits.Delete(e.Document));
        Add("Ctrl+Back", "deleteWordLeft", "Delete word left", e => Edits.DeleteWordLeft(e.Document));
        Add("Ctrl+Delete", "deleteWordRight", "Delete word right", e => Edits.DeleteWordRight(e.Document));
        Add("Enter", "newLine", "New line", e => Edits.NewLine(e.Document, e.Options));
        Add("Tab", "tab", "Indent", e => Edits.Tab(e.Document, e.Options));
        Add("Shift+Tab", "outdent", "Outdent", e => Edits.OutdentLines(e.Document, e.Options));
        Add("Ctrl+OemCloseBrackets", "editor.action.indentLines", "Indent lines", e => Edits.IndentLines(e.Document, e.Options));
        Add("Ctrl+OemOpenBrackets", "editor.action.outdentLines", "Outdent lines", e => Edits.OutdentLines(e.Document, e.Options));
        Add("Ctrl+Z", "undo", "Undo", e => e.Document.Undo());
        Add("Ctrl+Shift+Z", "redo", "Redo", e => e.Document.Redo());
        Add("Ctrl+Y", "redo", "Redo", e => e.Document.Redo());
        Add("Ctrl+A", "editor.action.selectAll", "Select all", e => Edits.SelectAll(e.Document));
        Add("Escape", "removeSecondaryCursors", "Single caret", e =>
        {
            if (e.Find.IsOpen) e.Find.Close();
            else Edits.SingleCaret(e.Document);
        });
        Add("Ctrl+Shift+OemOpenBrackets", "editor.fold", "Fold", e => e.Fold());
        Add("Ctrl+Shift+OemCloseBrackets", "editor.unfold", "Unfold", e => e.Unfold());
        Add("Ctrl+F", "actions.find", "Find", e => e.Find.Open(replace: false));
        Add("Ctrl+H", "editor.action.startFindReplaceAction", "Replace", e => e.Find.Open(replace: true));
        Add("F3", "editor.action.nextMatchFindAction", "Find next", e => e.Find.Next());
        Add("Shift+F3", "editor.action.previousMatchFindAction", "Find previous", e => e.Find.Previous());
        Add("Ctrl+C", "editor.action.clipboardCopyAction", "Copy", e => _ = e.CopyAsync());
        Add("Ctrl+Insert", "editor.action.clipboardCopyAction", "Copy", e => _ = e.CopyAsync());
        Add("Ctrl+X", "editor.action.clipboardCutAction", "Cut", e => _ = e.CutAsync());
        Add("Shift+Delete", "editor.action.clipboardCutAction", "Cut", e => _ = e.CutAsync());
        Add("Ctrl+V", "editor.action.clipboardPasteAction", "Paste", e => _ = e.PasteAsync());
        Add("Shift+Insert", "editor.action.clipboardPasteAction", "Paste", e => _ = e.PasteAsync());
        Add("Ctrl+D", "editor.action.addSelectionToNextFindMatch", "Add next occurrence", e => Edits.AddNextOccurrence(e.Document));
        Add("Ctrl+Shift+L", "editor.action.selectHighlights", "Select all occurrences", e => Edits.SelectAllOccurrences(e.Document));
        Add(Windows ? "Ctrl+Alt+Up" : "Ctrl+Shift+Up", "editor.action.insertCursorAbove", "Add caret above", e => Edits.AddCaretVertically(e.Document, -1, e.Options));
        Add(Windows ? "Ctrl+Alt+Down" : "Ctrl+Shift+Down", "editor.action.insertCursorBelow", "Add caret below", e => Edits.AddCaretVertically(e.Document, 1, e.Options));
        Add("Alt+Up", "editor.action.moveLinesUpAction", "Move lines up", e => Edits.MoveLines(e.Document, -1));
        Add("Alt+Down", "editor.action.moveLinesDownAction", "Move lines down", e => Edits.MoveLines(e.Document, 1));
        Add(Windows ? "Shift+Alt+Up" : "Ctrl+Shift+Alt+Up", "editor.action.copyLinesUpAction", "Copy lines up", e => Edits.CopyLines(e.Document, -1));
        Add(Windows ? "Shift+Alt+Down" : "Ctrl+Shift+Alt+Down", "editor.action.copyLinesDownAction", "Copy lines down", e => Edits.CopyLines(e.Document, 1));
        Add("Ctrl+Shift+K", "editor.action.deleteLines", "Delete lines", e => Edits.DeleteLines(e.Document, e.Options));
        Add("Ctrl+Oem2", "editor.action.commentLine", "Toggle line comment", e => Edits.ToggleLineComment(e.Document, e.Options));
        Add(Windows ? "Shift+Alt+A" : "Ctrl+Shift+A", "editor.action.blockComment", "Toggle block comment", e => Edits.ToggleBlockComment(e.Document, e.Options));
        Add("Ctrl+Enter", "editor.action.insertLineAfter", "Insert line below", e => Edits.InsertLine(e.Document, above: false));
        Add("Ctrl+Shift+Enter", "editor.action.insertLineBefore", "Insert line above", e => Edits.InsertLine(e.Document, above: true));
        Add("Ctrl+Shift+OemPipe", "editor.action.jumpToBracket", "Jump to bracket", e => e.JumpToBracket());
        Add("Shift+Alt+I", "editor.action.insertCursorAtEndOfEachLineSelected", "Add carets to line ends", e => Edits.CaretsAtLineEnds(e.Document));
        Add("Ctrl+U", "cursorUndo", "Undo cursor", e => e.UndoCursor());
        Add("Shift+Alt+Right", "editor.action.smartSelect.expand", "Expand selection", e => e.ExpandSelection());
        Add("Shift+Alt+Left", "editor.action.smartSelect.shrink", "Shrink selection", e => e.ShrinkSelection());
        return map;
    }
}
