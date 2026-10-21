using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wicked.App.ViewModels;

/// <summary>A button on a Get Started step: it runs a workbench command, the same one the palette and the keys run.</summary>
public sealed class GetStartedAction(string label, string keys, IAsyncRelayCommand run)
{
    public string Label { get; } = label;

    /// <summary>The keys that run it now ("" when none), shown beside the button.</summary>
    public string Keys { get; } = keys;

    public IAsyncRelayCommand Run { get; } = run;
}

/// <summary>One step of the walkthrough. A step whose state Wicked can see is ticked when it is done.</summary>
public sealed partial class GetStartedStep(string title, string text, IReadOnlyList<GetStartedAction> actions, Func<bool>? done) : ObservableObject
{
    public string Title { get; } = title;

    public string Text { get; } = text;

    public IReadOnlyList<GetStartedAction> Actions { get; } = actions;

    [ObservableProperty]
    private bool _isDone;

    internal void Refresh() => IsDone = done?.Invoke() ?? false;
}

/// <summary>
/// Help: Get Started, as VS Code's walkthrough: the first things to do in Wicked, each with the button that does it. The
/// model and folder steps tick themselves as the user does them.
/// </summary>
public sealed class GetStartedViewModel
{
    private readonly ShellViewModel _shell;

    public GetStartedViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Steps =
        [
            Step("Choose a model", "Wicked suggests two or three models that fit this PC. Install one and it runs here, on your machine.",
                () => _shell.Models.Installed.Count > 0, ("Choose a model", "wicked.showModels")),
            Step("Open a project", "Open a folder you already work in, or start a new project from a template.",
                () => _shell.Workbench.Root is not null, ("Open Folder…", "workbench.action.files.openFolder"), ("New Project…", "wicked.newProject")),
            Step("Ask the assistant", "Chat about your code, or press Ctrl+I in a file to have a change made and shown to you before it lands.",
                null, ("Open the assistant", "wicked.showAssistant")),
            Step("Run and debug", "Run the project or the file you are in; Wicked finds the build, run and test tasks of most languages.",
                null, ("Run and Debug", "workbench.action.debug.start"), ("Run Task…", "workbench.action.tasks.runTask")),
            Step("Make it yours", "Pick a theme, or change the font, the keys and every other setting.",
                null, ("Color Theme", "workbench.action.selectTheme"), ("Settings", "workbench.action.openSettings")),
            Step("Keys you know", "Keep Visual Studio Code's keys, or take a JetBrains IDE's (IntelliJ IDEA, Rider, PyCharm). Any key can be changed in Keyboard Shortcuts.",
                null, ("VS Code keys", "wicked.keymap.vscode"), ("JetBrains keys", "wicked.keymap.jetbrains"), ("Keyboard Shortcuts", "workbench.action.openGlobalKeybindings")),
            Step("Learn the keys", "Every command is in the palette; the reference lists the keys that run them.",
                null, ("Show All Commands", "workbench.action.showCommands"), ("Keyboard Shortcuts Reference", "workbench.action.keybindingsReference")),
        ];
        Refresh();
        _shell.Models.Installed.CollectionChanged += (_, _) => Refresh();
        _shell.Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(_shell.Workbench.Root)) Refresh();
        };
    }

    public ObservableCollection<GetStartedStep> Steps { get; }

    private GetStartedStep Step(string title, string text, Func<bool>? done, params (string Label, string Command)[] actions) =>
        new(title, text, [.. actions.Select(a => new GetStartedAction(a.Label, _shell.Keys.DisplayKeysFor(a.Command), new AsyncRelayCommand(() => _shell.RunCommandAsync(a.Command))))], done);

    private void Refresh()
    {
        foreach (var step in Steps) step.Refresh();
    }
}
