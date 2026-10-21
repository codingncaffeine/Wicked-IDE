using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.Debug;
using Wicked.App.Shell;
using Wicked.App.Terminal;
using Wicked.App.Views;
using Wicked.Core.Terminal;
using Wicked.Tests.Tasks;

namespace Wicked.Tests.Run;

/// <summary>The title bar's Run / Debug / Stop / Build control the way a user meets it: clicks, F-keys, the picker.</summary>
public sealed class RunControlWindowTests
{
    private static string ScreenOf(TerminalInstanceViewModel t)
    {
        var frame = t.Pane?.View.Frame;
        if (frame is null) return "";
        return string.Join('\n', frame.Lines.Select(row => string.Concat(row.Cells.Where(c => (c.Flags & CellFlags.WideTail) == 0).Select(c => frame.TextOf(c))).TrimEnd())).TrimEnd();
    }

    private static T Named<T>(MainWindow window, string name) where T : Control => window.FindControl<T>(name)!;

    /// <summary>A plain sh with an empty rc file in the terminal: nothing of this machine's shell setup runs in the test.</summary>
    private static void PlainShell(TempProfile profile, Sandbox box)
    {
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(profile.Paths.SettingsFile, """{ "terminal.integrated.defaultProfile": "sh" }""");
        Environment.SetEnvironmentVariable("ENV", box.Write("empty-rc", ""));
    }

