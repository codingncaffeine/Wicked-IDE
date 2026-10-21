using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Language;
using Wicked.App.Tasks;
using Wicked.App.ViewModels;
using Wicked.App.Views;

namespace Wicked.Tests.Tasks;

/// <summary>Tasks and the Testing panel the way a user meets them: keys, the palette, the panels, clicks. Headless: no window opens on anyone's desktop.</summary>
public sealed class TasksWindowTests
{
    private const string BuildTasks = """
        {
          "version": "2.0.0",
          "tasks": [
            {
              "label": "build",
              "type": "shell",
              "command": "if [ -f ok ]; then echo 'build fine'; else echo \"main.zz:3:10: error: 'x' undeclared\"; exit 1; fi",
              "group": { "kind": "build", "isDefault": true },
              "problemMatcher": "$gcc"
            },
            { "label": "other", "type": "shell", "command": "echo other", "group": "build" }
          ]
        }
        """;

    private static string ChannelText(ShellViewModel shell, string name) =>
        shell.Output.Channels.FirstOrDefault(c => c.Name == name)?.Document.Text.ToString() ?? "";

    private static IEnumerable<ListBoxItem> Items(MainWindow window, string list) =>
        window.GetVisualDescendants().OfType<ListBox>().Where(l => l.Name == list).SelectMany(l => l.GetVisualDescendants().OfType<ListBoxItem>());

