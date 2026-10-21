using Wicked.App.Shell;
using Wicked.Core.Debug;

namespace Wicked.App.ViewModels;

/// <summary>Debugging in the rest of the window: its commands and keys, what it asks of the palette, and what it keeps in the session.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>Run and Debug: breakpoints, sessions, Variables, Watch, Call Stack and the Debug Console.</summary>
    public Wicked.App.Debug.DebugController Debugger { get; }

    /// <summary>The title bar's Run / Debug / Stop / Build control; F5, Ctrl+F5 and Shift+F5 go through it.</summary>
    public Wicked.App.Debug.RunControl Run { get; }

    private IEnumerable<WorkbenchCommand> DebugCommands()
    {
        Task Run(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        var debug = Debugger;
        return
        [
            new("workbench.view.debug", "View", "Show Run and Debug", "Ctrl+Shift+D", () => Run(() => Navigate(ShellRoutes.Debug))),
            new("workbench.debug.action.toggleRepl", "View", "Debug Console", "Ctrl+Shift+Y", () => Run(() => BottomPanel.Toggle(PanelIds.DebugConsole))),
            new("workbench.action.debug.start", "Debug", "Start Debugging", "F5", this.Run.DebugAsync),
            new("workbench.action.debug.run", "Debug", "Start Without Debugging", "Ctrl+F5", this.Run.RunAsync),
            new("wicked.run.currentFile", "Run", "Run Current File", null, this.Run.RunCurrentFileAsync),
            new("wicked.debug.currentFile", "Debug", "Debug Current File", null, this.Run.DebugCurrentFileAsync),
            new("wicked.run.selectConfiguration", "Run", "Select Run Configuration…", null, this.Run.PickAsync),
            new("workbench.action.debug.selectandstart", "Debug", "Select and Start Debugging", null, debug.SelectAndStartAsync),
            new("workbench.action.debug.continue", "Debug", "Continue", null, () => debug.ContinueAsync()),
            // While debugging only (VS Code's "when"): otherwise F6 moves between parts and F11 is full screen.
            new("workbench.action.debug.pause", "Debug", "Pause", "F6", debug.PauseAsync) { When = () => debug.HasSession },
            new("workbench.action.debug.stepOver", "Debug", "Step Over", "F10", debug.StepOverAsync) { When = () => debug.HasSession },
            new("workbench.action.debug.stepInto", "Debug", "Step Into", "F11", debug.StepIntoAsync) { When = () => debug.HasSession },
            new("workbench.action.debug.stepOut", "Debug", "Step Out", "Shift+F11", debug.StepOutAsync) { When = () => debug.HasSession },
            new("workbench.action.debug.restart", "Debug", "Restart", "Ctrl+Shift+F5", debug.RestartAsync),
            new("workbench.action.debug.stop", "Debug", "Stop", "Shift+F5", this.Run.StopAsync),
            new("workbench.action.debug.attach", "Debug", "Attach to Process…", null, debug.AttachToProcessAsync),
            new("workbench.action.debug.createLaunchJson", "Debug", "Open launch.json", null, debug.OpenLaunchJsonAsync),
            new("editor.debug.action.toggleBreakpoint", "Debug", "Toggle Breakpoint", "F9", () => Run(debug.ToggleBreakpointAtCaret)),
            new("workbench.debug.viewlet.action.addFunctionBreakpointAction", "Debug", "New Function Breakpoint…", null, () => Run(debug.AddFunctionBreakpoint)),
            new("workbench.debug.viewlet.action.addWatch", "Debug", "Add Expression to Watch…", null, () => Run(() => debug.AddWatch())),
            new("workbench.debug.viewlet.action.removeAllBreakpoints", "Debug", "Remove All Breakpoints", null, () => Run(debug.Breakpoints.RemoveAll)),
            new("workbench.debug.viewlet.action.enableAllBreakpoints", "Debug", "Enable All Breakpoints", null, () => Run(() => debug.Breakpoints.SetAllEnabled(true))),
            new("workbench.debug.viewlet.action.disableAllBreakpoints", "Debug", "Disable All Breakpoints", null, () => Run(() => debug.Breakpoints.SetAllEnabled(false))),
            new("wicked.debug.installAdapter", "Debug", "Install a Debug Adapter…", null, InstallAdapterListAsync),
        ];
    }

    /// <summary>A list of the adapters the catalog knows, installed ones marked; choosing one offers to install it.</summary>
    private async Task InstallAdapterListAsync()
    {
        // Finding each adapter looks at the disk and the PATH: a worker does it.
        var found = await Task.Run(() => DebugAdapterCatalog.All.Select(spec => (Spec: spec, Command: Debugger.Locator.Find(spec))).ToList());
        var items = found.Select(f => new QuickItem(
            f.Spec.Name,
            f.Command is { } command ? $"installed: {command.Program}" : string.Join(", ", f.Spec.Languages),
            "",
            () => Debugger.OfferInstallAsync(f.Spec.Id))).ToList();
        ShowChoices("Debug adapters", items, null);
    }

    /// <summary>A line to type, in the palette's box: Enter gives the text, Escape gives null.</summary>
    public void ShowPrompt(string placeholder, string initial, Action<string?> done)
    {
        QuickInput.OpenPrompt(placeholder, initial, done);
        PaletteRequested?.Invoke("");
    }

    /// <summary>A list of choices in the palette, with what to do when it is left without one.</summary>
    public void ShowChoices(string placeholder, IReadOnlyList<QuickItem> items, Action? onCancel)
    {
        QuickInput.OpenList(placeholder, items, 0, onCancel);
        PaletteRequested?.Invoke("");
    }

    /// <summary>What the debugger kept for a folder (its breakpoints, watches, last configuration), from the session state.</summary>
    internal Wicked.Core.Debug.DebugWorkspaceState? DebugStateFor(string root) => _session.Debug.GetValueOrDefault(root);

    /// <summary>The debugger's state for a folder written into the session, on a worker.</summary>
    internal void RememberDebugState(string root, Wicked.Core.Debug.DebugWorkspaceState state)
    {
        _session = _session with { Debug = new Dictionary<string, Wicked.Core.Debug.DebugWorkspaceState>(_session.Debug) { [root] = state } };
        _ = SaveSessionAsync();
    }
}
