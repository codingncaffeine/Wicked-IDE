using Wicked.App.Language;
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
            new("editor.action.triggerSuggest", "Edit", "Trigger Suggest", null, () => Ui(ui => ui.TriggerSuggestAsync())),
            new("editor.action.triggerParameterHints", "Edit", "Trigger Parameter Hints", null, () => Ui(ui => ui.TriggerSignatureAsync())),
            new("editor.action.showHover", "View", "Show Hover", "Ctrl+K Ctrl+I", () => Ui(ui => ui.ShowHoverAtCaretAsync())),
            new("editor.action.marker.nextInFiles", "Go", "Go to Next Problem", "F8", () => Language.NextProblemAsync(Workbench.ActiveTab, 1)),
            new("editor.action.marker.prevInFiles", "Go", "Go to Previous Problem", "Shift+F8", () => Language.NextProblemAsync(Workbench.ActiveTab, -1)),
            new("workbench.action.gotoSymbol", "Go", "Go to Symbol in Editor...", "Ctrl+Shift+O", () => Sync(() => OpenPalette("@"))),
            new("workbench.action.showAllSymbols", "Go", "Go to Symbol in Workspace...", "Ctrl+T", () => Sync(() => OpenPalette("#"))),
            new("wicked.lsp.restart", "Language", "Restart Language Server", null, () => Language.RestartAsync(Workbench.ActiveTab)),
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

    /// <summary>The language layer's ways into the window: opening a place, listing places.</summary>
    private void WireLanguage()
    {
        Language.OpenLocation = (path, range) => OpenAtAsync(path, range.Start.Line, range.Start.Character, range.End.Line, range.End.Character);
        Language.ShowList = (title, items) =>
        {
            QuickInput.OpenList(title, items, 0, null);
            PaletteRequested?.Invoke("");
        };
    }
}
