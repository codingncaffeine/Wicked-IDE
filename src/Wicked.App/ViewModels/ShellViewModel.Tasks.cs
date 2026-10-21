using Wicked.App.Shell;
using Wicked.App.Tasks;
using Wicked.Core.Testing;

namespace Wicked.App.ViewModels;

/// <summary>Tasks and the test explorer in the rest of the window: their commands, their output, the Testing panel.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>tasks.json, detected tasks, Run Task and Ctrl+Shift+B.</summary>
    public TasksHost Tasks { get; private set; } = null!;

    /// <summary>The Testing panel.</summary>
    public TestingViewModel Testing { get; private set; } = null!;

    /// <summary>
    /// Runs the task called <paramref name="label"/> and returns when it is ready: a normal task ended with exit code 0, a
    /// background task printed its end line. This is what a launch configuration's preLaunchTask waits on.
    /// </summary>
    public Task<bool> RunTaskByLabelAsync(string label, CancellationToken ct = default) => Tasks.RunTaskByLabelAsync(label, ct);

    private void WireTasks()
    {
        Tasks = new TasksHost(this, _post);
        var testOutput = new OutputPanelTaskOutput(Output, name => Navigate($"output/{name}"), "", _post);
        Testing = new TestingViewModel(this, _post, new TestService(testOutput, workFolder: Path.Combine(Paths.State, "tests")));
        Testing.Service.WorkspaceFolder = Workbench.Root;
        Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workbench.Root)) _post(Testing.FolderChanged);
        };
    }

    private IEnumerable<WorkbenchCommand> TaskCommands() => [.. Tasks.Commands(), .. Testing.Commands()];

    /// <summary>The Testing panel looks for tests the first time it is shown; the extensions' panels follow the choice.</summary>
    partial void OnPanelChanged(SidePanel value)
    {
        if (value == SidePanel.Testing) Testing?.EnsureStarted();
        RaiseExtensionPanels();
    }

    /// <summary>A list of choices in the palette; <paramref name="onCancel"/> runs when Escape dismisses it.</summary>
    internal void ShowChoices(string placeholder, IReadOnlyList<QuickItem> items, int selected, Action? onCancel)
    {
        QuickInput.OpenList(placeholder, items, selected, onCancel);
        PaletteRequested?.Invoke("");
    }

    private void DisposeTasks()
    {
        Tasks.Dispose();
        Testing.Dispose();
    }
}
