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

    private const string CommitInstructions = """
        You write git commit messages. From the diff, write one: a subject line in the imperative mood of at most 72
        characters, then (only when it helps) a blank line and a short body saying what changed and why. Reply with the
        message only, no quotes or fences.
        """;

    /// <summary>A commit message for the active repository's staged changes (or all of them when nothing is staged).</summary>
    private async Task<string?> WriteCommitMessageAsync(CancellationToken ct)
    {
        if (AssistantModel() is not { } model || SourceControl.ActiveGit is not { } repo) return null;
        var diff = await repo.DiffAsync(".", staged: true, ct: ct);
        if (diff.Trim().Length == 0) diff = await repo.DiffAsync(".", staged: false, ct: ct);
        if (diff.Trim().Length == 0) return null;
        if (diff.Length > 24_000) diff = diff[..24_000] + "\n… (cut)";
        var text = new System.Text.StringBuilder();
        try
        {
            await foreach (var chunk in Assistant.Chat.StreamAsync(new ChatRequest(model, [new ChatMessage(ChatRole.System, CommitInstructions), new ChatMessage(ChatRole.User, diff)], new ChatSampling(Temperature: 0.2)), ct))
            {
                if (chunk is ContentDelta d) text.Append(d.Text);
            }
        }
        catch (ChatException ex)
        {
            Notices.Post(new Wicked.Core.Notice("commit-message", $"No commit message: {ex.Message}", Wicked.Core.NoticeKind.Error));
            return null;
        }

        return InlineEdit.StripFences(text.ToString()).Trim();
    }

    /// <summary>A problem's Fix (an inline edit over its lines) or Explain (a question in the assistant).</summary>
    private void OnProblemAction(CodeEditor editor, Wicked.Core.Lsp.Diagnostic problem, string kind)
    {
        var text = editor.Document.Text;
        var first = Math.Clamp(problem.Range.Start.Line, 0, text.LineCount - 1);
        var last = Math.Clamp(problem.Range.End.Line, first, text.LineCount - 1);
        var start = text.LineStart(first);
        var end = last + 1 < text.LineCount ? text.LineStart(last + 1) : text.Length;
        if (kind == "fix")
        {
            _ = InlineEditFor(editor)?.AskAboutAsync(start, end, $"Fix this problem: {problem.Message}");
            return;
        }

        AssistantVisible = true;
        var file = editor.FilePath is { } path ? Path.GetFileName(path) : "this file";
        var fence = new string((char)96, 3);
        Chat.Draft = $"Explain this problem in {file} at line {first + 1} and how to fix it: {problem.Message}\n\n{fence}\n{text.Substring(start, end - start).TrimEnd()}\n{fence}";
        Chat.SendCommand.Execute(null);
    }

    private IEnumerable<Wicked.App.Shell.WorkbenchCommand> AssistantCommands()
    {
        Task Ghost() => GhostsFor(Workbench.ActiveTab?.Editor)?.TriggerAsync() ?? Task.CompletedTask;
        Task Inline()
        {
            InlineEditFor(Workbench.ActiveTab?.Editor)?.Open();
            return Task.CompletedTask;
        }

        Task Agent()
        {
            AssistantVisible = true;
            Chat.AgentMode = Chat.CanUseAgent && !Chat.AgentMode;
            return Task.CompletedTask;
        }

        return
        [
            new("editor.action.inlineSuggest.trigger", "Assistant", "Trigger Inline Suggestion", "Alt+OemPipe", Ghost),
            new("inlineChat.start", "Assistant", "Edit with the Assistant (Inline)", null, Inline),
            new("wicked.assistant.toggleAgent", "Assistant", "Toggle Agent Mode", null, Agent),
            new("git.generateCommitMessage", "Git", "Write Commit Message", null, async () =>
            {
                if (await WriteCommitMessageAsync(CancellationToken.None) is { Length: > 0 } message) SourceControl.CommitMessage = message;
            }),
            new("wicked.assistant.installCodingPack", "Assistant", "Install Coding Pack", null, InstallCodingPackAsync),
            new("wicked.assistant.explainSelection", "Assistant", "Explain with the Assistant", null, () => SelectionToChat(send: true)),
            new("wicked.assistant.addSelectionToChat", "Assistant", "Add Selection to Chat", null, () => SelectionToChat(send: false)),
        ];
    }

    /// <summary>
    /// The selection (or the caret's line) into the assistant's message as a quoted block with its file and lines: added to
    /// what is being written (Add to Chat), or sent as a question (Explain).
    /// </summary>
    private Task SelectionToChat(bool send)
    {
        if (Workbench.ActiveTab is not { Editor: { } editor } tab) return Task.CompletedTask;
        var text = editor.Document.Text;
        var selection = editor.Document.Primary;
        var (start, end) = (selection.Start, selection.End);
        if (start == end)
        {
            var line = text.LineOf(start);
            start = text.LineStart(line);
            end = line + 1 < text.LineCount ? text.LineStart(line + 1) : text.Length;
        }

        var first = text.LineOf(start) + 1;
        var last = text.LineOf(Math.Max(start, end - 1)) + 1;
        var file = editor.FilePath is { } path ? Path.GetFileName(path) : "this file";
        var where = first == last ? $"{file}, line {first}" : $"{file}, lines {first}–{last}";
        var fence = new string((char)96, 3);
        var quote = $"{where}:\n{fence}{tab.Language?.Id}\n{text.Substring(start, end - start).TrimEnd()}\n{fence}\n";
        AssistantVisible = true;
        if (send)
        {
            Chat.Draft = $"Explain this code from {quote}";
            Chat.SendCommand.Execute(null);
        }
        else
        {
            Chat.Draft = Chat.Draft.Length == 0 ? quote : Chat.Draft.TrimEnd() + "\n\n" + quote;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// The coding pack (D18): the Coding pick for this machine with its small completion companion, installed in one go
    /// (licences asked for first), from the Models panel.
    /// </summary>
    private async Task InstallCodingPackAsync()
    {
        Navigate(Wicked.App.Shell.ShellRoutes.Models);
        var card = Models.Picks.FirstOrDefault(p => p.Pick.Kind == Wicked.Core.Catalog.PickKind.Coding || p.Pick.AlsoFor.Contains(Wicked.Core.Catalog.PickKind.Coding));
        if (card is null)
        {
            Notices.Post(new Wicked.Core.Notice("coding-pack", "No coding model fits this machine in the catalog yet", Wicked.Core.NoticeKind.Info, Wicked.App.Shell.ShellRoutes.Models));
            return;
        }

        if (card.Installed) Notices.Post(new Wicked.Core.Notice("coding-pack", $"The coding pack ({card.Name}) is installed", Wicked.Core.NoticeKind.Success));
        else await card.InstallCommand.ExecuteAsync(null);
    }

    private void WireAssistant()
    {
        SourceControl.MessageWriter = WriteCommitMessageAsync;
        Language.ProblemAction = OnProblemAction;
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
