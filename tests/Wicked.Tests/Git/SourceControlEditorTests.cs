using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.Core.Git;
using Wicked.Editor.Model;

namespace Wicked.Tests.Git;

/// <summary>What the panel gives the editor: blame for the status bar, the file's history, gutter trackers, line and hunk staging, and the commit dropdown's actions.</summary>
public sealed class SourceControlEditorTests
{
    private static (MainWindow Window, ShellViewModel Shell) Open(TempProfile profile, GitSandbox box)
    {
        var (window, shell) = Ui.Start(profile.Paths);
        shell.SourceControl.Environment = box.Env;
        var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted, 10), "the folder did not open");
        Input.Click(window, window.FindControl<Button>("RailSourceControl")!);
        Input.Click(window, window.FindControl<Button>("RailSourceControl")!);
        Assert.True(Ui.PumpUntil(() => shell.SourceControl.Status.Branch.Head is not null, 10), "the repository did not load");
        return (window, shell);
    }

    private static void Caret(Wicked.App.Workbench.EditorTab tab, int line) =>
        tab.Document.SetSelections([Selection.Caret(tab.Document.Text.LineStart(line))]);

    [Fact]
    public async Task TheBlameLineFollowsTheCaretAndNamesYouForYourOwnCommits()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await box.CommitAtAsync(now - (3 * 86400), "first by alice", "Alice", ("f.txt", "one\ntwo\n"));
        await box.CommitAtAsync(now - 3600, "mine", "Test", ("f.txt", "one\ntwo\nthree\n"));

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            var opened = shell.Workbench.OpenAsync(box.PathOf("f.txt"));
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && shell.Workbench.ActiveTab is not null, 10));
            var tab = shell.Workbench.ActiveTab!;
            Caret(tab, 0);
            Assert.True(Ui.PumpUntil(() => scm.BlameStatus == "Alice, 3 days ago • first by alice", 10), $"blame says '{scm.BlameStatus}'");
            Caret(tab, 2);
            Assert.True(Ui.PumpUntil(() => scm.BlameStatus == "You, 1 hour ago • mine", 5), $"blame says '{scm.BlameStatus}'");

            // Typing a new line makes the blame say so once the pause is over.
            tab.Document.Apply([new TextChange(tab.Document.Text.Length, 0, "four\n")], [Selection.Caret(tab.Document.Text.Length + 5)]);
            Caret(tab, 3);
            Assert.True(Ui.PumpUntil(() => scm.BlameStatus == "You, uncommitted changes", 10), $"blame says '{scm.BlameStatus}'");

            scm.ShowBlame = false;
            Assert.Equal("", scm.BlameStatus);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheFileHistorySectionListsTheActiveFilesCommitsAndAClickOpensTheChange()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAtAsync(1_700_000_001, "create f", "Test", ("f.txt", "a\n"));
        await box.CommitAtAsync(1_700_000_002, "touch other", "Test", ("other.txt", "o\n"));
        await box.CommitAtAsync(1_700_000_003, "edit f", "Test", ("f.txt", "a\nb\n"));

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            scm.HistoryExpanded = true;
            var opened = shell.Workbench.OpenAsync(box.PathOf("f.txt"));
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && scm.HistoryRows.Length == 2, 10), "the history did not load");
            Assert.Equal(["edit f", "create f"], scm.HistoryRows.Select(r => r.Subject));
            Assert.Equal("f.txt", scm.HistoryFile);

            var done = scm.OpenHistoryEntryAsync(scm.HistoryRows[0]);
            Assert.True(Ui.PumpUntil(() => done.IsCompleted && shell.Workbench.ActiveTab?.Title.StartsWith("f.txt (", StringComparison.Ordinal) == true, 10));
            Assert.Contains("+b", shell.Workbench.ActiveTab!.Document.Text.ToString(), StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ATrackerForAnOpenFileMarksTheGutterAndFollowsTheIndex()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("f.txt", "a\nb\nc\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            using var tracker = scm.CreateTracker(box.PathOf("f.txt"))!;
            Assert.NotNull(tracker);
            Assert.Null(scm.CreateTracker(Path.Combine(profile.Root, "elsewhere.txt")));
            var raised = new TaskCompletionSource<System.Collections.Immutable.ImmutableArray<LineChange>>();
            tracker.MarksChanged += m => raised.TrySetResult(m);
            _ = tracker.RefreshBaseAsync(TestContext.Current.CancellationToken);
            tracker.TextChanged(() => "a\nB\nc\nd\n");
            Assert.True(Ui.PumpUntil(() => raised.Task.IsCompleted, 10));
            Assert.Equal([new LineChange(1, LineChangeKind.Modified), new LineChange(3, LineChangeKind.Added)], raised.Task.Result);

            // The repository telling the editor to refresh its trackers after the index moved.
            var changed = 0;
            scm.RepositoryChanged += _ => changed++;
            File.WriteAllText(box.PathOf("f.txt"), "a\nB\nc\nd\n");
            Assert.True(Ui.PumpUntil(() => changed > 0, 10), "RepositoryChanged did not fire for a file change");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task StageSelectedRangesFromTheCommandListStagesOnlyTheSelectedLines()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";
        await box.CommitAsync("base", ("f.txt", original));
        File.WriteAllText(box.PathOf("f.txt"), original.Replace("line 5\n", "line 5 edited\n", StringComparison.Ordinal).Replace("line 25\n", "line 25 edited\n", StringComparison.Ordinal));

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            var opened = shell.Workbench.OpenAsync(box.PathOf("f.txt"));
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && shell.Workbench.ActiveTab is not null, 10));
            var tab = shell.Workbench.ActiveTab!;
            // Select line 25 (0-based 24) the way a user would, then run the command from the palette.
            var doc = tab.Document;
            doc.SetSelections([new Selection(doc.Text.LineStart(24), doc.Text.LineStart(24) + 5)]);
            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("Stage Selected Ranges");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.Count > 0 && shell.QuickInput.Items[0].Label == "Git: Stage Selected Ranges", 5));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => scm.Status.Staged.Any(c => c.Path == "f.txt"), 10), "nothing was staged");
            window.Close();
            return 0;
        });

        var index = (await box.GitAsync("show", ":f.txt")).Split('\n');
        Assert.Contains("line 25 edited", index);
        Assert.Contains("line 5", index);
        Assert.DoesNotContain("line 5 edited", index);
    }

    [Fact]
    public async Task StageAndRevertAChangeAtTheCaretAndTheRefusalWhenTheFileHasUnsavedEdits()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        var original = string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i}")) + "\n";
        await box.CommitAsync("base", ("f.txt", original));
        File.WriteAllText(box.PathOf("f.txt"), original.Replace("line 5\n", "line 5 edited\n", StringComparison.Ordinal).Replace("line 25\n", "line 25 edited\n", StringComparison.Ordinal));

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            var opened = shell.Workbench.OpenAsync(box.PathOf("f.txt"));
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && shell.Workbench.ActiveTab is not null, 10));
            var tab = shell.Workbench.ActiveTab!;

            var staged = scm.StageHunkAtAsync(box.PathOf("f.txt"), 4);
            Assert.True(Ui.PumpUntil(() => staged.IsCompleted, 10));
            Assert.True(staged.Result);

            // Unsaved edits: refused with a notice, nothing happens.
            tab.Document.Apply([new TextChange(0, 0, "x")], [Selection.Caret(1)]);
            var refused = scm.RevertHunkAtAsync(box.PathOf("f.txt"), 24);
            Assert.True(Ui.PumpUntil(() => refused.IsCompleted, 10));
            Assert.False(refused.Result);
            Assert.True(Ui.PumpUntil(() => shell.Notice?.Text?.Contains("Save the file first", StringComparison.Ordinal) == true, 5), "no notice about the unsaved file");
            window.Close();
            return 0;
        });

        Assert.Contains("line 5 edited", (await box.GitAsync("show", ":f.txt")).Split('\n'));
        Assert.DoesNotContain("line 25 edited", (await box.GitAsync("show", ":f.txt")).Split('\n'));
    }

    [Fact]
    public async Task CommitAndPushFromTheDropdownPushesAndUndoLastCommitPutsTheMessageBack()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        var remote = Path.Combine(box.Root, "remote.git");
        await box.GitInAsync(box.Root, "init", "-q", "--bare", "-b", "main", remote);
        await box.GitAsync("remote", "add", "origin", remote);
        await box.CommitAsync("base", ("a.txt", "1\n"));
        await box.GitAsync("push", "-q", "-u", "origin", "main");
        box.Write("a.txt", "2\n");

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => !scm.IsClean, 10));
            var staged = scm.StageAllAsync();
            Assert.True(Ui.PumpUntil(() => staged.IsCompleted && scm.Status.Staged.Any(), 10));

            // The dropdown's Commit and Push.
            scm.CommitMessage = "second";
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "CommitMoreButton"));
            Ui.Pump(0.2);
            var done = scm.CommitAndPushAsync();
            Assert.True(Ui.PumpUntil(() => done.IsCompleted && scm.IsClean && scm.Status.Branch.Ahead == 0, 15), "commit and push did not finish");
            Assert.Equal("", scm.ErrorText);

            // Undo Last Commit: the changes come back staged and the message returns to the box.
            var undo = scm.UndoLastCommitAsync();
            Assert.True(Ui.PumpUntil(() => undo.IsCompleted && scm.Status.Staged.Any(), 10), "the commit was not undone");
            Assert.Equal("second", scm.CommitMessage);

            // Amend loads the last commit's message when the box is empty.
            scm.CommitMessage = "";
            scm.Amend = true;
            Assert.True(Ui.PumpUntil(() => scm.CommitMessage == "base", 10), "Amend did not load the last message");
            window.Close();
            return 0;
        });

        // The remote has what was pushed: its main is the commit that was made before the undo.
        Assert.Equal("second", (await box.GitInAsync(remote, "log", "-1", "--format=%s", "main")).Trim());
    }
}
