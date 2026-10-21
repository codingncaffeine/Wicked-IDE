using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Git;
using Wicked.App.ViewModels;
using Wicked.Core.Git;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Tests.Git;

/// <summary>Source control where the user edits: change marks in the gutter, diffs as tabs, git's log in Output.</summary>
public sealed class GitEditorIntegrationTests
{
    [Fact]
    public void TheDiffSidesLineUpWithFillersAndKeepEachSidesLineNumbers()
    {
        string[] before = ["one", "two", "three", "four"];
        string[] after = ["one", "two!", "three", "inserted", "four", "five"];
        var diff = SideBySideDiff.Build(before, after, ct: TestContext.Current.CancellationToken);
        var sides = DiffEditorView.Lay(diff, before, after);
        var left = sides.Left.Split('\n');
        var right = sides.Right.Split('\n');
        Assert.Equal(left.Length, right.Length);                                          // a row for every line of either side
        Assert.Equal(diff.Rows.Length, left.Length);
        var inserted = Array.IndexOf(right, "inserted");
        Assert.Equal("", left[inserted]);                                                  // a filler opposite an added line
        Assert.Null(sides.LeftNumbers[inserted]);
        Assert.Equal(4, sides.RightNumbers[inserted]);
        Assert.Equal(Array.IndexOf(left, "four"), Array.IndexOf(right, "four"));          // the same line on the same row
        Assert.Equal(4, sides.LeftNumbers[Array.IndexOf(left, "four")]);
        Assert.Equal(5, sides.RightNumbers[Array.IndexOf(right, "four")]);

        // The changed line: red on the left, green on the right, the changed characters marked within it.
        var changed = Array.IndexOf(right, "two!");
        Assert.Equal("two", left[changed]);
        Assert.Contains(sides.LeftMarks.Lines, l => l.Style == LineStyle.Removed && l.LineStart == Offset(left, changed));
        Assert.Contains(sides.RightMarks.Lines, l => l.Style == LineStyle.Inserted && l.LineStart == Offset(right, changed));
        Assert.Contains(sides.RightMarks.Ranges, r => r.Style == RangeStyle.Inserted && r.Start >= Offset(right, changed) && r.End <= Offset(right, changed) + 4);
        Assert.Contains(sides.RightMarks.Lines, l => l.LineStart == Offset(right, Array.IndexOf(right, "five")));

        static int Offset(string[] lines, int line) => lines.Take(line).Sum(l => l.Length + 1);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TypingInATrackedFileMarksTheGutterAndADiffOpensAsATab()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var sandbox = box;
        await box.CommitAsync("first", ("notes.txt", "alpha\nbeta\ngamma\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            shell.SourceControl.Environment = box.Env;
            var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && WorkbenchWindowTests.Rows(window).Any(), 10), "the folder did not open");
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "notes.txt");
            var editor = tab.Editor!;
            bool Marked(GutterGlyph glyph, int line) => editor.Decorations[ShellViewModel.GitLayer].Gutter.Any(g => g.Glyph == glyph && tab.Document.Text.LineOf(g.LineStart) == line);

            // A new line after alpha: marked added; beta changed: marked modified.
            tab.Document.SetSelections([Selection.Caret(tab.Document.Text.LineStart(1))]);
            window.KeyTextInput("new");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => Marked(GutterGlyph.Added, 1), 10), "no added mark");
            Input.Chord(window, Key.S, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => !tab.IsDirty, 5));

            // Source Control lists it; opening its changes shows the two sides in a tab, git's commands in Output.
            Input.Click(window, window.FindControl<Button>("RailSourceControl")!);
            var panel = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => panel.Rows.OfType<ScmFileRow>().Any(r => r.Name == "notes.txt"), 15), "the change was not listed");
            var row = panel.Rows.OfType<ScmFileRow>().First(r => r.Name == "notes.txt");
            _ = panel.OpenChangesAsync(row);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.CustomView is DiffEditorView { Diff: not null, Right: not null }, 15), "no diff tab");
            var diff = (DiffEditorView)shell.Workbench.ActiveTab!.CustomView!;
            Assert.Equal("new", diff.Right!.Document.Text.Line(1));
            Assert.Equal("", diff.Left!.Document.Text.Line(1));
            Assert.True(diff.Left.Document.IsReadOnly);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<Control>().Any(c => c.Name == "DiffRight" && c.IsEffectivelyVisible), 5));
            Assert.True(Ui.PumpUntil(() =>
            {
                shell.Output.Flush();
                return shell.Output.Channels.Any(c => c.Name == ShellViewModel.GitChannel && c.Document.Text.ToString().Contains("status", StringComparison.Ordinal));
            }, 10), "git's commands are not in Output");
            window.Close();
            return 0;
        });
    }
}
