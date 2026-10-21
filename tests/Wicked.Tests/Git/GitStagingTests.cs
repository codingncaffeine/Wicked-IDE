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
