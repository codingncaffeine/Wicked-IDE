using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitStatusTests
{
    [Fact]
    public void ParsesEveryKindOfEntryFromPorcelainV2()
    {
        // Records are NUL-terminated; a rename carries its original path as the next record.
        var output = string.Join('\0',
            "# branch.oid 1111111111111111111111111111111111111111",
            "# branch.head feature/x y",
            "# branch.upstream origin/feature/x y",
            "# branch.ab +3 -2",
            "1 .M N... 100644 100644 100644 aaa bbb src/with space/ünï.cs",
            "1 A. N... 000000 100644 100644 000 bbb new.txt",
            "1 MM N... 100644 100644 100644 aaa bbb both.txt",
            "2 R. N... 100644 100644 100644 aaa bbb R100 moved name.txt",
            "old name.txt",
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc clash.txt",
            "u DU N... 100644 000000 100644 100644 aaa 000 ccc gone.txt",
            "? untracked file.txt",
            "? dir/日本語.txt",
            "");
        var status = GitStatusParser.Parse(output);

        Assert.Equal("feature/x y", status.Branch.Head);
        Assert.Equal("origin/feature/x y", status.Branch.Upstream);
        Assert.Equal((3, 2), (status.Branch.Ahead, status.Branch.Behind));
        Assert.False(status.Branch.IsDetached);

        GitChange Get(string path) => status.Changes.Single(c => c.Path == path);
        Assert.Equal((GitChangeKind.None, GitChangeKind.Modified), (Get("src/with space/ünï.cs").Index, Get("src/with space/ünï.cs").WorkTree));
        Assert.Equal(GitChangeKind.Added, Get("new.txt").Index);
        Assert.True(Get("both.txt").IsStaged && Get("both.txt").IsUnstaged);
        var renamed = Get("moved name.txt");
        Assert.Equal("old name.txt", renamed.OriginalPath);
        Assert.Equal(GitChangeKind.Renamed, renamed.Index);
        Assert.True(Get("clash.txt").IsConflicted);
        Assert.Equal("Both modified", Get("clash.txt").ConflictText);
        Assert.Equal("Deleted by us", Get("gone.txt").ConflictText);
        Assert.True(Get("untracked file.txt").IsUntracked);
        Assert.True(Get("dir/日本語.txt").IsUntracked);
        Assert.Equal(["clash.txt", "gone.txt"], status.Conflicts.Select(c => c.Path));
        Assert.Equal(["moved name.txt", "new.txt", "both.txt"], status.Staged.Select(c => c.Path).OrderBy(p => p == "both.txt" ? 2 : p == "new.txt" ? 1 : 0));
        Assert.Equal(2, status.Untracked.Count());
    }

    [Fact]
    public void ADetachedHeadAndAFreshRepositoryAreRecognised()
    {
        var detached = GitStatusParser.Parse("# branch.oid abc\0# branch.head (detached)\0");
        Assert.True(detached.Branch.IsDetached);
        var fresh = GitStatusParser.Parse("# branch.oid (initial)\0# branch.head main\0");
        Assert.True(fresh.Branch.IsUnborn);
        Assert.Equal("main", fresh.Branch.Head);
    }

    [Fact]
    public async Task ARealRepositoryReportsStagedUnstagedUntrackedAndRenames()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("first", ("a.txt", "one\n"), ("rename me.txt", "same content\nsecond line\nthird line\n"), ("sp ace/ünï.txt", "x\n"));

        box.Write("a.txt", "one\ntwo\n");
        await box.GitAsync("mv", "rename me.txt", "renamed ✓.txt");
        box.Write("sp ace/ünï.txt", "y\n");
        await box.GitAsync("add", "sp ace/ünï.txt");
        box.Write("sp ace/ünï.txt", "z\n");
        box.Write("brand new.txt", "n\n");

        var status = await repo.StatusAsync(TestContext.Current.CancellationToken);
        GitChange Get(string path) => status.Changes.Single(c => c.Path == path);

        Assert.Equal("main", status.Branch.Head);
        Assert.Equal(GitChangeKind.Modified, Get("a.txt").WorkTree);
        Assert.False(Get("a.txt").IsStaged);
        var renamed = Get("renamed ✓.txt");
        Assert.Equal(GitChangeKind.Renamed, renamed.Index);
        Assert.Equal("rename me.txt", renamed.OriginalPath);
        var both = Get("sp ace/ünï.txt");
        Assert.True(both.IsStaged && both.IsUnstaged);
        Assert.True(Get("brand new.txt").IsUntracked);
    }

    [Fact]
    public async Task AMergeConflictShowsAsConflictedAndTheOperationIsKnown()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("f.txt", "line\n"));
        await box.GitAsync("switch", "-q", "-c", "other");
        await box.CommitAsync("other", ("f.txt", "other side\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.CommitAsync("main", ("f.txt", "main side\n"));

        var merge = await repo.MergeAsync("other", TestContext.Current.CancellationToken);
        Assert.False(merge.Ok);

        var status = await repo.StatusAsync(TestContext.Current.CancellationToken);
        Assert.Equal(GitOperation.Merge, status.Operation);
        var conflict = Assert.Single(status.Conflicts);
        Assert.Equal("f.txt", conflict.Path);
        Assert.Equal("Both modified", conflict.ConflictText);
        Assert.Empty(status.Staged);
    }

    [Fact]
    public async Task AnUnbornBranchAndNoRepositoryAreHandled()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        var status = await repo.StatusAsync(TestContext.Current.CancellationToken);
        Assert.True(status.Branch.IsUnborn);
        Assert.Equal("main", status.Branch.Head);

        using var empty = GitSandbox.CreateEmpty();
        Assert.Null(await GitRepository.OpenAsync(empty.RepoPath, empty.Env, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NestedRepositoriesAndLinkedWorktreesAreFound()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("root", ("a.txt", "a\n"));
        var nested = Path.Combine(box.RepoPath, "libs", "inner");
        Directory.CreateDirectory(nested);
        await box.GitInAsync(nested, "init", "-q");
        var linked = Path.Combine(box.Root, "linked");
        await box.GitAsync("worktree", "add", "-q", "-b", "wt", linked);

        var found = await GitDiscovery.FindAsync(box.RepoPath, box.Env, TestContext.Current.CancellationToken);
        Assert.Equal([repo.Root, nested], found.Select(r => r.Root));

        // A folder inside a repository belongs to the repository above it.
        var deep = await GitDiscovery.FindAsync(Path.Combine(box.RepoPath, "libs"), box.Env, TestContext.Current.CancellationToken);
        Assert.Equal(repo.Root, deep[0].Root);

        var worktrees = await repo.WorktreesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, worktrees.Length);
        Assert.Contains(worktrees, w => w.Branch == "wt" && w.Path == linked);
        var linkedRepo = await GitRepository.OpenAsync(linked, box.Env, TestContext.Current.CancellationToken);
        Assert.NotNull(linkedRepo);
        Assert.NotEqual(linkedRepo.GitDir, linkedRepo.CommonDir);
    }
}
