using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitStagingTests
{
    private static readonly string Original = string.Join('\n', Enumerable.Range(1, 60).Select(i => $"line {i}")) + "\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Lines 3, 30 and 58 are changed, 45 is removed and one is added after 15: five hunks, far enough apart not to merge.</summary>
    private static string Edited()
    {
        var lines = Original.TrimEnd('\n').Split('\n').ToList();
        lines[2] = "line 3 changed";
        lines[29] = "line 30 changed";
        lines[57] = "line 58 changed";
        lines.RemoveAt(44);
        lines.Insert(15, "inserted after 15");
        return string.Join('\n', lines) + "\n";
    }

    private static async Task<(GitSandbox, GitRepository)> WithFileAsync()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        await box.CommitAsync("base", ("f.txt", Original));
        box.Write("f.txt", Edited());
        return (box, repo);
    }

    [Fact]
    public async Task StagingAWholeFileThenUnstagingItRestoresTheIndex()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        Assert.True((await repo.StageAsync(["f.txt"], Ct)).Ok);
        Assert.Equal(Edited(), await box.GitAsync("show", ":f.txt"));
        Assert.True((await repo.UnstageAsync(["f.txt"], Ct)).Ok);
        Assert.Equal(Original, await box.GitAsync("show", ":f.txt"));
        Assert.Equal(Edited(), box.Read("f.txt"));
    }

    [Fact]
    public async Task StagingOneHunkLeavesTheOthersUnstaged()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        var patch = await repo.PatchAsync("f.txt", staged: false, Ct);
        Assert.True(patch.Hunks.Length >= 4);

        // The hunk that changes line 30.
        var index = patch.Hunks.ToList().FindIndex(h => h.Lines.Any(l => l.Text == "line 30 changed"));
        var result = await repo.ApplySelectionAsync("f.txt", PatchAction.Stage, PatchSelection.OfHunks(index), Ct);
        Assert.True(result.Ok, result.Error);

        var staged = (await box.GitAsync("show", ":f.txt")).Split('\n');
        Assert.Contains("line 30 changed", staged);
        Assert.DoesNotContain("line 3 changed", staged);
        Assert.Contains("line 3", staged);
        Assert.DoesNotContain("inserted after 15", staged);
        Assert.Equal(Edited(), box.Read("f.txt"));

        // Unstaging that same hunk goes back to the original.
        var unstage = await repo.ApplySelectionAsync("f.txt", PatchAction.Unstage, PatchSelection.OfHunks(0), Ct);
        Assert.True(unstage.Ok, unstage.Error);
        Assert.Equal(Original, await box.GitAsync("show", ":f.txt"));
    }

    [Fact]
    public async Task EveryHunkStagedOneByOneEqualsStagingTheFile()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        var count = (await repo.PatchAsync("f.txt", false, Ct)).Hunks.Length;
        // The patch is read again each time, so after each stage the first hunk left is hunk 0.
        for (var i = 0; i < count; i++)
        {
            var result = await repo.ApplySelectionAsync("f.txt", PatchAction.Stage, PatchSelection.OfHunks(0), Ct);
            Assert.True(result.Ok, result.Error);
        }

        Assert.Equal(Edited(), await box.GitAsync("show", ":f.txt"));
        Assert.True((await repo.PatchAsync("f.txt", false, Ct)).IsEmpty);
    }

    [Fact]
    public async Task StagingSingleLinesTakesOnlyThoseLines()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        await repo.StageAsync(["f.txt"], Ct);
        await repo.UnstageAsync(["f.txt"], Ct);

        // Within the hunk that holds the insertion after line 10, take just the added line.
        var patch = await repo.PatchAsync("f.txt", false, Ct);
        var hunk = patch.Hunks.ToList().FindIndex(h => h.Lines.Any(l => l.Text == "inserted after 15"));
        var added = patch.Hunks[hunk].Lines.ToList().FindIndex(l => l.Kind == '+' && l.Text == "inserted after 15");
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Stage, PatchSelection.OfLines(hunk, [added]), Ct);
        Assert.True(result.Ok, result.Error);

        var expected = Original.TrimEnd('\n').Split('\n').ToList();
        expected.Insert(15, "inserted after 15");
        Assert.Equal(string.Join('\n', expected) + "\n", await box.GitAsync("show", ":f.txt"));
    }

    [Fact]
    public async Task StagingJustTheRemovedHalfOfAReplacementAndUnstagingItBack()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        var patch = await repo.PatchAsync("f.txt", false, Ct);
        var hunk = patch.Hunks.ToList().FindIndex(h => h.Lines.Any(l => l.Text == "line 3 changed"));
        var lines = patch.Hunks[hunk].Lines.ToList();
        var removed = lines.FindIndex(l => l.Kind == '-' && l.Text == "line 3");
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Stage, PatchSelection.OfLines(hunk, [removed]), Ct);
        Assert.True(result.Ok, result.Error);

        var staged = (await box.GitAsync("show", ":f.txt")).Split('\n');
        Assert.DoesNotContain("line 3", staged);
        Assert.DoesNotContain("line 3 changed", staged);
        Assert.Equal(Edited(), box.Read("f.txt"));

        // Now unstage that line: the index returns to the original.
        var staged1 = await repo.PatchAsync("f.txt", staged: true, Ct);
        var back = await repo.ApplySelectionAsync(staged1, PatchAction.Unstage, PatchSelection.OfHunks(0), Ct);
        Assert.True(back.Ok, back.Error);
        Assert.Equal(Original, await box.GitAsync("show", ":f.txt"));
    }

    [Fact]
    public async Task StagingJustTheAddedHalfOfAReplacementKeepsTheOldLineInTheIndex()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        var patch = await repo.PatchAsync("f.txt", false, Ct);
        var hunk = patch.Hunks.ToList().FindIndex(h => h.Lines.Any(l => l.Text == "line 3 changed"));
        var added = patch.Hunks[hunk].Lines.ToList().FindIndex(l => l.Kind == '+' && l.Text == "line 3 changed");
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Stage, PatchSelection.OfLines(hunk, [added]), Ct);
        Assert.True(result.Ok, result.Error);

        var staged = (await box.GitAsync("show", ":f.txt")).Split('\n');
        Assert.Contains("line 3", staged);
        Assert.Contains("line 3 changed", staged);
        Assert.Equal(Edited(), box.Read("f.txt"));
    }

    [Fact]
    public async Task UnstagingJustTheRemovedHalfOfAReplacementPutsTheOldLineBack()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        await repo.StageAsync(["f.txt"], Ct);
        var patch = await repo.PatchAsync("f.txt", staged: true, Ct);
        var hunk = patch.Hunks.ToList().FindIndex(h => h.Lines.Any(l => l.Text == "line 3 changed"));
        var removed = patch.Hunks[hunk].Lines.ToList().FindIndex(l => l.Kind == '-' && l.Text == "line 3");
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Unstage, PatchSelection.OfLines(hunk, [removed]), Ct);
        Assert.True(result.Ok, result.Error);

        var staged = (await box.GitAsync("show", ":f.txt")).Split('\n');
        Assert.Contains("line 3", staged);
        Assert.Contains("line 3 changed", staged);
    }

    [Fact]
    public async Task UnstagingOneLineOfAStagedFileKeepsTheRest()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        await repo.StageAsync(["f.txt"], Ct);
        var patch = await repo.PatchAsync("f.txt", staged: true, Ct);
        var hunk = patch.Hunks.ToList().FindIndex(h => h.Lines.Any(l => l.Text == "line 58 changed"));
        var both = patch.Hunks[hunk].Lines.Select((l, i) => (l, i)).Where(t => t.l.Kind != ' ' && t.l.Text.StartsWith("line 58", StringComparison.Ordinal)).Select(t => t.i);
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Unstage, PatchSelection.OfLines(hunk, both), Ct);
        Assert.True(result.Ok, result.Error);

        var expected = Edited().Replace("line 58 changed", "line 58", StringComparison.Ordinal);
        Assert.Equal(expected, await box.GitAsync("show", ":f.txt"));
        Assert.Equal(Edited(), box.Read("f.txt"));
    }

    [Fact]
    public async Task DiscardingAHunkRevertsOnlyThatHunkInTheWorkTree()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        var patch = await repo.PatchAsync("f.txt", false, Ct);
        var hunk = patch.Hunks.ToList().FindIndex(h => h.Lines.Any(l => l.Text == "line 30 changed"));
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Discard, PatchSelection.OfHunks(hunk), Ct);
        Assert.True(result.Ok, result.Error);
        Assert.Equal(Edited().Replace("line 30 changed", "line 30", StringComparison.Ordinal), box.Read("f.txt"));
        // The index was not touched.
        Assert.Equal(Original, await box.GitAsync("show", ":f.txt"));
    }

    [Fact]
    public async Task AFileWithoutATrailingNewlineStagesByLine()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("n.txt", "a\nb\nc"));
        box.Write("n.txt", "a\nB\nc\nd");
        var patch = await repo.PatchAsync("n.txt", false, Ct);
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Stage, PatchSelection.OfHunks(0), Ct);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("a\nB\nc\nd", await box.GitAsync("show", ":n.txt"));
    }

    [Fact]
    public async Task DiscardAndCleanRestoreTrackedAndRemoveUntrackedFiles()
    {
        var (box, repo) = await WithFileAsync();
        using var _ = box;
        box.Write("new/untracked.txt", "u\n");
        Assert.True((await repo.DiscardAsync(["f.txt"], Ct)).Ok);
        Assert.Equal(Original, box.Read("f.txt"));
        Assert.True((await repo.CleanAsync(["new/untracked.txt"], Ct)).Ok);
        Assert.False(File.Exists(box.PathOf("new/untracked.txt")));
    }

    [Fact]
    public async Task UnstagingBeforeTheFirstCommitWorksAndPathsWithSpecialCharactersAreLiteral()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        box.Write("star*.txt", "s\n");
        box.Write("starry.txt", "t\n");
        Assert.True((await repo.StageAsync(["star*.txt"], Ct)).Ok);
        var status = await repo.StatusAsync(Ct);
        Assert.Equal(["star*.txt"], status.Staged.Select(c => c.Path));
        Assert.True((await repo.UnstageAsync(["star*.txt"], Ct)).Ok);
        Assert.Empty((await repo.StatusAsync(Ct)).Staged);
    }
}

