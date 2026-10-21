using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Git;
using Wicked.App.ViewModels;
using Wicked.App.Views;

namespace Wicked.Tests.Git;

/// <summary>More of the panel through the window: the branch picker, the graph, a conflict, the stash, the output and several repositories.</summary>
public sealed class SourceControlPanelTests
{
    private static (MainWindow Window, ShellViewModel Shell) Open(TempProfile profile, GitSandbox box)
    {
        var (window, shell) = Ui.Start(profile.Paths);
        shell.SourceControl.Environment = box.Env;
        var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted, 10), "the folder did not open");
        Input.Click(window, window.FindControl<Button>("RailSourceControl")!);
        Assert.True(Ui.PumpUntil(() => shell.SourceControl.Status.Branch.Head is not null, 10), "the repository did not load");
        return (window, shell);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Enter(Window window) => window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

    [Fact]
    public async Task TheBranchPickerSwitchesAndCreatesBranches()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        await box.GitAsync("branch", "dev");
        await box.GitAsync("branch", "release/1.0");

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.Equal("main", Find<TextBlock>(window, "BranchText").Text);

            Input.Click(window, Find<Button>(window, "BranchButton"));
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Count >= 5, 10), "the picker did not open with the branches");
            Assert.Equal("Create new branch...", shell.QuickInput.Items[0].Label);
            Assert.Contains(shell.QuickInput.Items, i => i.Label == "release/1.0");

            // Searchable: typing narrows the list.
            window.KeyTextInput("rel");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.Count == 1 && shell.QuickInput.Items[0].Label == "release/1.0", 5));
            Enter(window);
            Assert.True(Ui.PumpUntil(() => scm.Status.Branch.Head == "release/1.0", 10), "Enter did not switch branches");
            Assert.Equal("release/1.0", Find<TextBlock>(window, "BranchText").Text);

            // Create a new branch from the picker's first entry.
            Input.Click(window, Find<Button>(window, "BranchButton"));
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Count > 0, 10));
            Enter(window); // "Create new branch..." is first
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Placeholder.StartsWith("Branch name", StringComparison.Ordinal), 10), "no name prompt");
            window.KeyTextInput("topic/new");
            Enter(window);
            Assert.True(Ui.PumpUntil(() => scm.Status.Branch.Head == "topic/new", 10), "the branch was not created");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheGraphShowsTheHistoryAndAClickShowsACommitsFiles()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAtAsync(1_700_000_001, "A first", "Test", ("f.txt", "a\n"));
        await box.GitAsync("switch", "-q", "-c", "feature");
        await box.CommitAtAsync(1_700_000_003, "D feature work", "Test", ("d.txt", "d\ndd\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.CommitAtAsync(1_700_000_004, "C main work", "Test", ("c.txt", "c\n"));
        await box.MergeAtAsync(1_700_000_006, "feature", "M merge feature");

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.False(Find<ListBox>(window, "GraphList").IsEffectivelyVisible);
            Input.Click(window, Find<Button>(window, "GraphHead"));
            Assert.True(Ui.PumpUntil(() => scm.GraphRows.Length == 4, 10), "the graph did not load");
            Assert.Equal(new[] { "A first", "C main work", "D feature work", "M merge feature" }, scm.GraphRows.Select(r => r.Subject).Order());
            Assert.Equal("M merge feature", scm.GraphRows[0].Subject);
            Assert.True(Find<ListBox>(window, "GraphList").IsEffectivelyVisible);
            Assert.Equal(2, scm.GraphLanes);

            var list = Find<ListBox>(window, "GraphList");
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is GraphRowVm { Subject: "D feature work" });
            Input.Click(window, row);
            Assert.True(Ui.PumpUntil(() => scm.SelectedCommit?.Subject == "D feature work" && scm.CommitFiles.Length == 1, 10), "the commit's files did not load");
            Assert.True(Find<Border>(window, "CommitDetails").IsEffectivelyVisible);
            Assert.Equal("d.txt", scm.CommitFiles[0].Name);

            // A click on the file shows what the commit did to it (as a unified diff until the diff editor exists).
            var fileRow = Find<ListBox>(window, "CommitFileList").GetVisualDescendants().OfType<ListBoxItem>().First();
            Input.Click(window, fileRow);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Title.StartsWith("d.txt (", StringComparison.Ordinal) == true, 10), "no diff tab opened");
            var text = shell.Workbench.ActiveTab!.Document.Text.ToString();
            Assert.Contains("+d", text, StringComparison.Ordinal);
            Assert.False(shell.Workbench.ActiveTab.IsDirty);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task AMergeConflictShowsInMergeChangesWithABannerAndContinueFinishesIt()
    {
        using var profile = new TempProfile();
        var (box, repo) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("f.txt", "line\n"));
        await box.GitAsync("switch", "-q", "-c", "other");
        await box.CommitAsync("other", ("f.txt", "theirs\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.CommitAsync("main", ("f.txt", "ours\n"));
        await repo.MergeAsync("other", TestContext.Current.CancellationToken);

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => scm.HasOperation, 10));
            Assert.True(Find<Border>(window, "OperationBanner").IsEffectivelyVisible);
            Assert.Contains(scm.Rows, r => r is ScmGroupRow { Group: ScmGroup.Merge, Count: 1 });
            var file = scm.Rows.OfType<ScmFileRow>().Single();
            Assert.Equal(("!", "Both modified"), (file.Letter, file.Detail));

            // Take the incoming side for the file and continue: the merge ends.
            _ = scm.TakeSideAsync([file], ours: false);
            Assert.True(Ui.PumpUntil(() => scm.Status.Conflicts.Count() == 0 && scm.Status.Staged.Any(), 10), "the side was not taken");
            Input.Click(window, Find<Button>(window, "ContinueButton"));
            Assert.True(Ui.PumpUntil(() => !scm.HasOperation && scm.IsClean, 10), "the merge did not finish");
            window.Close();
            return 0;
        });

        Assert.Equal("theirs\n", await box.GitAsync("show", "HEAD:f.txt"));
        Assert.Equal(2, (await box.GitAsync("rev-list", "--parents", "-n1", "HEAD")).Trim().Split(' ').Length - 1);
    }

    [Fact]
    public async Task StashFromTheCommandListThenPopItBack()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            File.WriteAllText(box.PathOf("a.txt"), "edited\n");
            Assert.True(Ui.PumpUntil(() => !scm.IsClean, 10));

            // Ctrl+Shift+P, "stash", Enter: the palette's own path to the command.
            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("Git: Stash");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.Count > 0 && shell.QuickInput.Items[0].Label == "Git: Stash", 5), "the command is not in the palette");
            Enter(window);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Placeholder.StartsWith("Stash message", StringComparison.Ordinal), 10), "no message prompt");
            window.KeyTextInput("parked");
            Enter(window);
            Assert.True(Ui.PumpUntil(() => scm.Stashes.Length == 1 && scm.IsClean, 10), "the stash did not happen");
            Assert.Equal("parked", scm.Stashes[0].Message);
            Assert.Equal("1\n", File.ReadAllText(box.PathOf("a.txt")));

            scm.StashesExpanded = true;
            Ui.Pump(0.2);
            var row = Find<ListBox>(window, "StashList").GetVisualDescendants().OfType<ListBoxItem>().First();
            var centre = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(centre, RawInputModifiers.None);
            Ui.Pump(0.1);
            var pop = row.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == "Pop Stash");
            Input.Click(window, pop);
            Assert.True(Ui.PumpUntil(() => scm.Stashes.Length == 0 && !scm.IsClean, 10), "the stash was not popped");
            Assert.Equal("edited\n", File.ReadAllText(box.PathOf("a.txt")));
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheOutputLogHoldsEveryCommandAndMarksTheFailures()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => scm.Output.Lines.Any(l => l.Text.Contains("git status", StringComparison.Ordinal)), 10), "the status read is not logged");
            Assert.All(scm.Output.Lines.Where(l => l.Text.Contains("git status", StringComparison.Ordinal)), l => Assert.False(l.IsError));

            _ = scm.CheckoutAsync("no-such-branch", remote: false, detach: false);
            Assert.True(Ui.PumpUntil(() => scm.HasError, 10), "the failure was not reported");
            Assert.Contains(scm.Output.Lines, l => l.IsError && l.Text.Contains("switch no-such-branch", StringComparison.Ordinal) && l.Text.Contains("exit 128", StringComparison.Ordinal));
            Assert.Contains(scm.Output.Lines, l => l.IsError && l.Text.Contains("no-such-branch", StringComparison.Ordinal) && l.Text.StartsWith("    ", StringComparison.Ordinal));
            Assert.Equal("Git: " + scm.ErrorText.Split('\n')[0], shell.Notice?.Text);

            var before = scm.Output.Lines.Count;
            scm.Output.Clear();
            Assert.True(before > 0);
            Assert.Empty(scm.Output.Lines);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task SeveralRepositoriesInOneFolderAreListedAndEachKeepsItsOwnChanges()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("outer", ("outer.txt", "o\n"));
        var inner = Path.Combine(box.RepoPath, "libs", "inner");
        Directory.CreateDirectory(inner);
        await box.GitInAsync(inner, "init", "-q");
        await File.WriteAllTextAsync(Path.Combine(inner, "in.txt"), "i\n", TestContext.Current.CancellationToken);
        await box.GitInAsync(inner, "add", "-A");
        await box.GitInAsync(inner, "commit", "-q", "-m", "inner base");
        File.WriteAllText(box.PathOf("outer.txt"), "outer changed\n");
        await File.WriteAllTextAsync(Path.Combine(inner, "in.txt"), "inner changed\n", TestContext.Current.CancellationToken);

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => scm.Repositories.Count == 2 && scm.Repositories.All(r => r.Changes > 0), 10), "both repositories should show their changes");
            Assert.True(scm.ShowsRepositoryPicker);
            Assert.Contains(scm.Rows.OfType<ScmFileRow>(), r => r.Name == "outer.txt");
            Assert.DoesNotContain(scm.Rows.OfType<ScmFileRow>(), r => r.Name == "in.txt");

            scm.ActiveRepository = scm.Repositories.Single(r => r.Name == "inner");
            Assert.True(Ui.PumpUntil(() => scm.Rows.OfType<ScmFileRow>().Any(r => r.Name == "in.txt"), 10), "the inner repository's change did not appear");
            Assert.DoesNotContain(scm.Rows.OfType<ScmFileRow>(), r => r.Name == "outer.txt");
            window.Close();
            return 0;
        });
    }
}

