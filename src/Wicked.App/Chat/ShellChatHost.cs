using Wicked.App.Models;
using Wicked.App.ViewModels;
using Wicked.Core.Settings;
using Wicked.Editor.Model;

namespace Wicked.App.Chat;

/// <summary>The shell as the chat sees it: the settings, the active editor, the clipboard, the window's dialogs.</summary>
internal sealed class ShellChatHost : IChatHost
{
    private readonly ShellViewModel _shell;

    public ShellChatHost(ShellViewModel shell)
    {
        _shell = shell;
        shell.Workbench.ActiveEditorChanged += () => CanInsertChanged?.Invoke();
        // A folder opened or closed: the agent has tools, or has none.
        shell.Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(shell.Workbench.Root)) CanInsertChanged?.Invoke();
        };
    }

    private Wicked.App.Assistant.WorkspaceAgentTools? _agent;

    /// <summary>The agent's tools, for the folder that is open (none without one).</summary>
    public Wicked.App.Assistant.IAgentTools? Agent => _shell.Workbench.Root is null ? null : _agent ??= new Wicked.App.Assistant.WorkspaceAgentTools(_shell);

    public WickedSettings Settings => _shell.Settings;

    public string? ActiveFileName => _shell.Workbench.ActiveTab is { ViewFactory: null } tab ? tab.Title : null;

    /// <summary>
    /// Code from the chat proposed to the active editor: over its selection; over the whole file when the code is most of a
    /// file (at least half its lines); else at the caret. It opens as a review.
    /// </summary>
    public void ApplyToEditor(string text)
    {
        if (_shell.Workbench.ActiveTab is not { ViewFactory: null } tab) return;
        var doc = tab.Document;
        var p = doc.Primary;
        var lines = text.Split('\n').Length;
        var (start, end) = !p.IsEmpty ? (p.Start, p.End) : lines * 2 >= doc.Text.LineCount ? (0, doc.Text.Length) : (p.Active, p.Active);
        if (start == 0 && end == doc.Text.Length && doc.Text.Length > 0 && doc.Text[doc.Text.Length - 1] == '\n' && !text.EndsWith('\n')) text += "\n";
        _shell.OpenReview(tab, start, end, text, "from the assistant");
    }

    public Task<string> WithContextAsync(string text, bool activeFile) => Wicked.App.Assistant.MentionContext.ExpandAsync(_shell, text, activeFile);

    public Task UpdateSettingsAsync(Func<WickedSettings, WickedSettings> change) => _shell.UpdateSettingsAsync(change);

    public bool CanInsert => _shell.Workbench.ActiveTab is not null;

    public event Action? CanInsertChanged;

    public void InsertIntoEditor(string text)
    {
        if (_shell.Workbench.ActiveTab is not { } tab) return;
        // At the caret, or over the selection: the editing commands do it as a paste does, in one undo step.
        Edits.Paste(tab.Document, text, wholeLines: false);
        tab.Editor?.RevealCaret();
        tab.Editor?.Focus();
    }

    public Task SetClipboardAsync(string text) => _shell.Clipboard?.Invoke(text) ?? Task.CompletedTask;

    public Task<bool> ConfirmAsync(string message, string detail, string confirm) =>
        _shell.Workbench.Dialogs?.ConfirmAsync(message, detail, confirm) ?? Task.FromResult(false);

    public Task<string?> AskNameAsync(string prompt, string initial) =>
        _shell.Workbench.Dialogs?.AskNameAsync(prompt, initial) ?? Task.FromResult<string?>(null);

    public void OpenLink(string url)
    {
        // Only web links leave the window; a reply cannot make it open a file or run a program.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        _ = Task.Run(() => Opener.Open(uri.AbsoluteUri));
    }

    public void ShowModels() => _shell.Navigate(Shell.ShellRoutes.Models);
}