public sealed class GitRangeStagingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(GitSandbox, GitRepository)> SetUpAsync()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        await box.CommitAsync("base", ("f.txt", string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")) + "\n"));
        // Working text: line 3 edited, line 10 removed, "new" added after 20 (so it is working line 20, 0-based 19+... see asserts), line 35 edited.
        var lines = Enumerable.Range(1, 40).Select(i => $"line {i}").ToList();
        lines[34] = "line 35 edited";
        lines.Insert(20, "new after 20");
        lines.RemoveAt(9);
        lines[2] = "line 3 edited";
        box.Write("f.txt", string.Join('\n', lines) + "\n");
        return (box, repo);
    }

    [Fact]
    public async Task AllChangedLinesInARangeAreSelectedAndNothingOutsideIt()
    {
        var (box, repo) = await SetUpAsync();
        using var _ = box;
        var patch = await repo.PatchAsync("f.txt", false, Ct);
        var working = box.Read("f.txt").Split('\n');
        var edited3 = Array.IndexOf(working, "line 3 edited");
        var added = Array.IndexOf(working, "new after 20");

        // Just the edited line 3 (its removed original is above it and goes with it).
        Assert.NotNull(PatchSelection.ForNewLines(patch, edited3, edited3));
        Assert.True((await repo.ApplySelectionAsync(patch, PatchAction.Stage, PatchSelection.ForNewLines(patch, edited3, edited3)!, Ct)).Ok);
        var index = (await box.GitAsync("show", ":f.txt")).Split('\n');
        Assert.Contains("line 3 edited", index);
        Assert.DoesNotContain("line 3", index);
        Assert.Contains("line 10", index); // the removal was not in the range
        Assert.DoesNotContain("new after 20", index);
        Assert.DoesNotContain("line 35 edited", index);

        // The addition alone.
        var patch2 = await repo.PatchAsync("f.txt", false, Ct);
        Assert.True((await repo.ApplySelectionAsync(patch2, PatchAction.Stage, PatchSelection.ForNewLines(patch2, added, added)!, Ct)).Ok);
        Assert.Contains("new after 20", (await box.GitAsync("show", ":f.txt")).Split('\n'));

        // A range with no change selects nothing.
        var patch3 = await repo.PatchAsync("f.txt", false, Ct);
        Assert.Null(PatchSelection.ForNewLines(patch3, 0, 0));
    }

    [Fact]
    public async Task ARemovedLineIsSelectedByTheLineBelowItAndAHunkByAnyLineOfIt()
    {
        var (box, repo) = await SetUpAsync();
        using var _ = box;
        var patch = await repo.PatchAsync("f.txt", false, Ct);
        var working = box.Read("f.txt").Split('\n');
        var below = Array.IndexOf(working, "line 11"); // "line 10" was removed just above it
        var selection = PatchSelection.ForNewLines(patch, below, below);
        Assert.NotNull(selection);
        Assert.True((await repo.ApplySelectionAsync(patch, PatchAction.Stage, selection!, Ct)).Ok);
        Assert.DoesNotContain("line 10", (await box.GitAsync("show", ":f.txt")).Split('\n'));

        // The hunk at line 35 as a whole.
        var patch2 = await repo.PatchAsync("f.txt", false, Ct);
        var edited35 = Array.IndexOf(working, "line 35 edited");
        var hunk = PatchSelection.ForHunkAt(patch2, edited35);
        Assert.NotNull(hunk);
        Assert.True((await repo.ApplySelectionAsync(patch2, PatchAction.Stage, hunk!, Ct)).Ok);
        Assert.Contains("line 35 edited", (await box.GitAsync("show", ":f.txt")).Split('\n'));
        Assert.Null(PatchSelection.ForHunkAt(patch2, 0));
    }

    [Fact]
    public async Task RevertingARangeChangesTheWorkTreeAndLeavesTheIndex()
    {
        var (box, repo) = await SetUpAsync();
        using var _ = box;
        var patch = await repo.PatchAsync("f.txt", false, Ct);
        var working = box.Read("f.txt").Split('\n');
        var edited35 = Array.IndexOf(working, "line 35 edited");
        var result = await repo.ApplySelectionAsync(patch, PatchAction.Discard, PatchSelection.ForNewLines(patch, edited35, edited35)!, Ct);
        Assert.True(result.Ok, result.Error);
        var after = box.Read("f.txt").Split('\n');
        Assert.Contains("line 35", after);
        Assert.DoesNotContain("line 35 edited", after);
        Assert.Contains("line 3 edited", after); // the other changes stay
        Assert.Contains("line 35", (await box.GitAsync("show", ":f.txt")).Split('\n'));
    }
}
