using Wicked.App.Language;
using Wicked.App.Editor;
using Wicked.App.Shell;

namespace Wicked.App.ViewModels;

/// <summary>The language servers' commands: go to, references, rename, format, quick fixes, symbols, problems.</summary>
public sealed partial class ShellViewModel
{
    private EditorLanguageUi? ActiveUi => Language.UiFor(Workbench.ActiveTab?.Editor);

    private IEnumerable<WorkbenchCommand> LanguageCommands()
    {
        Task Ui(Func<EditorLanguageUi, Task> run) => ActiveUi is { } ui ? run(ui) : Task.CompletedTask;
        return
        [
            new("editor.action.revealDefinition", "Go", "Go to Definition", "F12", () => Language.GoToAsync(Workbench.ActiveTab, "definition")),
            new("editor.action.revealDeclaration", "Go", "Go to Declaration", null, () => Language.GoToAsync(Workbench.ActiveTab, "declaration")),
            new("editor.action.goToTypeDefinition", "Go", "Go to Type Definition", null, () => Language.GoToAsync(Workbench.ActiveTab, "typeDefinition")),
            new("editor.action.goToImplementation", "Go", "Go to Implementations", "Ctrl+F12", () => Language.GoToAsync(Workbench.ActiveTab, "implementation")),
            new("editor.action.goToReferences", "Go", "Go to References", "Shift+F12", () => Language.ReferencesAsync(Workbench.ActiveTab)),
            new("editor.action.rename", "Edit", "Rename Symbol", "F2", () => Ui(ui => ui.StartRenameAsync())),
            new("editor.action.formatDocument", "Edit", "Format Document", "Shift+Alt+F", () => Workbench.ActiveTab is { } t ? Language.FormatAsync(t) : Task.CompletedTask),
            new("editor.action.formatSelection", "Edit", "Format Selection", "Ctrl+K Ctrl+F", () => Workbench.ActiveTab is { } t ? Language.FormatAsync(t, selection: true) : Task.CompletedTask),
            new("editor.action.quickFix", "Edit", "Quick Fix...", "Ctrl+OemPeriod", () => Ui(ui => ui.ShowActionsAsync())),
            new("editor.action.refactor", "Edit", "Refactor...", "Ctrl+Shift+R", () => Ui(ui => ui.ShowActionsAsync("refactor"))),
            new("editor.action.sourceAction", "Edit", "Source Action...", null, () => Ui(ui => ui.ShowActionsAsync("source"))),
            new("editor.action.triggerSuggest", "Edit", "Trigger Suggest", null, () => Ui(ui => ui.TriggerSuggestAsync())),
            new("editor.action.triggerParameterHints", "Edit", "Trigger Parameter Hints", null, () => Ui(ui => ui.TriggerSignatureAsync())),
            new("editor.action.showHover", "View", "Show Hover", "Ctrl+K Ctrl+I", () => Ui(ui => ui.ShowHoverAtCaretAsync())),
            new("editor.action.marker.nextInFiles", "Go", "Go to Next Problem", "F8", () => Language.NextProblemAsync(Workbench.ActiveTab, 1)),
            new("editor.action.marker.prevInFiles", "Go", "Go to Previous Problem", "Shift+F8", () => Language.NextProblemAsync(Workbench.ActiveTab, -1)),
            new("workbench.action.gotoSymbol", "Go", "Go to Symbol in Editor...", "Ctrl+Shift+O", () => Sync(() => OpenPalette("@"))),
            new("workbench.action.showAllSymbols", "Go", "Go to Symbol in Workspace...", "Ctrl+T", () => Sync(() => OpenPalette("#"))),
            new("wicked.lsp.restart", "Language", "Restart Language Server", null, () => Language.RestartAsync(Workbench.ActiveTab)),
            new("bookmarks.toggle", "Bookmarks", "Toggle Bookmark", "Ctrl+Alt+K", () => Sync(() => _bookmarks.Toggle(Workbench.ActiveTab))),
            new("bookmarks.jumpToNext", "Bookmarks", "Jump to Next", "Ctrl+Alt+L", () => Sync(() => _bookmarks.Step(Workbench.ActiveTab, 1))),
            new("bookmarks.jumpToPrevious", "Bookmarks", "Jump to Previous", "Ctrl+Alt+J", () => Sync(() => _bookmarks.Step(Workbench.ActiveTab, -1))),
            new("bookmarks.list", "Bookmarks", "List", null, () => Sync(ListBookmarks)),
            new("editor.action.peekDefinition", "Go", "Peek Definition", "Alt+F12", () => Ui(ui => ui.PeekDefinitionAsync())),
            new("references-view.showCallHierarchy", "Go", "Show Call Hierarchy", "Shift+Alt+H", () => Language.CallHierarchyAsync(Workbench.ActiveTab)),
            new("references-view.showOutgoingCalls", "Go", "Show Outgoing Calls", null, () => Language.CallHierarchyAsync(Workbench.ActiveTab, incoming: false)),
        ];

        static Task Sync(Action a)
        {
            a();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A missing server's notice clicked: what installing it runs, to confirm or leave (the install writes only into
    /// Wicked's own servers folder), its progress in the Output panel.
    /// </summary>
    private void OfferServerInstall(string serverId)
    {
        if (Wicked.Core.Lsp.LanguageServerCatalog.ById(serverId) is not { } spec) return;
        if (Wicked.Core.Lsp.LanguageServerInstaller.MissingTool(spec) is { } missing)
        {
            Notices.Post(new Wicked.Core.Notice($"lsp:install:{spec.Id}", missing, Wicked.Core.NoticeKind.Warning));
            return;
        }

        var how = spec.Install switch
        {
            Wicked.Core.Lsp.InstallKind.GitHubRelease => $"download the latest release from github.com/{spec.Repository}",
            Wicked.Core.Lsp.InstallKind.Npm => $"npm install {spec.Package}",
            Wicked.Core.Lsp.InstallKind.DotnetTool => $"dotnet tool install {spec.Package}",
            _ => $"go install {spec.Package}",
        };
        var items = new List<QuickItem>
        {
            new($"Install {spec.Name}", $"{how}, into {Path.Combine(Language.ServersFolder, spec.Id)}", "", () => Language.InstallAsync(spec)),
            new("Not now", "", "", () => Task.CompletedTask),
        };
        QuickInput.OpenList($"{spec.Name} gives {string.Join(", ", spec.Languages)} files their completions, problems and navigation", items, 0, null);
        PaletteRequested?.Invoke("");
    }

    private Bookmarks? _bookmarksMade;

    private Bookmarks _bookmarks => _bookmarksMade ??= new Bookmarks(Workbench);

    private void ListBookmarks()
    {
        var items = _bookmarks.All().Select(b => new QuickItem($"{b.Tab.Title}:{b.Line + 1}  {b.Tab.Document.Text.Line(b.Line).Trim()}", b.Tab.Path ?? "", "", () =>
        {
            _bookmarks.Go(b);
            return Task.CompletedTask;
        })).ToList();
        QuickInput.OpenList(items.Count == 0 ? "No bookmarks: Ctrl+Alt+K marks a line" : "Bookmarks", items, 0, null);
        PaletteRequested?.Invoke("");
    }

    /// <summary>The language layer's ways into the window: opening a place, listing places.</summary>
    private void WireLanguage()
    {
        Language.OpenLocation = (path, range) => OpenAtAsync(path, range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);
        BottomPanel.Add(PanelIds.References, "REFERENCES", () => new Views.ReferencesView { DataContext = Language.References });
        Language.ReferencesShown += () => BottomPanel.Show(PanelIds.References);
        Language.ShowList = (title, items) =>
        {
            QuickInput.OpenList(title, items, 0, null);
            PaletteRequested?.Invoke("");
        };
    }
}
