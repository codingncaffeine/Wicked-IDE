using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Wicked.App.Assistant;
using Wicked.Core.Chat;
using Wicked.Editor.Model;
using Wicked.Tests.Git;

namespace Wicked.Tests.Chat;

/// <summary>The assistant's smaller hands: Apply on a code block, context in a question, commit messages, Fix on a problem.</summary>
public sealed class AssistantExtrasTests
{
    private static RunningModel Model => new("m:Q4", "Model", RunState.Loaded, null, false, false, 8192, null);

    private static (App.Views.MainWindow Window, App.ViewModels.ShellViewModel Shell, App.Workbench.EditorTab Tab) Open(ChatWorld world, TempProfile profile, string file, string text)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, file), text);
        File.WriteAllText(Path.Combine(root, "notes.md"), "the notes say hello\n");
        var (window, shell) = world.Start(profile.Paths);
        var opened = shell.OpenFolderAndRememberAsync(root);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted && WorkbenchWindowTests.Rows(window).Any() && shell.Workbench.Index is not null, 10));
        var tab = WorkbenchWindowTests.OpenByClicks(window, shell, file);
        Assert.True(Ui.PumpUntil(() => shell.Chat.HasSelectedModel, 5));
        return (window, shell, tab);
    }

    [Fact]
    public async Task AQuestionCarriesTheOpenFileAndItsMentionsButShowsOnlyWhatWasTyped()
    {
        using var profile = new TempProfile();
        var world = new ChatWorld();
        world.Runtime.Set(Model);
        await Ui.Run(() =>
        {
            var (window, shell, _) = Open(world, profile, "a.cs", "class Alpha {}\n");
            Assert.Equal("a.cs", shell.Chat.ActiveFileName);
            shell.Chat.Draft = "compare with @file:notes.md please";
            shell.Chat.SendCommand.Execute(null);
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 1 && !shell.Chat.IsStreaming, 10));
            var sent = world.Engine.Requests[0].Messages.Last(m => m.Role == ChatRole.User).Content;
            Assert.Contains("<file path=\"notes.md\">\nthe notes say hello", sent, StringComparison.Ordinal);
            Assert.Contains("<file path=\"a.cs\">\nclass Alpha {}", sent, StringComparison.Ordinal);
            Assert.EndsWith("compare with @file:notes.md please", sent, StringComparison.Ordinal);
            Assert.Equal("compare with @file:notes.md please", shell.Chat.Messages[0].Text);        // the chat shows what was typed

            // The chip off: the open file stays out.
            shell.Chat.IncludeActiveFile = false;
            shell.Chat.Draft = "and now?";
            shell.Chat.SendCommand.Execute(null);
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 2 && !shell.Chat.IsStreaming, 10));
            Assert.Equal("and now?", world.Engine.Requests[1].Messages.Last(m => m.Role == ChatRole.User).Content);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ApplyOnACodeBlockProposesItOverTheSelectionAsAReview()
    {
        using var profile = new TempProfile();
        var world = new ChatWorld();
        world.Runtime.Set(Model);
        world.Engine.Script = (r, ct) => FakeEngine.Reply("Try this:\n\n```csharp\nint total = a + b;\n```\n", 2, TimeSpan.FromMilliseconds(5), ct);
        await Ui.Run(() =>
        {
            var (window, shell, tab) = Open(world, profile, "a.cs", "void F()\n{\n    int total = a;\n}\n");
            var doc = tab.Document;
            var line = doc.Text.LineStart(2) + 4;
            doc.SetSelections([new Selection(line, line + "int total = a;".Length)]);
            shell.Chat.Draft = "fix it";
            shell.Chat.SendCommand.Execute(null);
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && world.Engine.Requests.Count == 1, 10));
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ApplyCode" && b.IsEffectivelyVisible), 5), "no Apply button");
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "ApplyCode" && b.IsEffectivelyVisible));
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.CustomView is EditReviewView, 5), "no review");
            var review = (EditReviewView)shell.Workbench.ActiveTab!.CustomView!;
            Assert.Equal("int total = a;", review.Proposal.Original);
            Assert.Equal("int total = a + b;", review.Proposal.Proposed);
            review.Proposal.AcceptAll();
            Assert.Equal("void F()\n{\n    int total = a + b;\n}\n", doc.Text.ToString());
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheSparkleWritesACommitMessageFromTheStagedChanges()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var sandbox = box;
        await box.CommitAsync("first", ("greet.txt", "hello\n"));
        box.Write("greet.txt", "hello world\n");
        await box.GitAsync("add", "greet.txt");
        var world = new ChatWorld();
        world.Runtime.Set(Model);
        world.Engine.Script = (r, ct) => FakeEngine.Reply("Greet the whole world", 2, TimeSpan.FromMilliseconds(5), ct);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            shell.SourceControl.Environment = box.Env;
            var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted, 10));
            Input.Click(window, window.FindControl<Button>("RailSourceControl")!);
            Assert.True(Ui.PumpUntil(() => shell.Chat.HasSelectedModel && shell.SourceControl.ActiveGit is not null, 10));
            var sparkle = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "WriteCommitMessage");
            Assert.True(Ui.PumpUntil(() => sparkle.IsEffectivelyVisible, 5));
            Input.Click(window, sparkle);
            Assert.True(Ui.PumpUntil(() => shell.SourceControl.CommitMessage == "Greet the whole world", 10), shell.SourceControl.CommitMessage);
            Assert.Contains("+hello world", world.Engine.Requests.Last().Messages.Last().Content, StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task FixOnAProblemsHoverAsksForItsLinesAndOpensTheReview()
    {
        Assert.SkipUnless(File.Exists("/usr/bin/clangd"), "clangd is not installed");
        using var profile = new TempProfile();
        var world = new ChatWorld();
        world.Runtime.Set(Model);
        world.Engine.Script = (r, ct) => FakeEngine.Reply("    return 0;", 1, TimeSpan.FromMilliseconds(5), ct);
        await Ui.Run(() =>
        {
            var (window, shell, tab) = Open(world, profile, "main.c", "int main(void) {\n    return undefined_name;\n}\n");
            var editor = tab.Editor!;
            Assert.True(Ui.PumpUntil(() => editor.Decorations[App.Language.LanguageFeatures.DiagnosticsLayer].Ranges.Any(), 30), "no problem from clangd");
            var bad = tab.Document.Text.ToString().IndexOf("undefined_name", StringComparison.Ordinal) + 2;
            window.MouseMove(editor.TranslatePoint(editor.RectOf(bad).Center, window)!.Value);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "FixWithAssistant" && b.IsEffectivelyVisible), 10), "no Fix button");
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "FixWithAssistant" && b.IsEffectivelyVisible));
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.CustomView is EditReviewView, 10), "no review");
            var review = (EditReviewView)shell.Workbench.ActiveTab!.CustomView!;
            Assert.Equal("    return undefined_name;\n", review.Proposal.Original);
            Assert.Equal("    return 0;\n", review.Proposal.Proposed);
            Assert.Contains("Fix this problem:", world.Engine.Requests.Last().Messages.Last().Content, StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }
}
