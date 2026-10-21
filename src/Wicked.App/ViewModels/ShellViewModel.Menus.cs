using Wicked.App.Shell;

namespace Wicked.App.ViewModels;

/// <summary>
/// The menus, with VS Code's items in VS Code's order: the menu bar (File, Edit, Selection, View, Go, Run, Terminal,
/// Help), the code editor's right-click menu and the tabs'. Every item is a registered command, so it shows the keys in
/// force and runs exactly as they do.
/// </summary>
public sealed partial class ShellViewModel
{
    private static MenuSeparator Line => MenuSeparator.Line;

    private static MenuCommand C(string id, string? label = null, Func<bool>? isChecked = null) => new(id, label, isChecked);

    public IReadOnlyList<TopMenu> MenuBar() =>
    [
        new("_File", () =>
        [
            C("workbench.action.files.newUntitledFile"), C("wicked.newProject"), C("workbench.action.newWindow"), Line,
            C("workbench.action.files.openFile"), C("workbench.action.files.openFolder"), new SubMenu("Open Recent", RecentEntries), Line,
            C("workbench.action.files.save"), C("workbench.action.files.saveAs"), C("workbench.action.files.saveAll"), Line,
            C("workbench.action.toggleAutoSave", "Auto Save", () => Settings.AutoSave != "off"),
            new SubMenu("Preferences", () =>
            [
                C("workbench.action.openSettings", "Settings"), C("workbench.action.openGlobalKeybindings", "Keyboard Shortcuts"),
                C("workbench.action.openSnippets", "Configure Snippets"), Line,
                C("workbench.action.selectTheme", "Color Theme"), C("wicked.selectCodeColours", "Code Colours"), C("workbench.action.importTheme", "Import a VS Code Color Theme..."), Line,
                C("workbench.action.openSettingsJson", "Settings (JSON)"), C("wicked.initScript.open", "init.csx"),
            ]),
            Line,
            C("workbench.action.files.revert"), C("workbench.action.closeActiveEditor"), C("workbench.action.closeFolder"), C("workbench.action.closeWindow"), Line,
            C("workbench.action.quit", "Exit"),
        ]),
        new("_Edit", () =>
        [
            C("undo"), C("redo"), Line,
            C("editor.action.clipboardCutAction"), C("editor.action.clipboardCopyAction"), C("editor.action.clipboardPasteAction"), Line,
            C("actions.find"), C("editor.action.startFindReplaceAction"), Line,
            C("workbench.view.search", "Find in Files"), C("workbench.action.replaceInFiles"), Line,
            C("editor.action.commentLine"), C("editor.action.blockComment"), Line,
            C("editor.action.formatDocument"), C("editor.action.rename"),
        ]),
        new("_Selection", () =>
        [
            C("editor.action.selectAll"), C("editor.action.smartSelect.expand"), C("editor.action.smartSelect.shrink"), Line,
            C("editor.action.copyLinesUpAction"), C("editor.action.copyLinesDownAction"), C("editor.action.moveLinesUpAction"), C("editor.action.moveLinesDownAction"),
            C("editor.action.duplicateSelection"), Line,
            C("editor.action.insertCursorAbove"), C("editor.action.insertCursorBelow"), C("editor.action.insertCursorAtEndOfEachLineSelected"),
            C("editor.action.addSelectionToNextFindMatch"), C("editor.action.selectHighlights"),
        ]),
        new("_View", () =>
        [
            C("workbench.action.showCommands", "Command Palette..."), Line,
            new SubMenu("Appearance", () =>
            [
                C("workbench.action.toggleFullScreen", "Full Screen", () => FullScreen), C("workbench.action.toggleZenMode", "Zen Mode", () => ZenMode), Line,
                C("workbench.action.toggleMenuBar", "Menu Bar", () => Settings.MenuBarVisibility is "classic" or "compact"),
                C("workbench.action.toggleSidebarVisibility", "Primary Side Bar", () => SidebarVisible),
                C("wicked.toggleAssistant", "Assistant", () => AssistantVisible),
                C("workbench.action.toggleStatusbarVisibility", "Status Bar", () => ShowStatusBar),
                C("workbench.action.togglePanel", "Panel", () => BottomPanel.IsVisible), Line,
                C("workbench.action.positionPanelBottom", "Panel Position: Bottom", () => PanelLocation == "bottom"),
                C("workbench.action.positionPanelLeft", "Panel Position: Left", () => PanelLocation == "left"),
                C("workbench.action.positionPanelRight", "Panel Position: Right", () => PanelLocation == "right"), Line,
                C("editor.action.toggleMinimap", "Minimap", () => Settings.Minimap), C("breadcrumbs.toggle", "Breadcrumbs", () => Settings.Breadcrumbs),
                C("wicked.toggleLineNumbers", "Line Numbers", () => Settings.LineNumbers != "off"),
                C("editor.action.toggleRenderWhitespace", "Render Whitespace", () => Settings.RenderWhitespace != "none"), Line,
                C("workbench.action.zoomIn"), C("workbench.action.zoomOut"), C("workbench.action.zoomReset"),
            ]),
            new SubMenu("Editor Layout", () =>
            [
                C("workbench.action.splitEditorUp", "Split Up"), C("workbench.action.splitEditorDown", "Split Down"),
                C("workbench.action.splitEditorLeft", "Split Left"), C("workbench.action.splitEditor", "Split Right"), Line,
                C("workbench.action.toggleMaximizeEditorGroup", "Maximize Editor Group"), C("workbench.action.evenEditorWidths", "Reset Editor Group Sizes"),
                C("workbench.action.joinTwoGroups", "Join with Next Group"), C("workbench.action.joinAllGroups", "Join All Editor Groups"),
            ]),
            Line,
            C("workbench.view.explorer", "Explorer"), C("workbench.view.search", "Search"), C("workbench.view.scm", "Source Control"),
            C("workbench.view.debug", "Run and Debug"), C("workbench.view.extensions", "Extensions"), Line,
            C("workbench.actions.view.problems", "Problems"), C("workbench.action.output.toggleOutput", "Output"),
            C("workbench.debug.action.toggleRepl", "Debug Console"), C("workbench.action.terminal.toggleTerminal", "Terminal"), Line,
            C("editor.action.toggleWordWrap", "Word Wrap"),
        ]),
        new("_Go", () =>
        [
            C("workbench.action.navigateBack", "Back"), C("workbench.action.navigateForward", "Forward"),
            C("workbench.action.navigateToLastEditLocation", "Last Edit Location"), Line,
            new SubMenu("Switch Editor", () =>
            [
                C("workbench.action.nextEditor", "Next Editor"), C("workbench.action.previousEditor", "Previous Editor"), Line,
                C("workbench.action.quickOpenNavigateNextInEditorPicker", "Next Used Editor"),
                C("workbench.action.quickOpenNavigatePreviousInEditorPicker", "Previous Used Editor"),
            ]),
            new SubMenu("Switch Group", () =>
            [
                C("workbench.action.focusFirstEditorGroup", "Group 1"), C("workbench.action.focusSecondEditorGroup", "Group 2"),
                C("workbench.action.focusThirdEditorGroup", "Group 3"), Line,
                C("workbench.action.focusNextGroup", "Next Group"), C("workbench.action.focusPreviousGroup", "Previous Group"), Line,
                C("workbench.action.focusLeftGroup", "Group Left"), C("workbench.action.focusRightGroup", "Group Right"),
                C("workbench.action.focusAboveGroup", "Group Above"), C("workbench.action.focusBelowGroup", "Group Below"),
            ]),
            Line,
            C("workbench.action.quickOpen", "Go to File..."), C("workbench.action.showAllSymbols", "Go to Symbol in Workspace..."), Line,
            C("workbench.action.gotoSymbol", "Go to Symbol in Editor..."), C("editor.action.revealDefinition", "Go to Definition"),
            C("editor.action.revealDeclaration", "Go to Declaration"), C("editor.action.goToTypeDefinition", "Go to Type Definition"),
            C("editor.action.goToImplementation", "Go to Implementations"), C("editor.action.goToReferences", "Go to References"), Line,
            C("workbench.action.gotoLine", "Go to Line/Column..."), C("editor.action.jumpToBracket", "Go to Bracket"), Line,
            C("editor.action.marker.nextInFiles", "Next Problem"), C("editor.action.marker.prevInFiles", "Previous Problem"),
            C("workbench.action.editor.nextChange", "Next Change"), C("workbench.action.editor.previousChange", "Previous Change"),
        ]),
        new("_Run", () =>
        [
            C("workbench.action.debug.start", "Start Debugging"), C("workbench.action.debug.run", "Run Without Debugging"),
            C("workbench.action.debug.stop", "Stop"), C("workbench.action.debug.restart", "Restart Debugging"), Line,
            C("wicked.run.currentFile", "Run Current File"), C("wicked.debug.currentFile", "Debug Current File"),
            C("wicked.run.selectConfiguration", "Select Run Configuration..."), Line,
            C("workbench.action.debug.createLaunchJson", "Open Configurations"), C("workbench.action.debug.selectandstart", "Select and Start Debugging..."),
            C("workbench.action.debug.attach", "Attach to Process..."), Line,
            C("workbench.action.debug.stepOver", "Step Over"), C("workbench.action.debug.stepInto", "Step Into"),
            C("workbench.action.debug.stepOut", "Step Out"), C("workbench.action.debug.continue", "Continue"), C("workbench.action.debug.pause", "Pause"), Line,
            C("editor.debug.action.toggleBreakpoint", "Toggle Breakpoint"),
            C("workbench.debug.viewlet.action.addFunctionBreakpointAction", "New Function Breakpoint..."), Line,
            C("workbench.debug.viewlet.action.enableAllBreakpoints", "Enable All Breakpoints"),
            C("workbench.debug.viewlet.action.disableAllBreakpoints", "Disable All Breakpoints"),
            C("workbench.debug.viewlet.action.removeAllBreakpoints", "Remove All Breakpoints"), Line,
            C("wicked.debug.installAdapter", "Install a Debug Adapter..."),
        ]),
        new("_Terminal", () =>
        [
            C("workbench.action.terminal.new", "New Terminal"), C("workbench.action.terminal.split", "Split Terminal"), Line,
            C("workbench.action.tasks.runTask", "Run Task..."), C("workbench.action.tasks.build", "Run Build Task..."),
            C("workbench.action.tasks.test", "Run Test Task..."), C("workbench.action.tasks.rebuild", "Rebuild"), C("wicked.tasks.clean", "Clean"),
            C("workbench.action.terminal.runSelectedText", "Run Selected Text"), Line,
            C("workbench.action.tasks.showTasks", "Show Running Tasks..."), C("workbench.action.tasks.restartTask", "Restart Running Task..."),
            C("workbench.action.tasks.terminate", "Terminate Task..."), Line,
            C("workbench.action.tasks.configureTaskRunner", "Configure Tasks..."),
        ]),
        new("_Help", () =>
        [
            C("workbench.action.showCommands", "Show All Commands"), C("workbench.action.openWalkthrough", "Get Started"),
            C("workbench.action.openDocumentationUrl", "Documentation"), C("update.showCurrentReleaseNotes", "Release Notes"), Line,
            C("workbench.action.keybindingsReference", "Keyboard Shortcuts Reference"), Line,
            C("workbench.action.openIssueReporter", "Report Issue"), Line,
            C("workbench.action.openLicenseUrl", "View Licence"), Line,
            C("update.checkForUpdates", "Check for Updates..."), Line,
            C("workbench.action.showAboutDialog", "About"),
        ]),
    ];

