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
            .. IndentationCommands(),
        ];
    }

    /// <summary>
    /// The file's indentation, as VS Code's status bar item offers it: spaces or tabs and how wide, read from the content again,
    /// or the whole file converted. A choice holds for this file only (the settings stay as they are).
    /// </summary>
    private IEnumerable<WorkbenchCommand> IndentationCommands()
    {
        Task OnEditor(Action<CodeEditor> run)
        {
            if (Workbench.ActiveTab?.Editor is { } editor) run(editor);
            OnActiveEditorChanged();
            return Task.CompletedTask;
        }

        // A width from 1 to 8, the one in force first; the choice applies <paramref name="pick"/>.
        Task PickWidth(Action<CodeEditor, int> pick) => OnEditor(editor => ShowList("Select Tab Size for Current File",
            [.. Enumerable.Range(1, 8).Select(n => new QuickItem(n.ToString(System.Globalization.CultureInfo.InvariantCulture), n == editor.Options.TabSize ? "current" : "", "", () => OnEditor(e => pick(e, n))))],
            editor.Options.TabSize - 1));

        return
        [
            new("editor.action.indentUsingSpaces", "View", "Indent Using Spaces", null, () => PickWidth((e, n) => e.SetIndentation(true, n))),
            new("editor.action.indentUsingTabs", "View", "Indent Using Tabs", null, () => PickWidth((e, n) => e.SetIndentation(false, n))),
            new("editor.action.changeTabDisplaySize", "View", "Change Tab Display Size", null, () => PickWidth((e, n) => e.SetIndentation(e.Options.InsertSpaces, n))),
            new("editor.action.detectIndentation", "View", "Detect Indentation from Content", null, async () =>
            {
                if (Workbench.ActiveTab?.Editor is { } editor) await editor.DetectIndentationAsync();
                OnActiveEditorChanged();
            }),
            new("editor.action.indentationToSpaces", "View", "Convert Indentation to Spaces", null, () => OnEditor(e =>
            {
                if (!e.Document.IsReadOnly) Edits.ConvertIndentation(e.Document, toSpaces: true, e.Options.TabSize);
                e.SetIndentation(true, e.Options.TabSize);
            })),
            new("editor.action.indentationToTabs", "View", "Convert Indentation to Tabs", null, () => OnEditor(e =>
            {
                if (!e.Document.IsReadOnly) Edits.ConvertIndentation(e.Document, toSpaces: false, e.Options.TabSize);
                e.SetIndentation(false, e.Options.TabSize);
            })),
            new("wicked.selectIndentation", "View", "Select Indentation...", null, () => OnEditor(_ => ShowList("Select Action",
            [
                .. new[] { "editor.action.indentUsingSpaces", "editor.action.indentUsingTabs", "editor.action.changeTabDisplaySize", "editor.action.detectIndentation",
                    "editor.action.indentationToSpaces", "editor.action.indentationToTabs" }
                    .Select(id => Commands.First(c => c.Id == id)).Select(c => new QuickItem(c.Title, "", "", c.Run)),
            ], 0))),
        ];
    }
}
