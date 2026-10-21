using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitOperationsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CommitAndAmendAndSignOff()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        box.Write("a.txt", "1\n");
        await repo.StageAsync(["a.txt"], Ct);

        var first = await repo.CommitAsync(new GitCommitOptions("First commit\n\nWith a body"), Ct);
        Assert.True(first.Ok, first.Error);
        Assert.Equal("First commit\n\nWith a body", await repo.LastCommitMessageAsync(Ct));

        box.Write("a.txt", "2\n");
        await repo.StageAsync(["a.txt"], Ct);
        var amend = await repo.CommitAsync(new GitCommitOptions("First commit, amended", Amend: true, SignOff: true), Ct);
        Assert.True(amend.Ok, amend.Error);
        Assert.Equal("1", (await box.GitAsync("rev-list", "--count", "HEAD")).Trim());
        var message = await repo.LastCommitMessageAsync(Ct);
        Assert.StartsWith("First commit, amended", message, StringComparison.Ordinal);
        Assert.Contains("Signed-off-by: Test <test@example.invalid>", message, StringComparison.Ordinal);
        Assert.Equal("2\n", await box.GitAsync("show", "HEAD:a.txt"));
    }

    [Fact]
    public async Task AnEmptyCommitAndAnEmptyMessageAreRefusedBeforeGitRuns()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        var before = box.Env.Log.Snapshot().Count;

        var nothing = await repo.CommitAsync(new GitCommitOptions("message"), Ct);
        Assert.False(nothing.Ok);
        Assert.Contains("nothing to commit", nothing.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("1", (await box.GitAsync("rev-list", "--count", "HEAD")).Trim());

        box.Write("a.txt", "2\n");
        await repo.StageAsync(["a.txt"], Ct);
        var blank = await repo.CommitAsync(new GitCommitOptions("   \n"), Ct);
        Assert.False(blank.Ok);
        Assert.Contains("message", blank.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(box.Env.Log.Snapshot().Skip(before), e => e.Command.Contains(" commit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHooksOutputComesBackAndAFailingHookStopsTheCommit()
    {
        if (OperatingSystem.IsWindows()) return; // the hook is a shell script
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        var hook = Path.Combine(box.RepoPath, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\necho \"checking the staged files\"\necho \"style problem in a.txt\" >&2\nexit 1\n", Ct);
        File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        box.Write("a.txt", "1\n");
        await repo.StageAsync(["a.txt"], Ct);
        var result = await repo.CommitAsync(new GitCommitOptions("will be refused"), Ct);
        Assert.False(result.Ok);
        Assert.Contains("checking the staged files", result.Error, StringComparison.Ordinal); // git sends a hook's stdout to stderr
        Assert.Contains("style problem in a.txt", result.Error, StringComparison.Ordinal);

        // The same text is in the output log the panel mounts.
        var entry = box.Env.Log.Snapshot().Last(e => e.Command.Contains(" commit", StringComparison.Ordinal));
        Assert.Contains("style problem", entry.Error, StringComparison.Ordinal);
        Assert.NotEqual(0, entry.ExitCode);

        // --no-verify skips it.
        var skipped = await repo.CommitAsync(new GitCommitOptions("skip the hook", NoVerify: true), Ct);
        Assert.True(skipped.Ok, skipped.Error);
    }

    [Fact]
    public async Task UndoLastCommitKeepsTheChangesStagedAndReturnsTheMessage()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("one", ("a.txt", "1\n"));
        await box.CommitAsync("two message", ("a.txt", "2\n"));
        var (result, message) = await repo.UndoLastCommitAsync(Ct);
        Assert.True(result.Ok, result.Error);
        Assert.Equal("two message", message);
        var status = await repo.StatusAsync(Ct);
        Assert.Equal(["a.txt"], status.Staged.Select(c => c.Path));
        Assert.Equal("1", (await box.GitAsync("rev-list", "--count", "HEAD")).Trim());

        // Undoing the very first commit leaves an unborn branch with the files staged.
        await repo.CommitAsync(new GitCommitOptions("redo"), Ct);
        var (first, _) = await repo.UndoLastCommitAsync(Ct);
        Assert.True(first.Ok, first.Error);
        var (second, firstMessage) = await repo.UndoLastCommitAsync(Ct);
        Assert.True(second.Ok, second.Error);
        Assert.Equal("one", firstMessage);
        Assert.True((await repo.StatusAsync(Ct)).Branch.IsUnborn);
        Assert.Equal(["a.txt"], (await repo.StatusAsync(Ct)).Staged.Select(c => c.Path));
    }

    [Fact]
    public async Task BranchesCanBeListedCreatedRenamedCheckedOutAndDeleted()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));

        Assert.True((await repo.CreateBranchAsync("feature/one", null, checkout: true, Ct)).Ok);
        Assert.True((await repo.CreateBranchAsync("side", "main", checkout: false, Ct)).Ok);
        var list = await repo.BranchesAsync(Ct);
        Assert.Equal(["feature/one", "main", "side"], list.Where(b => !b.IsRemote).Select(b => b.Name).Order());
        Assert.Equal("feature/one", list.Single(b => b.IsCurrent).Name);

        Assert.True((await repo.RenameBranchAsync("side", "side2", Ct)).Ok);
        Assert.True((await repo.CheckoutAsync("main", false, Ct)).Ok);
        Assert.Equal("main", (await repo.StatusAsync(Ct)).Branch.Head);

        // A branch with unmerged work needs force.
        await repo.CheckoutAsync("feature/one", false, Ct);
        await box.CommitAsync("on feature", ("b.txt", "b\n"));
        await repo.CheckoutAsync("main", false, Ct);
        Assert.False((await repo.DeleteBranchAsync("feature/one", false, Ct)).Ok);
        Assert.True((await repo.DeleteBranchAsync("feature/one", true, Ct)).Ok);
        Assert.DoesNotContain((await repo.BranchesAsync(Ct)), b => b.Name == "feature/one");

        // A commit detaches HEAD.
        var hash = (await box.GitAsync("rev-parse", "HEAD")).Trim();
        Assert.True((await repo.CheckoutAsync(hash, detach: true, Ct)).Ok);
        Assert.True((await repo.StatusAsync(Ct)).Branch.IsDetached);
    }

    [Fact]
    public async Task TagsCanBeCreatedListedAndDeleted()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        Assert.True((await repo.CreateTagAsync("v1", null, null, Ct)).Ok);
        Assert.True((await repo.CreateTagAsync("v2", "second release", null, Ct)).Ok);
        var tags = await repo.TagsAsync(Ct);
        Assert.False(tags.Single(t => t.Name == "v1").IsAnnotated);
        Assert.True(tags.Single(t => t.Name == "v2").IsAnnotated);
        Assert.True((await repo.DeleteTagAsync("v1", Ct)).Ok);
        Assert.DoesNotContain(await repo.TagsAsync(Ct), t => t.Name == "v1");
    }

    [Fact]
    public async Task StashPushListApplyPopAndDrop()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        box.Write("a.txt", "changed\n");
        box.Write("fresh.txt", "untracked\n");

        var push = await repo.StashPushAsync("my work in progress", includeUntracked: true, Ct);
        Assert.True(push.Ok, push.Error);
        var leftover = await repo.StatusAsync(Ct);
        Assert.True(leftover.IsClean, string.Join(", ", leftover.Changes.Select(c => c.Code + " " + c.Path)));
        var stashes = await repo.StashesAsync(Ct);
        var stash = Assert.Single(stashes);
        Assert.Equal("my work in progress", stash.Message);
        Assert.Equal("main", stash.Branch);
        Assert.Equal("stash@{0}", stash.Ref);

        Assert.True((await repo.StashApplyAsync(stash.Ref, Ct)).Ok);
        Assert.Equal("changed\n", box.Read("a.txt"));
        Assert.Single(await repo.StashesAsync(Ct));

        await repo.DiscardAsync(["a.txt"], Ct);
        await repo.CleanAsync(["fresh.txt"], Ct);
        Assert.True((await repo.StashPopAsync("stash@{0}", Ct)).Ok);
        Assert.Empty(await repo.StashesAsync(Ct));
        Assert.Equal("untracked\n", box.Read("fresh.txt"));

        await repo.StashPushAsync(null, true, Ct);
        Assert.True((await repo.StashDropAsync("stash@{0}", Ct)).Ok);
        Assert.Empty(await repo.StashesAsync(Ct));
    }

    [Fact]
    public async Task PushSetsTheUpstreamAndFetchPullAndAheadBehindFollow()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        var remote = Path.Combine(box.Root, "remote.git");
        await box.GitInAsync(box.Root, "init", "-q", "--bare", "-b", "main", remote);
        await box.GitAsync("remote", "add", "origin", remote);
        await box.CommitAsync("one", ("a.txt", "1\n"));

        var push = await repo.PushAsync(false, Ct);
        Assert.True(push.Ok, push.Error);
        var status = await repo.StatusAsync(Ct);
        Assert.Equal("origin/main", status.Branch.Upstream);
        Assert.Equal((0, 0), (status.Branch.Ahead, status.Branch.Behind));

        await box.CommitAsync("two", ("a.txt", "2\n"));
        status = await repo.StatusAsync(Ct);
        Assert.Equal((1, 0), (status.Branch.Ahead, status.Branch.Behind));
        Assert.Equal(1, (await repo.BranchesAsync(Ct)).Single(b => b.Name == "main").Ahead);
        Assert.True((await repo.PushAsync(false, Ct)).Ok);

        // Another clone pushes; we fetch to see we are behind, then pull.
        var other = Path.Combine(box.Root, "other");
        await box.GitInAsync(box.Root, "clone", "-q", remote, other);
        await File.WriteAllTextAsync(Path.Combine(other, "b.txt"), "b\n", Ct);
        await box.GitInAsync(other, "add", "-A");
        await box.GitInAsync(other, "commit", "-q", "-m", "from other");
        await box.GitInAsync(other, "push", "-q");

        Assert.True((await repo.FetchAsync(null, Ct)).Ok);
        status = await repo.StatusAsync(Ct);
        Assert.Equal((0, 1), (status.Branch.Ahead, status.Branch.Behind));
        Assert.True((await repo.PullAsync(GitPullMode.Configured, Ct)).Ok);
        Assert.True(File.Exists(box.PathOf("b.txt")));
        Assert.Equal((0, 0), ((await repo.StatusAsync(Ct)).Branch.Ahead, (await repo.StatusAsync(Ct)).Branch.Behind));
    }

    [Fact]
    public async Task APullOfDivergedHistoryFallsBackToAMergeWhenNothingIsConfigured()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        var remote = Path.Combine(box.Root, "remote.git");
        await box.GitInAsync(box.Root, "init", "-q", "--bare", "-b", "main", remote);
        await box.GitAsync("remote", "add", "origin", remote);
        await box.CommitAsync("one", ("a.txt", "1\n"));
        await repo.PushAsync(false, Ct);

        var other = Path.Combine(box.Root, "other");
        await box.GitInAsync(box.Root, "clone", "-q", remote, other);
        await File.WriteAllTextAsync(Path.Combine(other, "theirs.txt"), "t\n", Ct);
        await box.GitInAsync(other, "add", "-A");
        await box.GitInAsync(other, "commit", "-q", "-m", "theirs");
        await box.GitInAsync(other, "push", "-q");

        await box.CommitAsync("ours", ("ours.txt", "o\n"));
        var pull = await repo.PullAsync(GitPullMode.Configured, Ct);
        Assert.True(pull.Ok, pull.Error);
        Assert.True(File.Exists(box.PathOf("theirs.txt")) && File.Exists(box.PathOf("ours.txt")));
        Assert.Equal(2, (await box.GitAsync("rev-list", "--parents", "-n1", "HEAD")).Trim().Split(' ').Length - 1);

        // With rebase chosen in the user's configuration, the same pull rebases instead.
        await box.GitAsync("config", "pull.rebase", "true");
        await File.WriteAllTextAsync(Path.Combine(other, "theirs2.txt"), "t\n", Ct);
        await box.GitInAsync(other, "add", "-A");
        await box.GitInAsync(other, "commit", "-q", "-m", "theirs 2");
        await box.GitInAsync(other, "pull", "-q", "--no-rebase");
        await box.GitInAsync(other, "push", "-q");
        await box.CommitAsync("ours 2", ("ours2.txt", "o\n"));
        Assert.True((await repo.PullAsync(GitPullMode.Configured, Ct)).Ok);
        Assert.Equal(1, (await box.GitAsync("rev-list", "--parents", "-n1", "HEAD")).Trim().Split(' ').Length - 1);
    }

    [Fact]
    public async Task PushWithoutARemoteSaysSo()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("one", ("a.txt", "1\n"));
        var result = await repo.PushAsync(false, Ct);
        Assert.False(result.Ok);
        Assert.Contains("no remote", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WritesQueueSoTwoNeverRaceOnTheIndex()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        for (var i = 0; i < 12; i++) box.Write($"f{i}.txt", $"{i}\n");
        // Twelve stages started together: without the queue some would die on index.lock.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => repo.StageAsync([$"f{i}.txt"], Ct)));
        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(12, (await repo.StatusAsync(Ct)).Staged.Count());
    }

    [Fact]
    public async Task AbortingAMergeLeavesACleanTree()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("f.txt", "line\n"));
        await box.GitAsync("switch", "-q", "-c", "other");
        await box.CommitAsync("other", ("f.txt", "other side\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.CommitAsync("main", ("f.txt", "main side\n"));
        await repo.MergeAsync("other", Ct);
        Assert.Equal(GitOperation.Merge, (await repo.StatusAsync(Ct)).Operation);
        Assert.True((await repo.AbortAsync(GitOperation.Merge, Ct)).Ok);
        var status = await repo.StatusAsync(Ct);
        Assert.Equal(GitOperation.None, status.Operation);
        Assert.True(status.IsClean);
    }
}

