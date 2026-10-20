using Avalonia.Input;
using Wicked.App.Editor;
using Wicked.App.Shell;
using Wicked.Core.Settings;

namespace Wicked.Tests;

public sealed class KeybindingsTests
{
    [Theory]
    [InlineData("ctrl+shift+k", "Ctrl+Shift+K")]
    [InlineData("ctrl+k ctrl+o", "Ctrl+K Ctrl+O")]
    [InlineData("ctrl+/", "Ctrl+Oem2")]
    [InlineData("alt+pagedown", "Alt+PageDown")]
    [InlineData("ctrl++", "Ctrl+OemPlus")]
    [InlineData("shift+f3", "Shift+F3")]
    [InlineData("cmd+1", "Meta+D1")]
    public void VsCodeKeysTranslate(string vscode, string avalonia) => Assert.Equal(avalonia, Keybindings.Translate(vscode));

    [Fact]
    public void UnknownKeysAreSkippedAndCommentsAllowed()
    {
        var (bindings, problem) = Keybindings.Parse("""
            // mine
            [
              { "key": "ctrl+shift+d", "command": "editor.action.copyLinesDownAction" },
              { "key": "ctrl+nosuchkey", "command": "x" },
              { "key": "ctrl+w", "command": "-workbench.action.closeActiveEditor" },
            ]
            """);
        Assert.Null(problem);
        Assert.Equal(2, bindings.Count);
        Assert.True(bindings[1].Remove);
        Assert.Equal("workbench.action.closeActiveEditor", bindings[1].Command);
    }

    [Fact]
    public void ABrokenFileSaysSo() => Assert.NotNull(Keybindings.Parse("[ { \"key\": ").Problem);

    [Fact]
    public void OverridesComeFirstAndRemovalsGo()
    {
        var commands = new List<WorkbenchCommand>
        {
            new("a", "", "A", "Ctrl+W", () => Task.CompletedTask),
            new("b", "", "B", null, () => Task.CompletedTask),
        };
        var keys = new KeyDispatcher(commands, [new Keybinding("Ctrl+W", "a", Remove: true), new Keybinding("Ctrl+K Ctrl+B", "b", Remove: false)]);
        Assert.Equal("", keys.KeysFor("a"));
        Assert.Equal("Ctrl+K Ctrl+B", keys.KeysFor("b"));
    }

    [Fact]
    public async Task KeybindingsJsonEditedByHandRebindsAnEditorCommand()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "p");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\ntwo\nthree\n");
        try
        {
            await Ui.Run(() =>
            {
                var (window, shell) = Ui.Start(profile.Paths);
                var start = shell.StartAsync();
                Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
                var opening = shell.Workbench.OpenAsync(Path.Combine(root, "a.txt"));
                Assert.True(Ui.PumpUntil(() => opening.IsCompleted && shell.Workbench.ActiveTab?.Editor is not null, 5));
                var editor = shell.Workbench.ActiveTab!.Editor!;
                Input.Click(window, editor);
                editor.Document.SetSelections([Wicked.Editor.Model.Selection.Caret(0)]);

                Directory.CreateDirectory(profile.Paths.Config);
                File.WriteAllText(profile.Paths.KeybindingsFile, "[ { \"key\": \"ctrl+e\", \"command\": \"editor.action.deleteLines\" } ]");
                Assert.True(Ui.PumpUntil(() => EditorKeymap.Effective[0].Command.Id == "editor.action.deleteLines", 5), "the file was not applied");

                Input.Chord(window, Key.E, RawInputModifiers.Control);
                Assert.Equal("two\nthree\n", editor.Document.Text.ToString());
                window.Close();
                return 0;
            });
        }
        finally
        {
            EditorKeymap.Apply([]);
        }
    }
}
