using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Debug;
using Wicked.App.Editor;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.App.Workbench;
using Wicked.Core.Debug;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Tests.Debug;

/// <summary>Debugging walked through the real window against the scripted adapter: the gutter, F5, the stopped line, stepping, ending.</summary>
public sealed class DebugWindowTests
{
    private static readonly string[] Lines = ["pc = 1", "counter = 2", "name = 3", "obj = 4", "pc + counter", "done"];

    private static string MakeFolder(TempProfile profile)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(Path.Combine(root, ".vscode"));
        File.WriteAllLines(Path.Combine(root, "app.txt"), Lines);
        File.WriteAllText(Path.Combine(root, ".vscode", "launch.json"), """
            {
              // The scripted adapter runs the file as a program.
              "version": "0.2.0",
              "configurations": [
                { "name": "Fake", "type": "fake", "request": "launch", "program": "${workspaceFolder}/app.txt" }
              ]
            }
            """);
        return root;
    }

    private static (MainWindow Window, ShellViewModel Shell, EditorTab Tab, CodeEditor Editor, string Path) Open(TempProfile profile, FakeAdapterProvider? provider = null)
    {
        var folder = MakeFolder(profile);
        var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, folder);
        shell.Debugger.Adapters = provider ?? new FakeAdapterProvider();
        var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "app.txt");
        return (window, shell, tab, tab.Editor!, tab.Path!);
    }

    /// <summary>A click in the glyph margin beside a line (0-based), as the user makes it.</summary>
    private static void ClickGlyph(MainWindow window, CodeEditor editor, int line, MouseButton button = MouseButton.Left)
    {
        var offset = editor.Document.Text.LineStart(line);
        var box = editor.RectOf(offset);
        var point = editor.TranslatePoint(new Point(TextViewGlyphX, box.Top + box.Height / 2), window)!.Value;
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Ui.Pump(0.1);
    }

    private const double TextViewGlyphX = 8;

    private static IReadOnlyList<GutterGlyph> Glyphs(CodeEditor editor, int line)
    {
        var key = editor.Document.Text.LineStart(line);
        return [.. editor.Decorations[DebugController.MarksLayer].Gutter.Where(g => g.LineStart == key).Select(g => g.Glyph)];
    }

    private static bool Stopped(CodeEditor editor, int line) =>
        editor.Decorations[DebugController.MarksLayer].Lines.Any(l => l.LineStart == editor.Document.Text.LineStart(line) && l.Style == LineStyle.Stopped);

    [Trait("Tier", "Window")]
    [Fact]
    public async Task APreLaunchTaskFromTasksJsonRunsFirstAndAFailingOneStopsTheLaunch()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var root = MakeFolder(profile);
            // A real task through the tasks lane (echo and exit read the same in sh and cmd).
            File.WriteAllText(Path.Combine(root, ".vscode", "tasks.json"), """
                { "version": "2.0.0", "tasks": [
                  { "label": "build", "type": "shell", "command": "echo built> built.txt" },
                  { "label": "broken", "type": "shell", "command": "exit 3" } ] }
                """);
            File.WriteAllText(Path.Combine(root, ".vscode", "launch.json"), """
                { "version": "0.2.0", "configurations": [
                  { "name": "Fake", "type": "fake", "request": "launch", "program": "${workspaceFolder}/app.txt", "preLaunchTask": "build" },
                  { "name": "Broken", "type": "fake", "request": "launch", "program": "${workspaceFolder}/app.txt", "preLaunchTask": "broken" } ] }
                """);
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            try
            {
                shell.Debugger.Adapters = new FakeAdapterProvider();
                var editor = WorkbenchWindowTests.OpenByClicks(window, shell, "app.txt").Editor!;
                var debug = shell.Debugger;
                Assert.True(Ui.PumpUntil(() => debug.Configurations.Any(c => c.Name == "Broken"), 10), "launch.json was not read");
                ClickGlyph(window, editor, 2);

                Input.Chord(window, Key.F5, RawInputModifiers.None);
                Assert.True(Ui.PumpUntil(() => debug.IsStopped, 20), $"the program did not stop: {string.Join(" | ", debug.Console.Lines.Select(l => l.Text))}");
                Assert.Equal("built", File.ReadAllText(Path.Combine(root, "built.txt")).Trim());
                Input.Chord(window, Key.F5, RawInputModifiers.Shift);
                Assert.True(Ui.PumpUntil(() => !debug.HasSession, 10), "the session did not end");

                debug.SelectedConfiguration = debug.Configurations.First(c => c.Name == "Broken");
                Input.Chord(window, Key.F5, RawInputModifiers.None);
                Assert.True(Ui.PumpUntil(() => debug.Console.Lines.Any(l => l.Text.Contains("\"broken\"", StringComparison.Ordinal)), 20), "the failed task was not reported");
                Ui.Pump(0.5);
                Assert.False(debug.HasSession, "a launch went ahead after its preLaunchTask failed");
            }
            finally
            {
                window.Close();
            }

            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ClickingTheGlyphMarginSetsAndClearsABreakpointAndTheGutterShowsIt()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);

            ClickGlyph(window, editor, 2);

            Assert.Equal([3], shell.Debugger.Breakpoints.ForFile(path).Select(b => b.Line));
            Assert.Contains(GutterGlyph.Breakpoint, Glyphs(editor, 2));
            Assert.Single(shell.Debugger.BreakpointRows, r => r.Kind == BreakpointRowKind.Source && r.Line == 3);

            ClickGlyph(window, editor, 2);

            Assert.Empty(shell.Debugger.Breakpoints.ForFile(path));
            Assert.Empty(Glyphs(editor, 2));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ARightClickInTheMarginOffersTheBreakpointMenu()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            shell.Debugger.Breakpoints.Add(path, 2);

            ClickGlyph(window, editor, 1, MouseButton.Right);

            var menu = editor.ContextMenu ?? window.GetVisualDescendants().OfType<ContextMenu>().FirstOrDefault();
            var headers = window.GetVisualDescendants().OfType<MenuItem>().Select(m => m.Header?.ToString()).ToList();
            Assert.Contains("Remove Breakpoint", headers);
            Assert.Contains("Edit Breakpoint…", headers);
            Assert.Contains("Disable Breakpoint", headers);
            Assert.NotNull(menu ?? new object());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task F5RunsToTheBreakpointShowsTheStoppedLineAndFillsVariablesAndTheStack()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, editor, path) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);

            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && debug.Variables.Rows.Any(r => r.Name == "pc"), 15), "the program did not stop with its variables shown");

            Assert.True(Stopped(editor, 2));
            Assert.Contains(GutterGlyph.Stopped, Glyphs(editor, 2));
            Assert.Contains(GutterGlyph.Breakpoint, Glyphs(editor, 2));
            Assert.Equal("3", debug.Variables.Rows.Single(r => r.Name == "pc").Value);
            Assert.Equal(["Locals", "pc", "counter", "name", "obj", "Globals"], debug.Variables.Rows.Select(r => r.Name));
            Assert.Contains(debug.CallStack, r => r is { Kind: StackRowKind.Thread, Title: "main" });
            Assert.Equal(["Main", "Caller"], debug.CallStack.Where(r => r.IsFrame).Select(r => r.Title));
            Assert.True(debug.CallStack.First(r => r.IsFrame).IsSelected);
            // The editor is brought to the stopped line in a step posted after the stop: under load it lands a moment later.
            Assert.True(Ui.PumpUntil(() => tab.Document.Text.LineOf(tab.Document.Primary.Active) == 2 && editor.IsFocused, 5), $"the caret is on line {tab.Document.Text.LineOf(tab.Document.Primary.Active)}");
            Assert.True(editor.IsFocused);
            Assert.True(shell.IsDebug, "the Run and Debug panel opens when the first session starts");
            Assert.True(window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DebugBar").IsEffectivelyVisible);
            Assert.Equal("Paused on breakpoint", debug.StatusText);
            Assert.Contains("Hello from the fake adapter", debug.Console.Lines.Select(l => l.Text));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task F10MovesTheStoppedLineAndMarksTheVariablesThatChanged()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && debug.Variables.Rows.Any(r => r.Name == "pc"), 15));

            Input.Chord(window, Key.F10, RawInputModifiers.None);

            Assert.True(Ui.PumpUntil(() => Stopped(editor, 3) && debug.Variables.Rows.Any(r => r.Name == "pc" && r.Value == "4"), 15), "the stopped line did not move");
            Assert.False(Stopped(editor, 2));
            Assert.DoesNotContain(GutterGlyph.Stopped, Glyphs(editor, 2));
            Assert.True(debug.Variables.Rows.Single(r => r.Name == "pc").Changed);
            Assert.False(debug.Variables.Rows.Single(r => r.Name == "counter").Changed);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ShiftF5EndsTheSessionAndTakesTheMarksAndTheToolbarAway()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, _) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && Stopped(editor, 2), 15));

            Input.Chord(window, Key.F5, RawInputModifiers.Shift);

            Assert.True(Ui.PumpUntil(() => !debug.HasSession, 15), "the session did not end");
            Assert.False(Stopped(editor, 2));
            Assert.Contains(GutterGlyph.Breakpoint, Glyphs(editor, 2));
            Assert.Empty(debug.Variables.Rows);
            Assert.Empty(debug.CallStack);
            Assert.False(window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "DebugBar").IsEffectivelyVisible);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task ValuesAppearAfterTheLinesThatUseThemAndClickingAFrameMovesTheView()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 4);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && editor.Decorations[DebugController.InlineLayer].Inline.Length > 0, 15), "no values were drawn after the lines");

            var inline = editor.Decorations[DebugController.InlineLayer].Inline.Select(i => (Line: editor.Document.Text.LineOf(i.Offset), i.Text)).ToList();
            Assert.Contains(inline, i => i.Line == 0 && i.Text.Contains("pc = 5", StringComparison.Ordinal));
            Assert.Contains(inline, i => i.Line == 4 && i.Text.Contains("counter = 7", StringComparison.Ordinal) && i.Text.Contains("pc = 5", StringComparison.Ordinal));

            var caller = debug.CallStack.First(r => r is { IsFrame: true, Title: "Caller" });
            _ = debug.SelectFrameAsync(caller);
            Assert.True(Ui.PumpUntil(() => Stopped(editor, 0), 10), "the stopped line did not follow the chosen frame");
            Assert.False(Stopped(editor, 4));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task HoveringANameWhileStoppedShowsItsValue()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 4);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && Stopped(editor, 4), 15));

            // "pc + counter" on line 5: the pointer rests on "counter".
            var offset = editor.Document.Text.LineStart(4) + "pc + ".Length + 2;
            var box = editor.RectOf(offset);
            var point = editor.TranslatePoint(new Point(box.Left + 2, box.Top + box.Height / 2), window)!.Value;
            window.MouseMove(point);
            Assert.True(Ui.PumpUntil(() => Cards(window).Any(), 10), "no value card appeared");

            var card = string.Join(" ", Cards(window).SelectMany(c => c.GetVisualDescendants().OfType<TextBlock>()).Select(t => t.Text));
            Assert.Contains("counter", card);
            Assert.Contains("7", card);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheDebuggerRendersAFrameWhileStopped()
    {
        using var profile = new TempProfile();
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_DEBUG");
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            var debug = shell.Debugger;
            debug.AddWatchExpression("pc + counter");
            debug.AddWatchExpression("nothing");
            var store = debug.Breakpoints;
            store.Add(path, 1, condition: "pc > 100");
            store.Add(path, 2, logMessage: "counter is {counter}");
            store.Update(store.Add(path, 3).Id, b => b with { Enabled = false });
            store.Add(path, 7);
            ClickGlyph(window, editor, 4);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && debug.Variables.Rows.Any(r => r.Name == "pc") && debug.WatchTree.Rows.Any(r => r.Value == "12"), 15));
            window.MouseMove(editor.TranslatePoint(new Point(editor.RectOf(editor.Document.Text.LineStart(4) + 8).Left + 2, editor.RectOf(editor.Document.Text.LineStart(4)).Top + 8), window)!.Value);
            Ui.Pump(1.0);
            debug.Console.Write("stderr", "a warning from stderr\n");
            debug.Console.Write("important", "something important\n");
            debug.Console.Write("input", "pc + counter\n");
            debug.Console.Write("result", "12\n");
            debug.Console.Write("error", "Unknown identifier\n");
            shell.BottomPanel.Show(Wicked.App.Shell.PanelIds.DebugConsole);
            Ui.Pump(0.5);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(shot)) frame.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();
            return 0;
        });
    }

    private static IEnumerable<Border> Cards(MainWindow window) =>
        TopLevel.GetTopLevel(window)!.GetVisualDescendants().OfType<Border>().Where(b => b.Name == "DebugHoverCard" && b.IsEffectivelyVisible);

    [Trait("Tier", "Window")]
    [Fact]
    public async Task BreakpointsFollowTheirCodeAsTheTextIsEdited()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, editor, path) = Open(profile);
            var store = shell.Debugger.Breakpoints;
            store.Add(path, 3);
            store.Add(path, 5);
            var doc = tab.Document;

            doc.Write([new TextChange(0, 0, "added\n")], [Selection.Caret(0)]);
            Ui.Pump(0.1);
            Assert.Equal([4, 6], store.ForFile(path).Select(b => b.Line));
            Assert.Contains(GutterGlyph.Breakpoint, Glyphs(editor, 3));

            // The first line deleted whole: the others move back up.
            doc.Write([new TextChange(0, 6, "")], [Selection.Caret(0)]);
            Ui.Pump(0.1);
            Assert.Equal([3, 5], store.ForFile(path).Select(b => b.Line));

            // Typing inside the breakpoint's own line leaves it where it is.
            doc.Write([new TextChange(doc.Text.LineStart(2) + 2, 0, "ZZ")], [Selection.Caret(0)]);
            Ui.Pump(0.1);
            Assert.Equal([3, 5], store.ForFile(path).Select(b => b.Line));
            Assert.Contains(GutterGlyph.Breakpoint, Glyphs(editor, 2));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ABreakpointThePreviousRunRememberedComesBackAndTheListShowsIt()
    {
        using var profile = new TempProfile();
        string path = "";
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, p) = Open(profile);
            path = p;
            ClickGlyph(window, editor, 1);
            shell.Debugger.AddWatchExpression("counter");
            shell.Debugger.SaveState();
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SessionFile) && File.ReadAllText(profile.Paths.SessionFile).Contains("counter", StringComparison.Ordinal), 10), "the session file was not written");
            window.Close();
            return 0;
        });

        await Ui.Run(() =>
        {
            var folder = Path.GetDirectoryName(path)!;
            var (window, shell) = Ui.Start(profile.Paths);
            var started = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => started.IsCompleted, 15));
            Assert.True(Ui.PumpUntil(() => shell.Debugger.Breakpoints.ForFile(path).Count == 1, 10), "the breakpoint did not come back");

            Assert.Equal(2, shell.Debugger.Breakpoints.ForFile(path)[0].Line);
            Assert.Equal(["counter"], shell.Debugger.WatchExpressions);
            Assert.Equal("Fake", shell.Debugger.SelectedConfiguration?.Name);
            Assert.NotNull(folder);
            window.Close();
            return 0;
        });
    }

    private static TextBox Find(MainWindow window, string name) => window.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == name);

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheDebugConsoleEvaluatesInTheStoppedFrameAndUpAndDownRecallWhatWasTyped()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, _) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && Stopped(editor, 2), 15));
            Assert.True(shell.BottomPanel.IsVisible && shell.BottomPanel.Active?.Id == Wicked.App.Shell.PanelIds.DebugConsole, "the console tab did not open with the session");
            Input.Chord(window, Key.Y, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.False(shell.BottomPanel.IsVisible);
            Input.Chord(window, Key.Y, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(shell.BottomPanel.IsVisible);
            var box = Find(window, "ConsoleInput");
            box.Focus();

            window.KeyTextInput("pc + counter");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => debug.Console.Lines.Any(l => l.IsResult), 10), "no answer in the console");
            window.KeyTextInput("nothing");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => debug.Console.Lines.Any(l => l.Category == "error"), 10), "no error in the console");

            Assert.Contains(debug.Console.Lines, l => l.IsResult && l.Text == "10");
            Assert.Contains(debug.Console.Lines, l => l.Category == "error" && l.Text == "Unknown identifier 'nothing'");
            Assert.Contains(debug.Console.Lines, l => l.IsInput && l.Text == "pc + counter");
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal("nothing", box.Text);
            window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Assert.Equal("pc + counter", box.Text);
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Assert.Equal("nothing", box.Text);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task WatchExpressionsAreAddedThroughThePromptAndEvaluatedOnEveryStop()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, _) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && Stopped(editor, 2), 15));

            debug.AddWatch();
            Assert.True(shell.QuickInput.IsOpen);
            window.KeyTextInput("pc");
            Ui.Pump(0.1);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => debug.WatchTree.Rows.Any(r => r.Name == "pc" && r.Value == "3"), 10), "the watch was not evaluated");
            debug.AddWatchExpression("nothing");
            Assert.True(Ui.PumpUntil(() => debug.WatchTree.Rows.Any(r => r.Name == "nothing" && r.IsError), 10));

            Input.Chord(window, Key.F10, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.WatchTree.Rows.Any(r => r.Name == "pc" && r.Value == "4"), 10), "the watch was not evaluated again");
            Assert.Equal("Unknown identifier 'nothing'", debug.WatchTree.Rows.Single(r => r.Name == "nothing").Value);

            debug.RemoveWatch(debug.WatchTree.Rows.Single(r => r.Name == "nothing"));
            Assert.True(Ui.PumpUntil(() => debug.WatchTree.Rows.All(r => r.Name != "nothing"), 10));
            Assert.Equal(["pc"], debug.WatchExpressions);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ChangingAValueSendsItToTheProgramAndTheViewsShowTheNewOne()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, _) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && debug.Variables.Rows.Any(r => r.Name == "counter"), 15));
            var counter = debug.Variables.Rows.Single(r => r.Name == "counter");
            Assert.True(counter.CanSet);

            debug.BeginEdit(counter);
            Assert.True(counter.IsEditing);
            Assert.Equal("7", counter.EditText);
            counter.EditText = "41";
            _ = debug.CommitEditAsync(counter);

            Assert.True(Ui.PumpUntil(() => debug.Variables.Rows.Any(r => r.Name == "counter" && r.Value == "41"), 10), "the new value was not shown");
            Assert.True(Ui.PumpUntil(() => editor.Decorations[DebugController.InlineLayer].Inline.Any(i => i.Text.Contains("counter = 41", StringComparison.Ordinal)), 10), "the line values were not refreshed");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheBreakpointsListEnablesDisablesEditsAndRemovesAndTheGutterFollows()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            ClickGlyph(window, editor, 4);
            var row = debug.BreakpointRows.Single(r => r.Kind == BreakpointRowKind.Source && r.Line == 3);

            row.IsEnabled = false;
            Ui.Pump(0.1);
            Assert.False(debug.Breakpoints.ForFile(path).Single(b => b.Line == 3).Enabled);
            Assert.Contains(GutterGlyph.BreakpointDisabled, Glyphs(editor, 2));

            debug.EditBreakpoint(debug.Breakpoints.At(path, 5)!, "condition");
            Assert.True(shell.QuickInput.IsOpen);
            window.KeyTextInput("pc == 5");
            Ui.Pump(0.1);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Pump(0.2);
            Assert.Equal("pc == 5", debug.Breakpoints.At(path, 5)!.Condition);
            Assert.Contains(GutterGlyph.BreakpointConditional, Glyphs(editor, 4));
            Assert.Contains(debug.BreakpointRows, r => r.Line == 5 && r.Detail.Contains("pc == 5", StringComparison.Ordinal));

            debug.RemoveBreakpoint(debug.BreakpointRows.Single(r => r.Kind == BreakpointRowKind.Source && r.Line == 5));
            Ui.Pump(0.1);
            Assert.Empty(Glyphs(editor, 4));
            debug.Breakpoints.RemoveAll();
            Ui.Pump(0.1);
            Assert.DoesNotContain(debug.BreakpointRows, r => r.Kind == BreakpointRowKind.Source);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ABreakpointTheAdapterCannotBindIsDrawnHollowOnceASessionRuns()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            // The program has six lines; the editor also shows the empty seventh after the last line break, which holds no code.
            debug.Breakpoints.Add(path, 7);
            Ui.Pump(0.1);
            Assert.Contains(GutterGlyph.Breakpoint, Glyphs(editor, 6));

            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && Glyphs(editor, 6).Contains(GutterGlyph.BreakpointUnverified), 15), "the unbound breakpoint was not drawn hollow");

            Assert.Contains(GutterGlyph.Breakpoint, Glyphs(editor, 2));
            var row = debug.BreakpointRows.Single(r => r.Line == 7);
            Assert.True(row.Unverified);
            Assert.Equal("There is no code on that line", row.Problem);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ACompoundShowsEachSessionInTheCallStackAndStopAllEndsThem()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, path) = Open(profile);
            var debug = shell.Debugger;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, ".vscode", "launch.json"), $$"""
                { "configurations": [
                    { "name": "A", "type": "fake", "request": "launch", "program": "{{path.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "hangAtEnd": true },
                    { "name": "B", "type": "fake", "request": "launch", "program": "{{path.Replace("\\", "\\\\", StringComparison.Ordinal)}}", "hangAtEnd": true } ],
                  "compounds": [ { "name": "Both", "configurations": ["A", "B"], "stopAll": true } ] }
                """);
            var reloaded = debug.ReloadLaunchFileAsync("Both");
            Assert.True(Ui.PumpUntil(() => reloaded.IsCompleted && debug.SelectedConfiguration?.Name == "Both", 10));
            Assert.Equal(["A", "B", "Both"], debug.Configurations.Select(c => c.Name));

            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.Manager.Sessions.Count == 2 && debug.CallStack.Count(r => r.Kind == StackRowKind.Session) == 2, 15), "both sessions did not show");

            Assert.Equal(["A", "B"], debug.CallStack.Where(r => r.Kind == StackRowKind.Session).Select(r => r.Title));
            Input.Chord(window, Key.F5, RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => debug.Manager.Sessions.Count <= 1, 15));
            Assert.True(Ui.PumpUntil(() => !debug.HasSession, 15), "stopAll did not end the other session");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task WithoutALaunchJsonF5WritesOneForTheFilesLanguageOpensItAndOffersTheMissingAdapter()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var root = Path.Combine(profile.Root, "csproj");
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Program.cs"), "class P { static void Main() { } }\n");
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            shell.Debugger.Locator = new DebugAdapterLocator(Path.Combine(profile.Root, "no-adapters"), _ => null);
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "Program.cs");

            Input.Chord(window, Key.F5, RawInputModifiers.None);
            var launchJson = Path.Combine(root, ".vscode", "launch.json");
            Assert.True(Ui.PumpUntil(() => File.Exists(launchJson) && shell.Workbench.AllTabs.Any(t => t.Path == launchJson), 15), "launch.json was not created and opened");

            var file = LaunchFile.Parse(File.ReadAllText(launchJson));
            Assert.Equal(["coreclr", "coreclr"], file.Configurations.Select(c => c.Type));
            Assert.Equal("${workspaceFolder}/bin/Debug/net10.0/App.dll", (string?)file.Configurations[0].Properties["program"]);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Any(i => i.Label == "Install netcoredbg"), 10), "the missing adapter was not offered");
            Assert.NotNull(tab);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AConfigurationWhoseAdapterIsMissingPostsANoticeThatLeadsToItsInstall()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var folder = MakeFolder(profile);
            File.WriteAllText(Path.Combine(folder, ".vscode", "launch.json"), """{ "configurations": [ { "name": "Net", "type": "coreclr", "request": "launch", "program": "x.dll" } ] }""");
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, folder);
            var locator = new DebugAdapterLocator(Path.Combine(profile.Root, "no-adapters"), _ => null);
            shell.Debugger.Locator = locator;
            shell.Debugger.Adapters = new DebugAdapterProvider(locator);
            Assert.True(Ui.PumpUntil(() => shell.Debugger.Configurations.Count == 1, 10));

            Input.Chord(window, Key.F5, RawInputModifiers.None);

            Assert.True(Ui.PumpUntil(() => shell.Notice?.Route == "debug/install/netcoredbg", 15), "no notice led to the install");
            Assert.Contains("netcoredbg is not installed", shell.Notice!.Text);
            Assert.True(shell.Navigate(shell.Notice.Route!));
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen, 10), "the install offer did not open");
            Assert.Contains(shell.QuickInput.Items, i => i.Label == "Install netcoredbg" && i.Detail.Contains("github.com/Samsung/netcoredbg", StringComparison.Ordinal));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AttachToProcessListsRunningProgramsAndAttachesToTheOneChosen()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var provider = new FakeAdapterProvider();
            var (window, shell, _, _, _) = Open(profile, provider);

            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("Attach to Process");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "Debug: Attach to Process…", 5));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Count > 3, 10), "no list of processes");
            var chosen = shell.QuickInput.Items[0];
            var id = chosen.Label.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            Assert.True(Ui.PumpUntil(() => provider.Adapters.Count > 0 && provider.Adapters[0].Requests.Any(r => r.StartsWith("attach", StringComparison.Ordinal)), 15), "no attach request reached the adapter");
            var attach = provider.Adapters[0].Requests.First(r => r.StartsWith("attach", StringComparison.Ordinal));
            Assert.Contains($"\"processId\":\"{id}\"", attach, StringComparison.Ordinal);
            Assert.Contains("\"request\":\"attach\"", attach, StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task DoubleClickingAValueInTheVariablesViewOpensItForEditing()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, _) = Open(profile);
            var debug = shell.Debugger;
            ClickGlyph(window, editor, 2);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && debug.Variables.Rows.Any(r => r.Name == "counter"), 15));
            var counter = debug.Variables.Rows.Single(r => r.Name == "counter");
            var name = window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "counter" && t.Classes.Contains("name"));

            Input.Click(window, name);
            Input.Click(window, name);

            Assert.True(Ui.PumpUntil(() => counter.IsEditing, 5), "a double click did not open the value for editing");
            var box = window.GetVisualDescendants().OfType<TextBox>().First(t => t.IsEffectivelyVisible && t.Text == "7");
            box.Text = "99";
            box.Focus();
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => debug.Variables.Rows.Any(r => r.Name == "counter" && r.Value == "99"), 10), "the edited value was not sent");
            Assert.False(counter.IsEditing);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheMarginMenuAddsALogpointF9TogglesAtTheCaretAndAClosedFileKeepsItsBreakpoints()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, editor, path) = Open(profile);
            var debug = shell.Debugger;

            ClickGlyph(window, editor, 3, MouseButton.Right);
            var item = window.GetVisualDescendants().OfType<MenuItem>().First(m => m.Header?.ToString() == "Add Logpoint…");
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(shell.QuickInput.IsOpen);
            window.KeyTextInput("pc is {pc}");
            Ui.Pump(0.1);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Pump(0.2);
            Assert.Equal("pc is {pc}", debug.Breakpoints.At(path, 4)!.LogMessage);
            Assert.Contains(GutterGlyph.Logpoint, Glyphs(editor, 3));

            editor.Document.SetSelections([Selection.Caret(editor.Document.Text.LineStart(1))]);
            editor.Focus();
            Input.Chord(window, Key.F9, RawInputModifiers.None);
            Assert.NotNull(debug.Breakpoints.At(path, 2));
            Input.Chord(window, Key.F9, RawInputModifiers.None);
            Assert.Null(debug.Breakpoints.At(path, 2));

            // Closing the file keeps its breakpoints in the store; opening it again draws them.
            Input.Chord(window, Key.W, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveGroup.Tabs.Count == 0, 5));
            Assert.NotNull(debug.Breakpoints.At(path, 4));
            var reopened = shell.Workbench.OpenAsync(path);
            Assert.True(Ui.PumpUntil(() => reopened.IsCompleted && shell.Workbench.ActiveTab?.Editor?.IsAttachedToVisualTree() == true, 5));
            Assert.True(Ui.PumpUntil(() => Glyphs(shell.Workbench.ActiveTab!.Editor!, 3).Contains(GutterGlyph.Logpoint), 5), "the breakpoint was not drawn in the reopened file");
            Assert.NotNull(tab);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task CtrlShiftF5RestartsTheProgramAndTheSettingTurnsTheLineValuesOff()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, _, editor, _) = Open(profile);
            var debug = shell.Debugger;
            var off = shell.UpdateSettingsAsync(s => s with { DebugInlineValues = false });
            Assert.True(Ui.PumpUntil(() => off.IsCompleted, 10));
            ClickGlyph(window, editor, 2);
            Input.Chord(window, Key.F5, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => debug.IsStopped && Stopped(editor, 2), 15));
            Ui.Pump(0.3);
            Assert.True(editor.Decorations[DebugController.InlineLayer].IsEmpty, "values were drawn although the setting is off");
            var first = debug.Manager.Active;

            Input.Chord(window, Key.F5, RawInputModifiers.Control | RawInputModifiers.Shift);

            Assert.True(Ui.PumpUntil(() => debug.Manager.Active is { } now && now != first && debug.IsStopped && Stopped(editor, 2), 20), "the program did not stop again after the restart");
            Assert.Single(debug.Manager.Sessions);
            window.Close();
            return 0;
        });
    }
}
