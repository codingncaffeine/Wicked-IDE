using Avalonia.Headless;
using Avalonia.Input;
using Wicked.Editor.Model;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>The code editor's own actions in the palette (with the editor's keys shown), and the selection handed to the assistant.</summary>
public sealed class EditorActionsWindowTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task ThePaletteListsTheEditorsActionsWithTheirKeysAndRunsThemOnTheEditor()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.cs"), "var a = 1;\nvar b = 2;\n");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var tab = OpenByClicks(window, shell, "App.cs");

            // Ctrl+Shift+P, "toggle line comment": the editor's command, its key as people read it; Enter runs it there.
            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("toggle line comment");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "Edit: Toggle Line Comment", 5), "not in the palette");
            Assert.Equal("Ctrl+/", shell.QuickInput.Items[0].Keys);
            Input.Chord(window, Key.Enter, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => tab.Document.Text.ToString().StartsWith("// var a", StringComparison.Ordinal) && tab.Editor!.IsFocused, 5),
                tab.Document.Text.ToString());

            // The key itself still belongs to the editor: Ctrl+/ takes the comment off again.
            Input.Chord(window, Key.Oem2, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => tab.Document.Text.ToString().StartsWith("var a", StringComparison.Ordinal), 5), tab.Document.Text.ToString());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AddToChatQuotesTheSelectionWithItsFileAndLines()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.cs"), "var a = 1;\nvar b = 2;\nvar c = 3;\n");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var tab = OpenByClicks(window, shell, "App.cs");
            var text = tab.Document.Text;
            tab.Document.SetSelections([new Selection(text.LineStart(1), text.LineStart(3))]);
            var run = shell.RunCommandAsync("wicked.assistant.addSelectionToChat");
            Assert.True(Ui.PumpUntil(() => run.IsCompleted, 5));
            Assert.True(shell.AssistantVisible);
            Assert.Equal("App.cs, lines 2–3:\n```csharp\nvar b = 2;\nvar c = 3;\n```\n", shell.Chat.Draft);

            // A second one goes after what is there; with no selection it quotes the caret's line.
            tab.Document.SetSelections([Selection.Caret(0)]);
            run = shell.RunCommandAsync("wicked.assistant.addSelectionToChat");
            Assert.True(Ui.PumpUntil(() => run.IsCompleted, 5));
            Assert.EndsWith("```\n\nApp.cs, line 1:\n```csharp\nvar a = 1;\n```\n", shell.Chat.Draft, StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }
}
