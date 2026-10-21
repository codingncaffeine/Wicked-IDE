using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.Workbench;
using Wicked.Editor.Model;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>Back, Forward and Go to Last Edit Location: through the window, and the history's rules on their own.</summary>
public sealed class NavigationHistoryTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task BackAndForwardRetraceJumpsAndTabsAndTheSideButtonsDoToo()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), string.Join('\n', Enumerable.Range(0, 100).Select(i => $"a line {i}")));
        File.WriteAllText(Path.Combine(root, "b.txt"), "b\n");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            var a = OpenByClicks(window, shell, "a.txt");
            int Line() => bench.ActiveTab!.Document.Text.LineOf(bench.ActiveTab.Document.Primary.Active);
            void Run(string id)
            {
                var run = shell.RunCommandAsync(id);
                Assert.True(Ui.PumpUntil(() => run.IsCompleted, 5), $"{id} did not finish");
                Ui.Pump(0.1);
            }

            // Line 5, then a jump to line 80 (more than ten lines: a place is left behind), then another file.
            a.Document.SetSelections([Selection.Caret(a.Document.Text.LineStart(5))]);
            a.Document.SetSelections([Selection.Caret(a.Document.Text.LineStart(80))]);
            var b = OpenByClicks(window, shell, "b.txt");
            Assert.Same(b, bench.ActiveTab);

            Run("workbench.action.navigateBack");
            Assert.Equal(("a.txt", 80), (bench.ActiveTab!.Title, Line()));
            Run("workbench.action.navigateBack");
            Assert.Equal(("a.txt", 5), (bench.ActiveTab!.Title, Line()));
            Run("workbench.action.navigateForward");
            Assert.Equal(("a.txt", 80), (bench.ActiveTab!.Title, Line()));
            Run("workbench.action.navigateForward");
            Assert.Equal("b.txt", bench.ActiveTab!.Title);

            // The mouse's back button (XButton1) goes back too.
            var centre = new Point(window.Bounds.Width / 2, window.Bounds.Height / 2);
            window.MouseDown(centre, MouseButton.XButton1);
            window.MouseUp(centre, MouseButton.XButton1);
            Assert.True(Ui.PumpUntil(() => bench.ActiveTab?.Title == "a.txt", 5), bench.ActiveTab?.Title);

            // An edit in a.txt (open again in a new tab: the preview of it was replaced), then b.txt: Go to Last Edit Location
            // comes back to the edit.
            var reopened = bench.ActiveTab!.Document;
            reopened.SetSelections([Selection.Caret(reopened.Text.LineStart(40))]);
            Edits.Type(reopened, "X", EditOptions.Default);
            OpenByClicks(window, shell, "b.txt");
            Run("workbench.action.navigateToLastEditLocation");
            Assert.Equal(("a.txt", 40), (bench.ActiveTab!.Title, Line()));
            Assert.Equal(reopened.Text.LineStart(40) + 1, bench.ActiveTab.Document.Primary.Active);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public void ANewJumpAfterGoingBackDropsWhatWasAhead()
    {
        var history = new NavigationHistory();
        NavigationPlace At(string file, int offset) => new(file, null, offset);
        history.Leaving(At("a", 1));
        history.Leaving(At("b", 2));
        Assert.Equal(At("b", 2), history.Back(At("c", 3)));
        Assert.Equal(At("a", 1), history.Back(At("b", 2)));
        Assert.False(history.CanGoBack);
        Assert.True(history.CanGoForward);

        history.Leaving(At("a", 9)); // a new jump from here
        Assert.False(history.CanGoForward);
        Assert.Equal(At("a", 9), history.Back(At("d", 4)));

        // The same place twice in a row is one entry.
        var again = new NavigationHistory();
        again.Leaving(At("a", 1));
        again.Leaving(At("a", 1));
        Assert.Equal(At("a", 1), again.Back(At("z", 0)));
        Assert.False(again.CanGoBack);
    }
}

/// <summary>A jump (open at a line, go to definition) as VS Code reveals it: no scroll when the place is in sight, centred when not.</summary>
public sealed class JumpRevealTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task AJumpLeavesAPlaceInSightAloneAndCentresOneOutOfSight()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "short.txt"), string.Join('\n', Enumerable.Range(0, 10).Select(i => $"line {i}")));
        File.WriteAllText(Path.Combine(root, "long.txt"), string.Join('\n', Enumerable.Range(0, 600).Select(i => $"line {i}")));
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);

            // A short file opened at line 6 (its editor not laid out yet): everything fits, so nothing scrolls.
            var open = shell.OpenAtAsync(Path.Combine(root, "short.txt"), 5, 0);
            Assert.True(Ui.PumpUntil(() => open.IsCompleted && shell.Workbench.ActiveTab?.Editor?.View.Height > 0, 5));
            Ui.Pump(0.2);
            Assert.Equal(0, shell.Workbench.ActiveTab!.Editor!.View.ScrollY);

            // A long file opened at line 300: the line is in the middle of the view, not at its top or bottom edge.
            open = shell.OpenAtAsync(Path.Combine(root, "long.txt"), 299, 0);
            Assert.True(Ui.PumpUntil(() => open.IsCompleted && shell.Workbench.ActiveTab?.Editor?.View.Height > 0, 5));
            Ui.Pump(0.2);
            var view = shell.Workbench.ActiveTab!.Editor!.View;
            var y = 299 * view.LineHeight - view.ScrollY;
            Assert.InRange(y, view.Height / 2 - 2 * view.LineHeight, view.Height / 2 + view.LineHeight);
            window.Close();
            return 0;
        });
    }
}
