using Wicked.App.Chat;
using Wicked.App.Previews;
using Wicked.App.Shell;

namespace Wicked.App.ViewModels;

/// <summary>Markdown: Open Preview (Ctrl+Shift+V) and Open Preview to the Side (Ctrl+K V), as VS Code's built-in Markdown has.</summary>
public sealed partial class ShellViewModel
{
    private IEnumerable<WorkbenchCommand> PreviewCommands() =>
    [
        new("markdown.showPreview", "Markdown", "Open Preview", "Ctrl+Shift+V", () => ShowMarkdownPreview(toSide: false)),
        new("markdown.showPreviewToSide", "Markdown", "Open Preview to the Side", "Ctrl+K V", () => ShowMarkdownPreview(toSide: true)),
    ];

    private Task ShowMarkdownPreview(bool toSide)
    {
        if (Workbench.ActiveTab is not { ViewFactory: null } tab) return Task.CompletedTask;
        if (tab.Language?.Id != "markdown")
        {
            Notices.Post(new Wicked.Core.Notice("preview", "Open Preview is for Markdown files", Wicked.Core.NoticeKind.Info));
            return Task.CompletedTask;
        }

        var document = tab.Document;
        var actions = new PreviewActions(this);
        Workbench.OpenView($"Preview {tab.Title}", () => new MarkdownPreviewView(document, actions), viewPath: tab.Path ?? tab.Tooltip,
            group: toSide ? Workbench.SideGroup() : null);
        return Task.CompletedTask;
    }

    /// <summary>A preview's code blocks: Copy goes to the clipboard; links open in the browser (web links only); nothing is inserted.</summary>
    private sealed class PreviewActions(ShellViewModel shell) : ICodeActions
    {
        public bool CanInsert => false;

        public event EventHandler? CanInsertChanged
        {
            add { }
            remove { }
        }

        public void Copy(string text) => _ = shell.Clipboard?.Invoke(text);

        public void Insert(string text)
        {
        }

        public void Apply(string text)
        {
        }

        public void OpenLink(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") shell.Models.OpenExternal(uri.AbsoluteUri);
        }
    }
}
