using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Shell;
using Wicked.App.Terminal;
using Wicked.Core.Terminal;
using Wicked.Editor.Model;

namespace Wicked.Tests.Terminal;

/// <summary>The terminal in the window: Ctrl+` opens one in the panel, and an editor's line runs in it.</summary>
public sealed class TerminalShellTests
{
    private static string ScreenOf(TerminalInstanceViewModel t)
    {
        var frame = t.Pane?.View.Frame;
        if (frame is null) return "";
        return string.Join('\n', frame.Lines.Select(row => string.Concat(row.Cells.Where(c => (c.Flags & CellFlags.WideTail) == 0).Select(c => frame.TextOf(c))).TrimEnd())).TrimEnd();
    }

    [Fact]
    public async Task CtrlBacktickOpensATerminalInThePanelAndTheEditorsLineRunsInIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a POSIX shell");
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "run.sh"), "echo from-the-editor-$((40+2))\n");
        File.WriteAllText(Path.Combine(root, "empty-rc"), "");
        Directory.CreateDirectory(profile.Paths.Config);
        // A plain sh with an empty rc file: nothing of this machine's shell setup runs in the test.
        File.WriteAllText(profile.Paths.SettingsFile, """{ "terminal.integrated.defaultProfile": "sh" }""");
        Environment.SetEnvironmentVariable("ENV", Path.Combine(root, "empty-rc"));
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "run.sh");

            Input.Chord(window, Key.OemTilde, RawInputModifiers.Control);
            Assert.True(shell.BottomPanel.IsVisible);
            Assert.Equal(PanelIds.Terminal, shell.BottomPanel.Active?.Id);
            var terminal = Assert.Single(shell.Terminal.AllTerminals());
            Assert.True(Ui.PumpUntil(() => terminal.Pane is not null && terminal.Session is not null, 20), "the shell did not start");
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<TerminalPanelView>().Any(v => v.IsEffectivelyVisible), 5));

            // Back in the editor: the line runs in the terminal.
            tab.Editor!.Focus();
            tab.Document.SetSelections([Selection.Caret(0)]);
            var command = shell.Commands.Single(c => c.Id == "workbench.action.terminal.runSelectedText");
            var run = command.Run();
            Assert.True(Ui.PumpUntil(() => run.IsCompleted, 20));
            Assert.True(Ui.PumpUntil(() => ScreenOf(terminal).Contains("from-the-editor-42", StringComparison.Ordinal), 20), ScreenOf(terminal));

            // Ctrl+` again hides the panel.
            Input.Chord(window, Key.OemTilde, RawInputModifiers.Control);
            Assert.False(shell.BottomPanel.IsVisible);
            window.Close();
            return 0;
        });
    }
}