public sealed class GitOutputViewTests
{
    [Fact]
    public async Task TheOutputViewListsTheLinesOfTheLog()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            shell.SourceControl.Environment = box.Env;
            var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && shell.SourceControl.Output.Lines.Count > 0, 10));

            // The bottom panel's Output tab mounts the view on the log.
            var view = new GitOutputView { DataContext = shell.SourceControl.Output };
            var host = new Window { Content = view, Width = 600, Height = 300 };
            host.Show();
            Ui.Pump(0.3);
            var list = view.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Equal(shell.SourceControl.Output.Lines.Count, list.ItemCount);
            Assert.Contains(shell.SourceControl.Output.Lines, l => l.Text.Contains("git status", StringComparison.Ordinal));
            host.Close();
            window.Close();
            return 0;
        });
    }
}

public sealed class ScmSelectionTests
{
    [Fact]
    public async Task SelectingEveryRowWithCtrlAAndPressingPlusStagesThemAllAndTheTreeToggleNestsFolders()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("src/a.txt", "a\n"), ("src/b.txt", "b\n"), ("c.txt", "c\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            shell.SourceControl.Environment = box.Env;
            var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted, 10));
            Input.Click(window, window.FindControl<Button>("RailSourceControl")!);
            var scm = shell.SourceControl;
            File.WriteAllText(box.PathOf("src/a.txt"), "a2\n");
            File.WriteAllText(box.PathOf("src/b.txt"), "b2\n");
            File.WriteAllText(box.PathOf("c.txt"), "c2\n");
            Assert.True(Ui.PumpUntil(() => scm.Rows.OfType<ScmFileRow>().Count() == 3, 10), "the three changes did not appear");

            // The tree toggle nests the two files of src/ under their folder.
            Input.Click(window, window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "TreeToggle"));
            Assert.True(Ui.PumpUntil(() => scm.Rows.OfType<ScmFolderRow>().Any(f => f.Name == "src" && f.Count == 2), 5), "the tree did not show the folder");
            Input.Click(window, window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "TreeToggle"));
            Assert.True(Ui.PumpUntil(() => scm.Rows.OfType<ScmFolderRow>().Count() == 0, 5));

            // Ctrl+click a row (selects it without opening a diff, and the list has the keyboard), then select all and stage from the keyboard.
            var list = window.GetVisualDescendants().OfType<ListBox>().First(l => l.Name == "ChangeList");
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.DataContext is ScmFileRow { Name: "c.txt" });
            var at = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseDown(at, MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(at, MouseButton.Left, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => list.IsKeyboardFocusWithin, 5), "the list did not take the keyboard");
            Input.Chord(window, Key.A, RawInputModifiers.Control);
            Assert.Equal(3, list.SelectedItems!.OfType<ScmFileRow>().Count());
            Input.Chord(window, Key.Add, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => scm.Status.Staged.Count() == 3 && !scm.Status.Unstaged.Any(), 10), "the selected rows were not all staged");
            window.Close();
            return 0;
        });
    }
}
