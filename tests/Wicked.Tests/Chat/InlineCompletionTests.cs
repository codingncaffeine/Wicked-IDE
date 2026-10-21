using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.Assistant;
using Wicked.Core.Chat;
using Wicked.Editor.Model;

namespace Wicked.Tests.Chat;

/// <summary>Ghost text: asked for when typing pauses, drawn after the caret, taken with Tab, walked into, dropped.</summary>
public sealed class InlineCompletionTests
{
    [Theory]
    [InlineData("(a, b);", ");", "(a, b")]
    [InlineData("total + 1;\n\n", "", "total + 1;")]
    [InlineData("x", "", "x")]
    public void TheSuggestionLosesWhatAlreadyFollowsTheCaret(string model, string rest, string expected) => Assert.Equal(expected, InlineCompletions.Clean(model, rest));

    [Fact]
    public void TheSuggestionStopsWhereTheModelStartsTalkingOrRepeatsTheNextLine()
    {
        // What the real 0.5B coder said for "return " in add(): the code, then a fence and prose.
        Assert.Equal("a + b", InlineCompletions.Clean("a + b\n```\n\nIn this solution, we define", "", "\n\nprint(add(2, 3))\n"));
        Assert.Equal("x = 1\n    y = 2", InlineCompletions.Clean("x = 1\n    y = 2\n    return x\n    return x", "", "\n    return x\n"));
    }

    [Theory]
    [InlineData("foo(bar)", "foo")]
    [InlineData(" bar baz", " bar")]
    [InlineData("(x)", "(")]
    public void CtrlRightTakesTheNextWord(string suggestion, string word) => Assert.Equal(word, InlineCompletions.NextWord(suggestion));

    private static (App.Views.MainWindow Window, App.ViewModels.ShellViewModel Shell, App.Workbench.EditorTab Tab) Open(ChatWorld world, TempProfile profile, string text)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "calc.cs"), text);
        File.WriteAllText(Path.Combine(root, "other.cs"), "static int sum(int a, int b) => a + b;\n");
        var (window, shell) = world.Start(profile.Paths);
        var opened = shell.OpenFolderAndRememberAsync(root);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted && WorkbenchWindowTests.Rows(window).Any(), 10));
        var other = shell.Workbench.OpenAsync(Path.Combine(root, "other.cs"));
        Assert.True(Ui.PumpUntil(() => other.IsCompleted, 5));
        var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "calc.cs");
        return (window, shell, tab);
    }

    [Fact]
    public async Task APauseInTypingAsksTheCodingModelAndTabPutsTheSuggestionIn()
    {
        using var profile = new TempProfile();
        var world = new ChatWorld();
        world.Runtime.Set(new RunningModel("chat:Q4_K_M", "Some Chat Model", RunState.Loaded, null, false, false, 8192, null),
                          new RunningModel("qwen2.5-coder-1.5b:Q8_0", "Qwen2.5 Coder 1.5B", RunState.Unloaded, null, false, false, null, null));
        world.Infill.Answer = r => r.Prefix.EndsWith("sum", StringComparison.Ordinal) ? "(2, 3);" : "";
        await Ui.Run(() =>
        {
            var (window, shell, tab) = Open(world, profile, "var x = \n");
            var doc = tab.Document;
            doc.SetSelections([Selection.Caret("var x = ".Length)]);
            window.KeyTextInput("sum");
            var ghosts = shell.GhostsFor(tab.Editor)!;
            Assert.True(Ui.PumpUntil(() => ghosts.Suggestion == "(2, 3);", 5), "no suggestion");
            var request = world.Infill.Requests.Last();
            Assert.Equal("qwen2.5-coder-1.5b:Q8_0", request.Model);                       // the coding model, not the chat one
            Assert.Equal("var x = sum", request.Prefix);
            Assert.Equal("\n", request.Suffix);
            Assert.Contains(request.Extra, e => e.FileName == "other.cs" && e.Text.Contains("static int sum", StringComparison.Ordinal));
            Assert.Contains(tab.Editor!.Decorations[InlineCompletions.Layer].Inline, i => i.Text == "(2, 3);" && i.Offset == doc.Primary.Active);

            // Typing what it says walks into it; Tab takes the rest as one step.
            window.KeyTextInput("(");
            Ui.Pump(0.05);
            Assert.Equal("2, 3);", ghosts.Suggestion);
            Input.Chord(window, Key.Tab, RawInputModifiers.None);
            Assert.Equal("var x = sum(2, 3);\n", doc.Text.ToString());
            Assert.Null(ghosts.Suggestion);
            Assert.Empty(tab.Editor.Decorations[InlineCompletions.Layer].Inline);
            Assert.Equal("var x = sum(2, 3);".Length, doc.Primary.Active);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task EscapeOrMovingAwayDropsItAndTabIndentsAgain()
    {
        using var profile = new TempProfile();
        var world = new ChatWorld();
        world.Runtime.Set(new RunningModel("tiny-coder:Q4", "Tiny Coder", RunState.Loaded, null, false, false, 4096, null));
        world.Infill.Answer = _ => "done();";
        await Ui.Run(() =>
        {
            var (window, shell, tab) = Open(world, profile, "\n");
            var ghosts = shell.GhostsFor(tab.Editor)!;
            tab.Document.SetSelections([Selection.Caret(0)]);
            window.KeyTextInput("x");
            Assert.True(Ui.PumpUntil(() => ghosts.Suggestion is not null, 5));
            Input.Chord(window, Key.Escape, RawInputModifiers.None);
            Assert.Null(ghosts.Suggestion);
            Input.Chord(window, Key.Tab, RawInputModifiers.None);
            Assert.Equal("x   \n", tab.Document.Text.ToString());                       // Tab is Tab again (to the next stop)

            window.KeyTextInput("y");
            Assert.True(Ui.PumpUntil(() => ghosts.Suggestion is not null, 5));
            tab.Document.SetSelections([Selection.Caret(0)]);                             // the caret moved away
            Ui.Pump(0.05);
            Assert.Null(ghosts.Suggestion);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task WithoutACodingModelNothingIsAsked()
    {
        using var profile = new TempProfile();
        var world = new ChatWorld();
        world.Runtime.Set(new RunningModel("chat:Q4", "Chat Only", RunState.Loaded, null, false, false, 4096, null));
        await Ui.Run(() =>
        {
            var (window, _, tab) = Open(world, profile, "\n");
            tab.Document.SetSelections([Selection.Caret(0)]);
            window.KeyTextInput("abc");
            Ui.Pump(0.6);
            Assert.Empty(world.Infill.Requests);
            window.Close();
            return 0;
        });
    }
}
