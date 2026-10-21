using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Assistant;
using Wicked.App.Workbench;
using Wicked.Core.Chat;
using Wicked.Core.Files;
using Wicked.Editor.Model;

namespace Wicked.Tests.Chat;

/// <summary>Proposed edits: reviewed change by change, nothing in the document until accepted.</summary>
public sealed class EditReviewTests
{
    private static EditorTab Tab(string text) => new(null, new Document(text), null, FileEncoding.Utf8, default);

    [Fact]
    public void EachChangeIsTakenOrLeftOnItsOwnAndTheStretchFollowsEditsAboveIt()
    {
        var tab = Tab("head\none\ntwo\nthree\nfour\ntail\n");
        var start = tab.Document.Text.LineStart(1);
        var end = tab.Document.Text.LineStart(5);
        var proposal = new EditProposal(tab, start, end, "ONE\ntwo\nthree\nFOUR\n");
        Assert.Equal(2, proposal.Changes.Count);
        Assert.Equal("head\none\ntwo\nthree\nfour\ntail\n", tab.Document.Text.ToString());     // nothing yet

        // An edit above the stretch while it is reviewed: the stretch moves with its text.
        tab.Document.Apply([new TextChange(0, 0, "new first line\n")], [Selection.Caret(0)]);
        Assert.Equal("one\ntwo\nthree\nfour\n", proposal.Original);

        proposal.Reject(1);                                                                    // four stays four
        Assert.Single(proposal.Changes);
        proposal.Accept(0);
        Assert.Equal("new first line\nhead\nONE\ntwo\nthree\nfour\ntail\n", tab.Document.Text.ToString());
        Assert.True(proposal.IsSettled);
        Assert.True(tab.Document.Undo());
        Assert.Equal("new first line\nhead\none\ntwo\nthree\nfour\ntail\n", tab.Document.Text.ToString()); // one undo step
    }

    [Theory]
    [InlineData("int x = 1;", "int x = 1;")]
    [InlineData("```csharp\nint x = 1;\n```", "int x = 1;")]
    [InlineData("Here you go:\n```\na();\nb();\n```\nDone.", "a();\nb();")]
    public void FencesAroundTheAnswerAreTakenOff(string reply, string code) => Assert.Equal(code, InlineEdit.StripFences(reply));

    [Fact]
    public async Task CtrlIRewritesTheSelectionThroughAReviewThatAcceptsIntoTheEditor()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.cs"), "class A\n{\n    int x = 1;\n}\n");
        var world = new ChatWorld();
        world.Runtime.Set(new RunningModel("m:Q4", "Model", RunState.Loaded, null, false, false, 8192, null));
        world.Engine.Script = (request, ct) => FakeEngine.Reply("```cs\n    long x = 1L;\n```", 2, TimeSpan.FromMilliseconds(5), ct);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var opened = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && WorkbenchWindowTests.Rows(window).Any(), 10));
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "a.cs");
            var doc = tab.Document;
            doc.SetSelections([new Selection(doc.Text.LineStart(2), doc.Text.LineStart(3))]);
            Input.Chord(window, Key.I, RawInputModifiers.Control);
            var edit = shell.InlineEditFor(tab.Editor)!;
            Assert.True(edit.IsOpen);
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "InlineEditInput");
            Assert.True(Ui.PumpUntil(() => box.IsFocused, 5));
            window.KeyTextInput("make it a long");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.CustomView is EditReviewView, 10), "no review");
            var review = (EditReviewView)shell.Workbench.ActiveTab!.CustomView!;
            Assert.Single(review.Proposal.Changes);
            Assert.Contains("make it a long", world.Engine.Requests.Last().Messages[^1].Content, StringComparison.Ordinal);
            Assert.Contains("<<<REGION>>>\n    int x = 1;\n", world.Engine.Requests.Last().Messages[^1].Content, StringComparison.Ordinal);
            Assert.Equal("class A\n{\n    int x = 1;\n}\n", doc.Text.ToString());               // not yet

            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "ReviewAccept" && b.IsEffectivelyVisible), 5));
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ReviewAccept" && b.IsEffectivelyVisible));
            Assert.True(Ui.PumpUntil(() => doc.Text.ToString() == "class A\n{\n    long x = 1L;\n}\n", 5), doc.Text.ToString());
            Assert.True(Ui.PumpUntil(() => shell.Workbench.AllTabs.All(t => t.CustomView is not EditReviewView), 5), "the review stayed open");
            window.Close();
            return 0;
        });
    }
}
