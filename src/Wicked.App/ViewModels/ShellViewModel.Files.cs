using Wicked.App.Git;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Files;
using Wicked.Editor.Syntax;

namespace Wicked.App.ViewModels;

/// <summary>
/// The File menu's basics with VS Code's ids and keys: closing tabs in bulk, Reopen Closed Editor, Revert, Auto Save, Copy
/// Path, Reveal, Compare with Saved, Open Recent, Close Folder, Quit, and the active file's language, line breaks and
/// encoding.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>Quit (Ctrl+Q): the window closes, asking once about every unsaved file.</summary>
    public event Action? QuitRequested;

    private IEnumerable<WorkbenchCommand> FileCommands()
    {
        var bench = Workbench;
        Task OnTab(Func<Wicked.App.Workbench.EditorTab, Task> run) => bench.ActiveTab is { } tab ? run(tab) : Task.CompletedTask;
        Task Copy(Func<Wicked.App.Workbench.EditorTab, string?> text) =>
            bench.ActiveTab is { } tab && text(tab) is { } value && Clipboard is { } clipboard ? clipboard(value) : Task.CompletedTask;
        var windows = OperatingSystem.IsWindows();
        return
        [
            new("workbench.action.closeAllEditors", "View", "Close All Editors", "Ctrl+K Ctrl+W", bench.CloseAllAsync),
            new("workbench.action.closeOtherEditors", "View", "Close Other Editors in Group", null, () => OnTab(tab => bench.CloseOthersAsync(tab, bench.ActiveGroup))),
            new("workbench.action.closeEditorsToTheRight", "View", "Close Editors to the Right in Group", null, () => OnTab(tab => bench.CloseToTheRightAsync(tab, bench.ActiveGroup))),
            new("workbench.action.closeUnmodifiedEditors", "View", "Close Saved Editors in Group", "Ctrl+K U", () => bench.CloseSavedAsync(bench.ActiveGroup)),
            new("workbench.action.reopenClosedEditor", "View", "Reopen Closed Editor", "Ctrl+Shift+T", bench.ReopenClosedAsync),
            new("workbench.action.files.revert", "File", "Revert File", null, () => OnTab(bench.RevertAsync)),
            new("workbench.action.toggleAutoSave", "File", "Toggle Auto Save", null,
                () => UpdateSettingsAsync(s => s with { AutoSave = s.AutoSave == "off" ? "afterDelay" : "off" })),
            new("workbench.action.files.copyPathOfActiveFile", "File", "Copy Path of Active File", "Ctrl+K P", () => Copy(tab => tab.Path)),
            new("workbench.action.files.copyRelativePathOfActiveFile", "File", "Copy Relative Path of Active File", "Ctrl+K Ctrl+Shift+C",
                () => Copy(tab => tab.Path is { } path && bench.Root is { } root ? Path.GetRelativePath(root, path) : tab.Path)),
            new("workbench.files.action.showActiveFileInExplorer", "File", "Reveal Active File in Explorer View", null, () => OnTab(async tab =>
            {
                if (tab.Path is not { } path) return;
                Navigate(ShellRoutes.Explorer);
                await bench.RevealAsync(path);
            })),
            new("revealFileInOS", "File", windows ? "Reveal in File Explorer" : "Open Containing Folder", windows ? "Shift+Alt+R" : "Ctrl+Alt+R", () => OnTab(tab =>
            {
                if (tab.Path is { } path) Wicked.App.Workbench.Workbench.Reveal(path);
                return Task.CompletedTask;
            })),
            new("workbench.files.action.compareWithSaved", "File", "Compare Active File with Saved", "Ctrl+K D", () => OnTab(tab =>
            {
                if (tab.Path is not { } path) return Task.CompletedTask;
                var title = $"{tab.Title} (on disk) ↔ {tab.Title}";
                var document = tab.Document;
                Workbench.OpenView(title, () => new DiffEditorView(title, path, () => Wicked.App.Workbench.Workbench.SavedTextAsync(path),
                    () => Task.FromResult(document.Text.ToString()), () => bench.OpenAsync(path), Settings));
                return Task.CompletedTask;
            })),
            new("workbench.action.openRecent", "File", "Open Recent...", "Ctrl+R", () =>
            {
                ShowRecentList();
                return Task.CompletedTask;
            }),
            new("workbench.action.clearRecentFiles", "File", "Clear Recently Opened", null, ClearRecentAsync),
            new("workbench.action.closeFolder", "File", "Close Folder", "Ctrl+K F", CloseFolderAsync),
            // Each window is its own Wicked: closing it is quitting that one (unsaved files kept or asked about as at Quit).
            new("workbench.action.closeWindow", "File", "Close Window", "Ctrl+Shift+W", () =>
            {
                QuitRequested?.Invoke();
                return Task.CompletedTask;
            }),
            new("workbench.action.quit", "File", "Quit", "Ctrl+Q", () =>
            {
                QuitRequested?.Invoke();
                return Task.CompletedTask;
            }),
            new("workbench.action.editor.changeLanguageMode", "View", "Change Language Mode", "Ctrl+K M", () => OnTab(tab =>
            {
                ShowLanguageList(tab);
                return Task.CompletedTask;
            })),
            new("workbench.action.editor.changeEOL", "View", "Change End of Line Sequence", null, () => OnTab(tab =>
            {
                ShowList("Select End of Line Sequence",
                [
                    new QuickItem("LF", "\\n — Linux and macOS", "", () => ChangeLineBreak(tab, "\n")),
                    new QuickItem("CRLF", "\\r\\n — Windows", "", () => ChangeLineBreak(tab, "\r\n")),
                ], tab.Document.LineBreak == "\r\n" ? 1 : 0);
                return Task.CompletedTask;
            })),
            new("workbench.action.editor.changeEncoding", "View", "Change File Encoding", null, () => OnTab(tab =>
            {
                ShowEncodingChoice(tab);
                return Task.CompletedTask;
            })),
        ];
    }

    private Task ChangeLineBreak(Wicked.App.Workbench.EditorTab tab, string lineBreak)
    {
        tab.Document.ChangeLineBreak(lineBreak);
        OnActiveEditorChanged();
        return Task.CompletedTask;
    }

    /// <summary>Open Recent (Ctrl+R): the folders opened before, newest first; Enter opens one.</summary>
    private void ShowRecentList()
    {
        var folders = Workbench.RecentFolders.Where(f => f != Workbench.Root).ToList();
        if (folders.Count == 0)
        {
            Notices.Post(new Notice("recent", "No folders opened recently", NoticeKind.Info));
            return;
        }

        ShowList("Open a recent folder", [.. folders.Select(f => new QuickItem(Path.GetFileName(f.TrimEnd(Path.DirectorySeparatorChar)), f, "", () => OpenFolderAndRememberAsync(f)))], 0);
    }

    private Task ClearRecentAsync()
    {
        Workbench.RecentFolders.Clear();
        _session = _session with { RecentFolders = [] };
        return SaveSessionAsync();
    }

    private async Task CloseFolderAsync()
    {
        if (Workbench.Root is null || !await Workbench.CloseFolderAsync()) return;
        _session = _session with { LastFolder = null };
        await SaveSessionAsync();
    }

    /// <summary>Change Language Mode (Ctrl+K M): every language, the current one first; the choice recolours the file.</summary>
    private void ShowLanguageList(Wicked.App.Workbench.EditorTab tab)
    {
        var current = tab.Language?.Id ?? "plaintext";
        var ids = Languages.All().Where(id => id != current).Prepend(current).ToList();
        ShowList("Select Language Mode", [.. ids.Select(id => new QuickItem(id, id == current ? "current" : "", "", () =>
        {
            if (id != current) Workbench.ChangeLanguage(tab, Languages.ForName(id));
            OnActiveEditorChanged();
            return Task.CompletedTask;
        }))], 0);
    }

    /// <summary>Change File Encoding: reopen the file read as an encoding, or save it in one, as VS Code offers.</summary>
    private void ShowEncodingChoice(Wicked.App.Workbench.EditorTab tab)
    {
        FileEncoding[] encodings = [FileEncoding.Utf8, new("utf-8", true), new("utf-16le", true), new("utf-16be", true), new("latin1", false)];
        IReadOnlyList<QuickItem> Pick(Func<FileEncoding, Task> then) =>
            [.. encodings.Select(e => new QuickItem(e.Label, e == tab.Encoding ? "current" : "", "", () => then(e)))];
        ShowList("Select Action",
        [
            new QuickItem("Save with Encoding", "", "", () =>
            {
                ShowList("Select File Encoding to Save with", Pick(async encoding =>
                {
                    tab.Encoding = encoding;
                    await Workbench.SaveAsync(tab);
                    OnActiveEditorChanged();
                }), Array.IndexOf(encodings, tab.Encoding));
                return Task.CompletedTask;
            }),
            new QuickItem("Reopen with Encoding", tab.IsDirty ? "unsaved changes are dropped" : "", "", () =>
            {
                ShowList("Select File Encoding to Reopen with", Pick(async encoding =>
                {
                    await Workbench.ReopenWithEncodingAsync(tab, encoding);
                    OnActiveEditorChanged();
                }), Array.IndexOf(encodings, tab.Encoding));
                return Task.CompletedTask;
            }),
        ], 0);
    }
}
