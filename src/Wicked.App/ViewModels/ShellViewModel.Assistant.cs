using System.Runtime.CompilerServices;
using Wicked.App.Assistant;
using Wicked.App.Chat;
using Wicked.App.Editor;
using Wicked.Core.Chat;

namespace Wicked.App.ViewModels;

/// <summary>The assistant inside the editors: ghost-text suggestions from a local coding model.</summary>
public sealed partial class ShellViewModel
{
    private readonly ConditionalWeakTable<CodeEditor, InlineCompletions> _ghosts = new();
    private readonly ConditionalWeakTable<CodeEditor, InlineEdit> _inlineEdits = new();

    /// <summary>The inline edit box of an editor (Ctrl+I).</summary>
    public InlineEdit? InlineEditFor(CodeEditor? editor) => editor is not null && _inlineEdits.TryGetValue(editor, out var e) ? e : null;

    /// <summary>
    /// The model for the assistant's code work (inline edits, reviews): the one the settings name, else the chat's, else a
    /// loaded model, else an installed one.
    /// </summary>
    public string? AssistantModel()
    {
        var models = Assistant.Runtime.Models;
        if (Settings.AssistantModel is { Length: > 0 } chosen && models.Any(m => m.Id == chosen)) return chosen;
        if (Chat.SelectedModel?.Id is { } selected) return selected;
        return models.OrderByDescending(m => m.State == RunState.Loaded).Select(m => m.Id).FirstOrDefault();
    }

    /// <summary>Opens a proposal over a stretch of a tab's document as a review tab; it closes itself when settled.</summary>
    public EditReviewView? OpenReview(Wicked.App.Workbench.EditorTab tab, int start, int end, string proposed, string why)
    {
        if (tab.Document.Text.Substring(start, end - start) == proposed) return null;
        var proposal = new EditProposal(tab, start, end, proposed);
        var title = $"Review: {tab.Title}";
        Wicked.App.Workbench.EditorTab? reviewTab = null;
        EditReviewView? view = null;
        reviewTab = Workbench.OpenView(title, () => view = new EditReviewView(proposal, $"{tab.Title}: {why}", () => _post(() =>
        {
            if (reviewTab is null) return;
            var group = Workbench.Groups.FirstOrDefault(g => g.Tabs.Contains(reviewTab));
            if (group is not null) _ = Workbench.CloseAsync(reviewTab, group);
            if (Workbench.AllTabs.Contains(tab)) Workbench.Focus(Workbench.Groups.First(g => g.Tabs.Contains(tab)));
        }), Settings));
        return view ?? reviewTab.CustomView as EditReviewView;
    }

    /// <summary>The model half as the assistant uses it (tests supply fakes through <see cref="AssistantServices.Factory"/>).</summary>
    public AssistantServices Assistant { get; }

    /// <summary>The ghost text of an editor, when it has it.</summary>
    public InlineCompletions? GhostsFor(CodeEditor? editor) => editor is not null && _ghosts.TryGetValue(editor, out var g) ? g : null;

    private void WireAssistant()
    {
        Workbench.TabOpened += tab =>
        {
            if (tab.ViewFactory is not null) return;
            if (tab.Editor is { } made) AttachGhosts(made);
            tab.EditorCreated += AttachGhosts;
        };
    }

    private void AttachGhosts(CodeEditor editor)
    {
        if (!_inlineEdits.TryGetValue(editor, out _))
        {
            _inlineEdits.Add(editor, new InlineEdit(editor, Assistant.Chat, AssistantModel, (start, end, code, why) =>
            {
                var tab = Workbench.AllTabs.FirstOrDefault(t => t.Editor == editor);
                if (tab is not null) OpenReview(tab, start, end, code, why);
            }));
        }

        if (Assistant.Infill is not { } engine || _ghosts.TryGetValue(editor, out _)) return;
        _ghosts.Add(editor, new InlineCompletions(editor, engine, CompletionModel, () => Neighbours(editor), () => Settings));
    }

    /// <summary>
    /// The model that writes suggestions: the one the settings name, else a loaded model built for code, else an installed
    /// one (it loads on the first suggestion). Null when there is none: the assistant says so when asked directly.
    /// </summary>
    public string? CompletionModel()
    {
        var models = Assistant.Runtime.Models;
        if (Settings.CompletionModel is { Length: > 0 } chosen) return models.Any(m => m.Id == chosen) ? chosen : null;
        static bool Coder(RunningModel m) =>
            new[] { "coder", "starcoder", "codegemma", "codellama", "fim", "codestral", "devstral" }.Any(k => m.Id.Contains(k, StringComparison.OrdinalIgnoreCase) || m.DisplayName.Contains(k, StringComparison.OrdinalIgnoreCase));
        return models.Where(Coder).OrderByDescending(m => m.State == RunState.Loaded).Select(m => m.Id).FirstOrDefault();
    }

    /// <summary>Up to three other open files as context, those of the same language first (their text read on the worker).</summary>
    private IReadOnlyList<InfillContext> Neighbours(CodeEditor editor)
    {
        var language = editor.Language?.Id;
        return [.. Workbench.AllTabs
            .Where(t => t.Path is not null && t.Editor != editor && t.ViewFactory is null)
            .DistinctBy(t => t.Path)
            .OrderByDescending(t => t.Language?.Id == language)
            .Take(3)
            .Select(t => new InfillContext(Workbench.Root is { } root ? Path.GetRelativePath(root, t.Path!) : Path.GetFileName(t.Path!), t.Document.Text.Length > 4000 ? t.Document.Text.Substring(0, 4000) : t.Document.Text.ToString()))];
    }
}