    private static bool OnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, program)));

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task RunStartsTheCurrentFileInTheRunTerminalSavedFirstAndCtrlF5ReusesIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !OnPath("bash"), "a POSIX shell and bash");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.Write("hello.sh", "echo from-disk\n");
        PlainShell(profile, box);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "hello.sh");
            // No launch.json: the control starts the current file.
            Assert.Equal(ConfigurationChoice.CurrentFileName, Named<TextBlock>(window, "RunTargetText").Text);

            tab.Editor!.Focus();
            window.KeyTextInput("echo first-run-unsaved");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Ui.Pump(0.1);
            Assert.True(tab.IsDirty);

            Input.Click(window, Named<Button>(window, "RunButton"));
            Assert.True(Ui.PumpUntil(() => shell.Terminal.AllTerminals().Any(t => t.CustomName == RunControl.RunTerminalName), 10), "no Run terminal");
            var terminal = shell.Terminal.AllTerminals().Single(t => t.CustomName == RunControl.RunTerminalName);
            Assert.True(Ui.PumpUntil(() => ScreenOf(terminal).Contains("first-run-unsaved\nfrom-disk", StringComparison.Ordinal), 20), ScreenOf(terminal));
            Assert.False(tab.IsDirty);
            Assert.Equal(PanelIds.Terminal, shell.BottomPanel.Active?.Id);

            // Ctrl+F5 does what the button does, in the same terminal.
            tab.Editor!.Focus();
            window.KeyTextInput("echo second-run");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
            Input.Chord(window, Key.F5, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => ScreenOf(terminal).Contains("first-run-unsaved\nsecond-run\nfrom-disk", StringComparison.Ordinal), 20), ScreenOf(terminal));
            Assert.Single(shell.Terminal.AllTerminals(), t => t.CustomName == RunControl.RunTerminalName);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ThePickerOffersTheFoldersProgramsATaskRunsWithoutTheDebuggerAndStopEndsIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() || !OnPath("make"), "make and a POSIX shell");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.Write("Makefile", "run:\n\tsleep 60\n");
        PlainShell(profile, box);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            Assert.False(Named<Button>(window, "StopButton").IsEnabled);

            Input.Click(window, Named<Button>(window, "RunTarget"));
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Any(i => i.Label == "make: run"), 10), "the folder's program is not offered");
            Assert.Equal([ConfigurationChoice.CurrentFileName, "make: run", "Add Configuration…", "Run Task…"], shell.QuickInput.Items.Select(i => i.Label));
            var chosen = shell.QuickInput.Items.Single(i => i.Label == "make: run").Choose();
            Assert.True(Ui.PumpUntil(() => chosen.IsCompleted && Named<TextBlock>(window, "RunTargetText").Text == "make: run", 5), Named<TextBlock>(window, "RunTargetText").Text);
            // A task has no debugger; the Run and Debug panel shows the same choice.
            Assert.False(Named<Button>(window, "DebugButton").IsEnabled);
            Assert.Equal("make: run", shell.Debugger.SelectedConfiguration?.Name);

            var runAt = Named<Button>(window, "RunButton").TranslatePoint(default, window);
            Input.Click(window, Named<Button>(window, "RunButton"));
            Assert.True(Ui.PumpUntil(() => shell.Tasks.Service.IsRunning("make: run") && Named<Button>(window, "StopButton").IsEnabled, 10), "the program did not start, or Stop did not show");
            // Nothing moved under the pointer when the run started.
            Ui.Pump(0.2);
            Assert.Equal(runAt, Named<Button>(window, "RunButton").TranslatePoint(default, window));
            Input.Click(window, Named<Button>(window, "StopButton"));
            Assert.True(Ui.PumpUntil(() => !shell.Tasks.Service.IsRunning("make: run") && !Named<Button>(window, "StopButton").IsEnabled, 10), "Stop did not end it");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task BuildRunsTheBuildAndItsResultStaysInTheStatusBar()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the task is a /bin/sh command");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        const string Tasks = """{ "version": "2.0.0", "tasks": [ { "label": "build", "type": "shell", "command": "COMMAND", "group": { "kind": "build", "isDefault": true } } ] }""";
        box.WriteTasks(Tasks.Replace("COMMAND", "exit 3", StringComparison.Ordinal));
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            var result = Named<Button>(window, "TaskResultStatus");
            Assert.False(result.IsVisible);

            Input.Click(window, Named<Button>(window, "BuildButton"));
            Assert.True(Ui.PumpUntil(() => result.IsVisible && Named<TextBlock>(window, "TaskResultText").Text == "✗ build · exit code 3", 20), Named<TextBlock>(window, "TaskResultText").Text);
            Assert.Contains("failed", result.Classes);

            box.WriteTasks(Tasks.Replace("COMMAND", "true", StringComparison.Ordinal));
            Input.Click(window, Named<Button>(window, "BuildButton"));
            Assert.True(Ui.PumpUntil(() => Named<TextBlock>(window, "TaskResultText").Text?.StartsWith("✓ build · ", StringComparison.Ordinal) == true, 20), Named<TextBlock>(window, "TaskResultText").Text);
            Assert.DoesNotContain("failed", result.Classes);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheControlGivesWayWhenTheTitleBarIsNarrowAndNeverCoversTheCommandCentre()
    {
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.Write("a.txt", "a\n");
        await Ui.Run(() =>
        {
            var (window, _) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            var controls = Named<Grid>(window, "RunControls");
            var centre = Named<Button>(window, "CommandCentre");
            var buttons = Named<StackPanel>(window, "TitleButtons");
            Assert.True(controls.IsEffectivelyVisible && Named<Button>(window, "RunTarget").IsEffectivelyVisible, "hidden at the usual width");
            var left = controls.TranslatePoint(default, window)!.Value.X;
            var right = left + controls.Bounds.Width;
            Assert.True(left >= centre.TranslatePoint(default, window)!.Value.X + centre.Bounds.Width, "over the command centre");
            Assert.True(right <= buttons.TranslatePoint(default, window)!.Value.X + 0.5, "over the window buttons");

            window.Width = 1000;
            Assert.True(Ui.PumpUntil(() => !controls.IsVisible, 5), "still shown with no room");
            window.Width = 1440;
            Assert.True(Ui.PumpUntil(() => controls.IsVisible && Named<Button>(window, "RunTarget").IsVisible, 5), "did not come back");
            window.Close();
            return 0;
        });
    }
}
