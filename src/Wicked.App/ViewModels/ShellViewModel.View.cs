using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Editor.Model;

namespace Wicked.App.ViewModels;

/// <summary>
/// The View menu's editor toggles (minimap, whitespace, line numbers, breadcrumbs: each a setting, written to settings.json
/// as VS Code's toggles do) and Go to Next / Previous Change over the git marks in the gutter (Alt+F5, Shift+Alt+F5).
/// </summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<WorkbenchCommand> ViewCommands() =>
    [
        new("editor.action.toggleMinimap", "View", "Toggle Minimap", null, () => UpdateSettingsAsync(s => s with { Minimap = !s.Minimap })),
        new("editor.action.toggleRenderWhitespace", "View", "Toggle Render Whitespace", null,
            () => UpdateSettingsAsync(s => s with { RenderWhitespace = s.RenderWhitespace == "none" ? "all" : "none" })),
        new("wicked.toggleLineNumbers", "View", "Toggle Line Numbers", null,
            () => UpdateSettingsAsync(s => s with { LineNumbers = s.LineNumbers == "off" ? "on" : "off" })),
        new("breadcrumbs.toggle", "View", "Toggle Breadcrumbs", null, () => UpdateSettingsAsync(s => s with { Breadcrumbs = !s.Breadcrumbs })),
        new("workbench.action.editor.nextChange", "Go", "Go to Next Change", "Alt+F5", () => GoToChange(1)),
        new("workbench.action.editor.previousChange", "Go", "Go to Previous Change", "Shift+Alt+F5", () => GoToChange(-1)),
    ];

    /// <summary>The caret goes to the first line of the next (or previous) block of changed lines, wrapping round the file.</summary>
    private Task GoToChange(int direction)
    {
        if (Workbench.ActiveTab is not { } tab || !_trackers.TryGetValue(tab.Document, out var entry)) return Task.CompletedTask;
        var lines = entry.Tracker.Marks.Select(m => m.Line).Distinct().Order().ToList();
        // The first line of each block of consecutive changed lines.
        var starts = lines.Where((line, i) => i == 0 || lines[i - 1] != line - 1).ToList();
        if (starts.Count == 0)
        {
            Notices.Post(new Notice("changes", "No changes in this file", NoticeKind.Info));
            return Task.CompletedTask;
        }

        var doc = tab.Document;
        var here = doc.Text.LineOf(doc.Primary.Active);
        var target = direction > 0
            ? starts.FirstOrDefault(l => l > here, starts[0])
            : starts.LastOrDefault(l => l < here, starts[^1]);
        var offset = doc.Text.LineStart(Math.Min(target, doc.Text.LineCount - 1));
        doc.SetSelections([Selection.Caret(offset)]);
        tab.Editor?.RevealCaret();
        return Task.CompletedTask;
    }
}