    [Fact]
    public async Task CtrlShiftBRunsTheDefaultBuildTaskItsErrorAppearsInProblemsAndAClickOpensTheFileAtTheLine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the task is a /bin/sh command");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.Write("main.zz", "first line\nsecond line\nthird line has the error\nfourth\n");
        box.WriteTasks(BuildTasks);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);

            Input.Chord(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => shell.Language.Problems.Errors == 1, 60), "the build's error never reached Problems");
            var problem = shell.Language.Problems.Files.Single().Items.Single();
            Assert.Equal((Path.Combine(box.Root, "main.zz"), 2, 9, "'x' undeclared", "gcc"), (problem.Path, problem.Line, problem.Column, problem.Message, problem.Source));

            // The output is in its own channel of the Output panel, without escape codes, and the footer says it failed.
            Assert.True(Ui.PumpUntil(() => ChannelText(shell, "Task - build").Contains("Task failed with exit code 1", StringComparison.Ordinal), 10));
            Assert.Contains("> Executing task: ", ChannelText(shell, "Task - build"), StringComparison.Ordinal);
            Assert.Contains("failed with exit code 1", shell.Notices.Top?.Text, StringComparison.Ordinal);

            // The status bar counts it; its click opens Problems, and a click on the problem opens the file at the line.
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ProblemsStatus"));
            Assert.True(Ui.PumpUntil(() => Items(window, "ProblemsList").Any(i => i.DataContext is ProblemItem && i.IsEffectivelyVisible), 10), "no problem row on screen");
            var row = Items(window, "ProblemsList").First(i => i.DataContext is ProblemItem);
            Input.Click(window, row);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Path == Path.Combine(box.Root, "main.zz"), 10), "the file did not open");
            var tab = shell.Workbench.ActiveTab!;
            Assert.True(Ui.PumpUntil(() => tab.Document.Text.LineOf(tab.Document.Primary.Active) == 2, 5), "the caret is not on the error's line");

            // Fixed: the next run starts the task's problems afresh, and finds none.
            File.WriteAllText(box.Path_("ok"), "");
            Input.Chord(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => shell.Language.Problems.Errors == 0 && ChannelText(shell, "Task - build").Contains("build fine", StringComparison.Ordinal), 60), "the old problem stayed");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task RunTaskListsTheConfiguredAndTheDetectedTasksAndRunsTheChosenOne()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the task is a /bin/sh command");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.Write("package.json", """{ "scripts": { "lint": "eslint ." } }""");
        box.WriteTasks(BuildTasks);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            var command = shell.Commands.Single(c => c.Id == "workbench.action.tasks.runTask");
            _ = command.Run();
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Count == 3, 10), "the task list did not open");
            Assert.Equal(["build", "other", "npm: lint"], shell.QuickInput.Items.Select(i => i.Label));
            Assert.Equal("eslint .", shell.QuickInput.Items[2].Detail.Replace("npm: ", "", StringComparison.Ordinal));

            // Typing narrows it; Enter runs the one left.
            shell.QuickInput.Query = "oth";
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.Count == 1, 10));
            _ = shell.QuickInput.ChooseAsync();
            Assert.True(Ui.PumpUntil(() => ChannelText(shell, "Task - other").Contains("Task finished", StringComparison.Ordinal), 60), "the task did not run");
            Assert.Contains("other", ChannelText(shell, "Task - other"), StringComparison.Ordinal);
            Assert.True(Ui.PumpUntil(() => shell.Output.Shown?.Name == "Task - other" && shell.BottomPanel.IsVisible, 5), "its output was not brought forward");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ATaskInputIsAskedInThePaletteAndItsAnswerIsUsed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the task is a /bin/sh command");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.WriteTasks("""
            { "version": "2.0.0",
              "inputs": [ { "id": "who", "type": "promptString", "description": "Who to greet?" },
                          { "id": "how", "type": "pickString", "description": "How loud?", "options": ["quietly", "loudly"] } ],
              "tasks": [ { "label": "greet", "type": "shell", "command": "echo hello ${input:who} ${input:how}", "group": { "kind": "build", "isDefault": true } } ] }
            """);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            Input.Chord(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Placeholder == "Who to greet?", 30), "the first question was not asked");
            shell.QuickInput.Query = "Ada";
            _ = shell.QuickInput.ChooseAsync();
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Placeholder == "How loud?", 30), "the second question was not asked");
            Assert.Equal(["quietly", "loudly"], shell.QuickInput.Items.Select(i => i.Label));
            shell.QuickInput.Selected = 1;
            _ = shell.QuickInput.ChooseAsync();
            Assert.True(Ui.PumpUntil(() => ChannelText(shell, "Task - greet").Contains("hello Ada loudly", StringComparison.Ordinal), 60), "the answers were not used");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task EscapingAQuestionCancelsTheTaskInsteadOfLeavingItWaiting()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the task is a /bin/sh command");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.WriteTasks("""
            { "version": "2.0.0",
              "inputs": [ { "id": "how", "type": "pickString", "description": "How loud?", "options": ["quietly", "loudly"] } ],
              "tasks": [ { "label": "greet", "type": "shell", "command": "echo hello ${input:how}", "group": { "kind": "build", "isDefault": true } } ] }
            """);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            Input.Chord(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Placeholder == "How loud?", 30));
            Assert.True(shell.Tasks.Service.IsRunning("greet"));
            shell.QuickInput.Close();
            Assert.True(Ui.PumpUntil(() => !shell.Tasks.Service.IsRunning("greet"), 10), "the task kept waiting for an answer");
            Assert.DoesNotContain("hello", ChannelText(shell, "Task - greet"), StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TerminateTaskStopsARunningTaskAndTheFooterDoesNotCallItAFailure()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the task is a /bin/sh command");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.WriteTasks("""{ "version": "2.0.0", "tasks": [ { "label": "serve", "type": "shell", "isBackground": true, "command": "echo up; sleep 300", "group": { "kind": "build", "isDefault": true } } ] }""");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            Input.Chord(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => ChannelText(shell, "Task - serve").Contains("up", StringComparison.Ordinal) && shell.Tasks.Service.IsRunning("serve"), 30));

            // Running it again offers to stop or restart it rather than starting a second.
            Input.Chord(window, Key.B, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Select(i => i.Label).SequenceEqual(["Terminate Task", "Restart Task", "Show Output"]), 10));
            shell.QuickInput.Close();

            _ = shell.Commands.Single(c => c.Id == "workbench.action.tasks.terminate").Run();
            Assert.True(Ui.PumpUntil(() => !shell.Tasks.Service.IsRunning("serve"), 15), "the task is still running");
            Assert.True(Ui.PumpUntil(() => ChannelText(shell, "Task - serve").Contains("Task terminated", StringComparison.Ordinal), 10));
            Assert.DoesNotContain("failed", shell.Notices.Top?.Text ?? "", StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ConfigureTaskWritesADetectedTaskIntoTasksJsonAndOpensIt()
    {
        using var profile = new TempProfile();
        using var box = new Sandbox();
        box.Write("Makefile", "all:\n\t@echo built\n\ntest:\n\t@echo tested\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            _ = shell.Commands.Single(c => c.Id == "workbench.action.tasks.configureTaskRunner").Run();
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Count == 2, 10));
            Assert.Equal(["make: all", "make: test"], shell.QuickInput.Items.Select(i => i.Label));
            shell.QuickInput.Selected = 1;
            _ = shell.QuickInput.ChooseAsync();
            var path = Path.Combine(box.Root, ".vscode", "tasks.json");
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Path == path, 15), "tasks.json did not open");
            var text = shell.Workbench.ActiveTab!.Document.Text.ToString();
            Assert.Contains("\"label\": \"make: test\"", text, StringComparison.Ordinal);
            Assert.Contains("\"command\": \"make test\"", text, StringComparison.Ordinal);
            Assert.Equal(["make: test"], Wicked.Core.Tasks.TasksFile.Parse(text).Tasks.Select(t => t.Label));

            // A second one is added to the file already there.
            _ = shell.Commands.Single(c => c.Id == "workbench.action.tasks.configureTaskRunner").Run();
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Count >= 1, 10));
            var all = shell.QuickInput.Items.ToList().FindIndex(i => i.Label == "make: all");
            Assert.True(all >= 0);
            shell.QuickInput.Selected = all;
            _ = shell.QuickInput.ChooseAsync();
            Assert.True(Ui.PumpUntil(() => Wicked.Core.Tasks.TasksFile.Parse(File.ReadAllText(path)).Tasks.Length == 2, 15), "the second task was not added");
            window.Close();
            return 0;
        });
    }

    // ── The Testing panel ────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheTestingPanelListsRunsAndShowsAFailureWhoseStackLineOpensTheFile()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the scratch project is built on Linux");
        using var profile = new TempProfile();
        using var box = new Sandbox();
        TestExplorerTests.PlantTinyProject(box);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, box.Root);
            Input.Click(window, window.FindControl<Button>("RailTesting")!);
            Assert.Equal("TESTING", window.FindControl<Border>("SidePanel")!.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("heading")).Text);

            // The tests are looked for when the panel first shows: a tree of project, namespace, class, tests.
            Assert.True(Ui.PumpUntil(() => shell.Testing.Rows.Count >= 11, 180), "the tests were not listed");
            Assert.True(Ui.PumpUntil(() => Items(window, "TestTree").Count() >= 11, 10));
            Assert.Contains(shell.Testing.Rows, r => r.Name == "Positive(x: 2)");

            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "TestRunAll"));
            Assert.True(Ui.PumpUntil(() => shell.Testing.Summary.Contains("1 failed", StringComparison.Ordinal), 180), $"summary was '{shell.Testing.Summary}'");
            Assert.Equal("4 passed, 1 failed, 1 skipped of 6", shell.Testing.Summary);

            // The failed test: a click shows its message and its stack, and the stack line opens the file at the line.
            var fails = Items(window, "TestTree").First(i => i.DataContext is TestRowItem { Name: "Fails" });
            Input.Click(window, fails);
            Assert.True(Ui.PumpUntil(() => shell.Testing.Details.Any(d => d.Text.Contains("Expected: 5", StringComparison.Ordinal)), 10), "the failure message is not shown");
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "TestDetails").IsEffectivelyVisible, 5));
            var link = window.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("testlink") && b.IsEffectivelyVisible);
            // The line is longer than the panel is wide: the click goes on its start, where a user would put it.
            var at = link.TranslatePoint(new Point(12, link.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left);
            window.MouseUp(at, MouseButton.Left);
            Ui.Pump(0.1);
            var file = Path.Combine(box.Root, "Tiny", "MathTests.cs");
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Path == file, 10), "the stack line did not open the file");
            var tab = shell.Workbench.ActiveTab!;
            Assert.True(Ui.PumpUntil(() => tab.Document.Text.LineOf(tab.Document.Primary.Active) == 8, 5), "not at the failing line");
            window.Close();
            return 0;
        });
    }
}