public sealed class GitSwitchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(GitSandbox Box, GitRepository Repo)> TwoBranchesAsync()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        var lines = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}")) + "\n";
        await box.CommitAsync("base", ("f.txt", lines));
        await box.GitAsync("switch", "-q", "-c", "other");
        await box.CommitAsync("other", ("f.txt", lines.Replace("line 2\n", "line 2 on other\n", StringComparison.Ordinal)));
        await box.GitAsync("switch", "-q", "main");
        return (box, repo);
    }

    [Fact]
    public async Task ASwitchBlockedByLocalChangesCanCarryThemAcrossThroughTheStash()
    {
        var (box, repo) = await TwoBranchesAsync();
        using var _ = box;
        // Line 2 differs on "other" and is edited here too: git refuses to switch. A change far away is carried.
        box.Write("f.txt", box.Read("f.txt").Replace("line 2\n", "line 2 edited here\n", StringComparison.Ordinal));
        var plain = await repo.CheckoutAsync("other", false, Ct);
        Assert.False(plain.Ok);
        Assert.Contains("would be overwritten", plain.Error, StringComparison.Ordinal);

        // Edit line 30 instead of line 2: the switch is still refused (the file differs), and carrying works.
        await repo.DiscardAsync(["f.txt"], Ct);
        box.Write("f.txt", box.Read("f.txt").Replace("line 30\n", "line 30 edited here\n", StringComparison.Ordinal));
        var carried = await repo.SwitchCarryingChangesAsync(["switch", "other"], Ct);
        Assert.True(carried.Ok, carried.Error);
        var status = await repo.StatusAsync(Ct);
        Assert.Equal("other", status.Branch.Head);
        var text = box.Read("f.txt");
        Assert.Contains("line 2 on other", text, StringComparison.Ordinal);
        Assert.Contains("line 30 edited here", text, StringComparison.Ordinal);
        Assert.Empty(await repo.StashesAsync(Ct));
    }

    [Fact]
    public async Task WhenTheChangesConflictOnTheOtherBranchTheySurviveInTheStash()
    {
        var (box, repo) = await TwoBranchesAsync();
        using var _ = box;
        box.Write("f.txt", box.Read("f.txt").Replace("line 2\n", "line 2 edited here\n", StringComparison.Ordinal));
        var carried = await repo.SwitchCarryingChangesAsync(["switch", "other"], Ct);
        Assert.False(carried.Ok);
        Assert.Contains("stash", carried.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("other", (await repo.StatusAsync(Ct)).Branch.Head);
        var stash = Assert.Single(await repo.StashesAsync(Ct));
        Assert.Contains("line 2 edited here", await box.GitAsync("show", stash.Ref + ":f.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwitchThatFailsForAnotherReasonPutsTheChangesBack()
    {
        var (box, repo) = await TwoBranchesAsync();
        using var _ = box;
        box.Write("f.txt", box.Read("f.txt").Replace("line 30\n", "line 30 edited here\n", StringComparison.Ordinal));
        var result = await repo.SwitchCarryingChangesAsync(["switch", "no-such-branch"], Ct);
        Assert.False(result.Ok);
        Assert.Equal("main", (await repo.StatusAsync(Ct)).Branch.Head);
        Assert.Contains("line 30 edited here", box.Read("f.txt"), StringComparison.Ordinal);
        Assert.Empty(await repo.StashesAsync(Ct));
    }
}
