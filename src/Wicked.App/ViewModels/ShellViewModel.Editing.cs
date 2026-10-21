using Wicked.App.Editor;
using Wicked.App.Shell;
using Wicked.Editor.Model;

namespace Wicked.App.ViewModels;

/// <summary>
/// The editor's text tools that VS Code runs from the palette (no default keys there either, except Trim Trailing
/// Whitespace): duplicate, select the line, change case, sort, reverse and join lines. They act on the active editor.
/// </summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<WorkbenchCommand> EditingCommands()
    {
        Task OnEditor(Action<CodeEditor> run)
        {
            if (Workbench.ActiveTab?.Editor is { } editor && !editor.Document.IsReadOnly) run(editor);
            return Task.CompletedTask;
        }

        Task OnDocument(Func<Document, bool> run) => OnEditor(e => run(e.Document));

        return
        [
            new("editor.action.duplicateSelection", "Selection", "Duplicate Selection", null, () => OnDocument(Edits.DuplicateSelection)),
            new("expandLineSelection", "Selection", "Expand Line Selection", null, () => OnDocument(Edits.SelectLines)),
            new("editor.action.transformToUppercase", "Edit", "Transform to Uppercase", null, () => OnDocument(d => Edits.TransformCase(d, t => t.ToUpperInvariant()))),
            new("editor.action.transformToLowercase", "Edit", "Transform to Lowercase", null, () => OnDocument(d => Edits.TransformCase(d, t => t.ToLowerInvariant()))),
            new("editor.action.transformToTitlecase", "Edit", "Transform to Title Case", null, () => OnDocument(d => Edits.TransformCase(d, Edits.TitleCase))),
            new("editor.action.sortLinesAscending", "Edit", "Sort Lines Ascending", null, () => OnDocument(d => Edits.ReorderLines(d, Edits.SortAscending))),
            new("editor.action.sortLinesDescending", "Edit", "Sort Lines Descending", null, () => OnDocument(d => Edits.ReorderLines(d, Edits.SortDescending))),
            new("editor.action.reverseLines", "Edit", "Reverse Lines", null, () => OnDocument(d => Edits.ReorderLines(d, Edits.Reverse))),
            new("editor.action.joinLines", "Edit", "Join Lines", null, () => OnDocument(Edits.JoinLines)),
            new("editor.action.trimTrailingWhitespace", "Edit", "Trim Trailing Whitespace", "Ctrl+K Ctrl+X", () => OnDocument(d => Edits.TrimTrailingWhitespace(d))),
        ];
    }
}
