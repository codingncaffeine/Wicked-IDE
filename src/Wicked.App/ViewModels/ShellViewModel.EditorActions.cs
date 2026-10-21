using Wicked.App.Editor;
using Wicked.App.Shell;

namespace Wicked.App.ViewModels;

/// <summary>
/// The code editor's own keyed actions (undo, copy, comment, move lines...) as commands the palette and the menus list,
/// with VS Code's ids and titles. Their keys stay the editor's (EditorKeymap): the workbench shows them but never binds
/// them, so a key still reaches the editor first and does what it always did.
/// </summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<WorkbenchCommand> EditorActionCommands()
    {
        // (id, category, title, changes the text)
        (string Id, string Category, string Title, bool Edits)[] actions =
        [
            ("undo", "Edit", "Undo", true),
            ("redo", "Edit", "Redo", true),
            ("editor.action.clipboardCutAction", "Edit", "Cut", true),
            ("editor.action.clipboardCopyAction", "Edit", "Copy", false),
            ("editor.action.clipboardPasteAction", "Edit", "Paste", true),
            ("actions.find", "Edit", "Find", false),
            ("editor.action.startFindReplaceAction", "Edit", "Replace", false),
            ("editor.action.nextMatchFindAction", "Edit", "Find Next", false),
            ("editor.action.previousMatchFindAction", "Edit", "Find Previous", false),
            ("editor.action.commentLine", "Edit", "Toggle Line Comment", true),
            ("editor.action.blockComment", "Edit", "Toggle Block Comment", true),
            ("editor.action.indentLines", "Edit", "Indent Line", true),
            ("editor.action.outdentLines", "Edit", "Outdent Line", true),
            ("editor.action.deleteLines", "Edit", "Delete Line", true),
            ("editor.action.insertLineAfter", "Edit", "Insert Line Below", true),
            ("editor.action.insertLineBefore", "Edit", "Insert Line Above", true),
            ("editor.action.selectAll", "Selection", "Select All", false),
            ("editor.action.smartSelect.expand", "Selection", "Expand Selection", false),
            ("editor.action.smartSelect.shrink", "Selection", "Shrink Selection", false),
            ("editor.action.copyLinesUpAction", "Selection", "Copy Line Up", true),
            ("editor.action.copyLinesDownAction", "Selection", "Copy Line Down", true),
            ("editor.action.moveLinesUpAction", "Selection", "Move Line Up", true),
            ("editor.action.moveLinesDownAction", "Selection", "Move Line Down", true),
            ("editor.action.insertCursorAbove", "Selection", "Add Cursor Above", false),
            ("editor.action.insertCursorBelow", "Selection", "Add Cursor Below", false),
            ("editor.action.insertCursorAtEndOfEachLineSelected", "Selection", "Add Cursors to Line Ends", false),
            ("editor.action.addSelectionToNextFindMatch", "Selection", "Add Next Occurrence", false),
            ("editor.action.selectHighlights", "Selection", "Select All Occurrences", false),
            ("editor.action.changeAll", "Edit", "Change All Occurrences", false),
            ("cursorUndo", "Selection", "Cursor Undo", false),
            ("editor.action.jumpToBracket", "Go", "Go to Bracket", false),
            ("editor.fold", "View", "Fold", false),
            ("editor.unfold", "View", "Unfold", false),
        ];

        var byId = EditorKeymap.Defaults.Select(d => d.Command).DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        foreach (var (id, category, title, edits) in actions)
        {
            if (!byId.TryGetValue(id, out var command)) continue;
            yield return new WorkbenchCommand(id, category, title, null, () =>
            {
                if (Workbench.ActiveTab?.Editor is { } editor && !(edits && editor.Document.IsReadOnly))
                {
                    command.Run(editor);
                    editor.Focus();
                }

                return Task.CompletedTask;
            }) { EditorKeys = true };
        }
    }
}
