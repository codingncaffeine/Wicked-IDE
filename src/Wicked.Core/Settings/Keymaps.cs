namespace Wicked.Core.Settings;

/// <summary>
/// Keymap presets (workbench.keymap): VS Code's keys are the defaults; "jetbrains" lays the keys of JetBrains IDEs
/// (IntelliJ IDEA, Rider, PyCharm; their default Windows and Linux keymap) over them for the commands Wicked has, as VS
/// Code's IntelliJ keybindings extension does. A preset's keys win over the defaults; keybindings.json wins over both.
/// </summary>
public static class Keymaps
{
    public const string VsCode = "vscode";

    public const string JetBrains = "jetbrains";

    /// <summary>The preset's bindings, in Avalonia's key names; none for VS Code's.</summary>
    public static IReadOnlyList<Keybinding> For(string? keymap) => keymap == JetBrains ? JetBrainsBindings : [];

    private static Keybinding K(string keys, string command) => new(keys, command, false);

    private static readonly IReadOnlyList<Keybinding> JetBrainsBindings =
    [
        // Finding things
        K("Ctrl+Shift+A", "workbench.action.showCommands"),
        K("Ctrl+Shift+N", "workbench.action.quickOpen"),
        K("Ctrl+E", "workbench.action.quickOpen"),
        K("Ctrl+N", "workbench.action.showAllSymbols"),
        K("Ctrl+Alt+Shift+N", "workbench.action.showAllSymbols"),
        K("Ctrl+F12", "workbench.action.gotoSymbol"),
        K("Ctrl+G", "workbench.action.gotoLine"),
        K("Ctrl+Shift+F", "workbench.view.search"),
        K("Ctrl+Shift+R", "workbench.action.replaceInFiles"),
        K("Ctrl+F", "actions.find"),
        K("Ctrl+R", "editor.action.startFindReplaceAction"),

        // Navigating code
        K("Ctrl+B", "editor.action.revealDefinition"),
        K("Ctrl+Alt+B", "editor.action.goToImplementation"),
        K("Ctrl+Shift+B", "editor.action.goToTypeDefinition"),
        K("Alt+F7", "editor.action.goToReferences"),
        K("Ctrl+Alt+Left", "workbench.action.navigateBack"),
        K("Ctrl+Alt+Right", "workbench.action.navigateForward"),
        K("F2", "editor.action.marker.nextInFiles"),
        K("Shift+F2", "editor.action.marker.prevInFiles"),
        K("Ctrl+Q", "editor.action.showHover"),
        K("Ctrl+P", "editor.action.triggerParameterHints"),

        // Editing
        K("Shift+F6", "editor.action.rename"),
        K("Ctrl+Alt+L", "editor.action.formatDocument"),
        K("Alt+Enter", "editor.action.quickFix"),
        K("Ctrl+D", "editor.action.copyLinesDownAction"),
        K("Ctrl+Y", "editor.action.deleteLines"),
        K("Ctrl+Oem2", "editor.action.commentLine"),
        K("Ctrl+Shift+Oem2", "editor.action.blockComment"),
        K("Ctrl+W", "editor.action.smartSelect.expand"),
        K("Ctrl+Shift+W", "editor.action.smartSelect.shrink"),
        K("Alt+Shift+Up", "editor.action.moveLinesUpAction"),
        K("Alt+Shift+Down", "editor.action.moveLinesDownAction"),
        K("Ctrl+Shift+J", "editor.action.joinLines"),
        K("Ctrl+Shift+U", "editor.action.transformToUppercase"),
        K("Ctrl+F4", "workbench.action.closeActiveEditor"),

        // Running and debugging
        K("Ctrl+F9", "workbench.action.tasks.build"),
        K("Shift+F10", "workbench.action.debug.run"),
        K("Shift+F9", "workbench.action.debug.start"),
        K("Ctrl+F2", "workbench.action.debug.stop"),
        K("F8", "workbench.action.debug.stepOver"),
        K("F7", "workbench.action.debug.stepInto"),
        K("Shift+F8", "workbench.action.debug.stepOut"),
        K("F9", "workbench.action.debug.continue"),
        K("Ctrl+F8", "editor.debug.action.toggleBreakpoint"),

        // Tool windows and the rest
        K("Alt+D1", "workbench.view.explorer"),
        K("Alt+D9", "workbench.view.scm"),
        K("Alt+F12", "workbench.action.terminal.toggleTerminal"),
        K("Ctrl+Alt+S", "workbench.action.openSettings"),
        K("Ctrl+Shift+K", "git.push"),
        K("Ctrl+T", "git.pull"),
    ];
}