    /// <summary>File > Open Recent: the folders opened before (this one left out), then the picker and the clearing.</summary>
    private IReadOnlyList<MenuEntry> RecentEntries()
    {
        var folders = Workbench.RecentFolders.Where(f => f != Workbench.Root).Take(10).ToList();
        return
        [
            .. folders.Select(f => (MenuEntry)new MenuAction(f, () => OpenFolderAndRememberAsync(f))),
            Line,
            C("workbench.action.openRecent", "More..."),
            Line,
            C("workbench.action.clearRecentFiles", "Clear Recently Opened"),
        ];
    }

    /// <summary>A tab's right-click menu, as VS Code's: closing, its paths, revealing it, pinning, splitting. It acts on that tab.</summary>
    public IReadOnlyList<MenuEntry> TabContextMenu(Wicked.App.Workbench.EditorTab tab, Wicked.App.Workbench.EditorGroup group)
    {
        void Pick() => group.Activate(tab);
        MenuCommand On(string id, string label) => new(id, label, Before: Pick);
        var file = tab.Path is not null;
        return
        [
            On("workbench.action.closeActiveEditor", "Close"), On("workbench.action.closeOtherEditors", "Close Others"),
            On("workbench.action.closeEditorsToTheRight", "Close to the Right"), On("workbench.action.closeUnmodifiedEditors", "Close Saved"),
            On("workbench.action.closeEditorsInGroup", "Close All"), Line,
            .. file
                ? new MenuEntry[]
                {
                    On("workbench.action.files.copyPathOfActiveFile", "Copy Path"), On("workbench.action.files.copyRelativePathOfActiveFile", "Copy Relative Path"), Line,
                    On("workbench.files.action.showActiveFileInExplorer", "Reveal in Explorer View"),
                    On("revealFileInOS", OperatingSystem.IsWindows() ? "Reveal in File Explorer" : "Open Containing Folder"), Line,
                }
                : [],
            On("workbench.action.pinEditor", tab.IsPinned ? "Unpin" : "Pin"), Line,
            On("workbench.action.splitEditorUp", "Split Up"), On("workbench.action.splitEditorDown", "Split Down"),
            On("workbench.action.splitEditorLeft", "Split Left"), On("workbench.action.splitEditor", "Split Right"),
        ];
    }

    /// <summary>The code editor's right-click menu, as VS Code's: the language's navigation and edits, the clipboard, the assistant.</summary>
    public IReadOnlyList<MenuEntry> EditorContextMenu() =>
    [
        C("editor.action.revealDefinition", "Go to Definition"), C("editor.action.goToReferences", "Go to References"),
        C("editor.action.peekDefinition", "Peek Definition"), Line,
        C("editor.action.rename", "Rename Symbol"), C("editor.action.changeAll", "Change All Occurrences"),
        C("editor.action.formatDocument", "Format Document"), C("editor.action.formatSelection", "Format Selection"),
        C("editor.action.refactor", "Refactor..."), C("editor.action.sourceAction", "Source Action..."), Line,
        C("inlineChat.start", "Edit with the Assistant"), C("wicked.assistant.explainSelection", "Explain with the Assistant"),
        C("wicked.assistant.addSelectionToChat", "Add to Chat"), Line,
        C("editor.action.clipboardCutAction", "Cut"), C("editor.action.clipboardCopyAction", "Copy"), C("editor.action.clipboardPasteAction", "Paste"), Line,
        C("workbench.action.showCommands", "Command Palette..."),
    ];
}
