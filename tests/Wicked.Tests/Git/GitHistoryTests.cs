using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitHistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A --- B --- C ------- M   (main)
    ///        \             /
    ///         D --- E -----    (feature)
    /// at times 1..6 in the order A B D C E M.
    /// </summary>
    private static async Task<(GitSandbox Box, GitRepository Repo, Dictionary<string, string> Hash)> PlantedAsync()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        var h = new Dictionary<string, string>
        {
            ["A"] = await box.CommitAtAsync(1_700_000_001, "A", "Test", ("f.txt", "a\n")),
            ["B"] = await box.CommitAtAsync(1_700_000_002, "B", "Test", ("f.txt", "a\nb\n")),
        };
        await box.GitAsync("switch", "-q", "-c", "feature");
        h["D"] = await box.CommitAtAsync(1_700_000_003, "D", "Test", ("d.txt", "d\n"));
        await box.GitAsync("switch", "-q", "main");
        h["C"] = await box.CommitAtAsync(1_700_000_004, "C", "Test", ("c.txt", "c\n"));
        await box.GitAsync("switch", "-q", "feature");
        h["E"] = await box.CommitAtAsync(1_700_000_005, "E", "Test", ("e.txt", "e\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.MergeAtAsync(1_700_000_006, "feature", "M");
        h["M"] = (await box.GitAsync("rev-parse", "HEAD")).Trim();
        await box.GitAsync("tag", "v1", h["B"]);
        return (box, repo, h);
    }

    [Fact]
    public async Task TheLogIsNewestFirstWithParentsAndDecorations()
    {
        var (box, repo, h) = await PlantedAsync();
        using var _ = box;
        var log = await repo.LogAsync(0, 100, null, Ct);
        Assert.Equal(["M", "E", "C", "D", "B", "A"], log.Select(c => c.Subject));
        var merge = log[0];
        Assert.True(merge.IsMerge);
        Assert.Equal([h["C"], h["E"]], merge.Parents);
        Assert.Contains(merge.Refs, r => r is { Name: "HEAD", Kind: GitRefKind.Head });
        Assert.Contains(merge.Refs, r => r is { Name: "main", Kind: GitRefKind.LocalBranch });
        Assert.Contains(log.Single(c => c.Subject == "E").Refs, r => r is { Name: "feature", Kind: GitRefKind.LocalBranch });
        Assert.Contains(log.Single(c => c.Subject == "B").Refs, r => r is { Name: "v1", Kind: GitRefKind.Tag });
        Assert.Empty(log.Single(c => c.Subject == "A").Parents);

        // Paging: two commits at a time reproduces the same list.
        var paged = new List<GitCommit>();
        for (var skip = 0; ; skip += 2)
        {
            var page = await repo.LogAsync(skip, 2, null, Ct);
            if (page.IsEmpty) break;
            paged.AddRange(page);
        }

        Assert.Equal(log.Select(c => c.Hash), paged.Select(c => c.Hash));
    }

    [Fact]
    public async Task TheGraphPlacesLanesForAPlantedMergeHistory()
    {
        var (box, repo, _) = await PlantedAsync();
        using var _ = box;
        var rows = new LogGraphBuilder().Add(await repo.LogAsync(0, 100, null, Ct));
        string Describe(GraphRow r) => $"{r.Commit.Subject}@{r.Lane} " + string.Join(' ', r.Edges.Select(e => $"{e.Span.ToString()[0]}{e.FromLane}{e.ToLane}"));

        Assert.Equal(
            [
                "M@0 L00 L01",     // the merge: first parent keeps lane 0, second parent opens lane 1
                "E@1 U11 T00 L11", // E arrives on lane 1; lane 0 passes by
                "C@0 U00 T11 L00",
                "D@1 U11 T00 L11",
                "B@0 U00 U10 L00", // lanes 0 and 1 join at B
                "A@0 U00",
            ],
            rows.Select(Describe));

        Assert.Equal(2, rows[0].LaneCount);
        // The branch keeps its colour down the page, and the merged-in branch has another.
        Assert.Equal(rows[0].Color, rows[2].Color);
        Assert.NotEqual(rows[1].Color, rows[2].Color);

        // Fed page by page, the builder draws the same picture as in one go.
        var builder = new LogGraphBuilder();
        var log = await repo.LogAsync(0, 100, null, Ct);
        var pieces = log.Chunk(2).SelectMany(page => builder.Add(page)).ToList();
        Assert.Equal(rows.Select(Describe), pieces.Select(Describe));
    }

    [Fact]
    public async Task ABranchTipStartsANewLaneWithNoLineAboveIt()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAtAsync(1_700_000_001, "base", "Test", ("a.txt", "1\n"));
        await box.GitAsync("switch", "-q", "-c", "side");
        await box.CommitAtAsync(1_700_000_002, "side tip", "Test", ("b.txt", "1\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.CommitAtAsync(1_700_000_003, "main tip", "Test", ("c.txt", "1\n"));
        var rows = new LogGraphBuilder().Add(await repo.LogAsync(0, 10, null, Ct));
        Assert.Equal(["main tip", "side tip", "base"], rows.Select(r => r.Commit.Subject));
        Assert.Empty(rows[0].Edges.Where(e => e.Span == GraphSpan.Upper));
        Assert.Empty(rows[1].Edges.Where(e => e.Span == GraphSpan.Upper)); // the side branch's tip: nothing above
        Assert.Equal(1, rows[1].Lane);
        Assert.Equal(2, rows[2].Edges.Count(e => e.Span == GraphSpan.Upper));
    }

    [Fact]
    public async Task CommitFilesListsAddsModifiesDeletesAndRenames()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        var root = await box.CommitAtAsync(1_700_000_001, "root", "Test", ("keep.txt", "k\n"), ("gone.txt", "g\n"), ("old name.txt", "line one\nline two\nline three\nline four\n"));
        File.Delete(box.PathOf("gone.txt"));
        box.Write("keep.txt", "k2\n");
        await box.GitAsync("mv", "old name.txt", "new name.txt");
        box.Write("added.txt", "a\n");
        var second = await box.CommitAtAsync(1_700_000_002, "second");

        var rootFiles = await repo.CommitFilesAsync(root, Ct);
        Assert.Equal(3, rootFiles.Length);
        Assert.All(rootFiles, f => Assert.Equal(GitChangeKind.Added, f.Kind));

        var files = await repo.CommitFilesAsync(second, Ct);
        Assert.Equal(GitChangeKind.Modified, files.Single(f => f.Path == "keep.txt").Kind);
        Assert.Equal(GitChangeKind.Deleted, files.Single(f => f.Path == "gone.txt").Kind);
        Assert.Equal(GitChangeKind.Added, files.Single(f => f.Path == "added.txt").Kind);
        var renamed = files.Single(f => f.Path == "new name.txt");
        Assert.Equal((GitChangeKind.Renamed, "old name.txt"), (renamed.Kind, renamed.OriginalPath));

        var diff = await repo.CommitDiffAsync(second, "keep.txt", null, 3, Ct);
        Assert.Contains("+k2", diff, StringComparison.Ordinal);
        Assert.Equal("k\n", await repo.ShowAsync(root, "keep.txt", Ct));
        Assert.Null(await repo.ShowAsync(second, "gone.txt", Ct));
    }

    [Fact]
    public async Task FileHistoryFollowsARename()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        var content = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"row {i}")) + "\n";
        await box.CommitAtAsync(1_700_000_001, "create", "Test", ("a.txt", content));
        await box.CommitAtAsync(1_700_000_002, "unrelated", "Test", ("other.txt", "o\n"));
        await box.GitAsync("mv", "a.txt", "b.txt");
        await box.CommitAtAsync(1_700_000_003, "rename");
        await box.CommitAtAsync(1_700_000_004, "edit", "Test", ("b.txt", content + "more\n"));

        var history = await repo.FileHistoryAsync("b.txt", 0, 50, Ct);
        Assert.Equal(["edit", "rename", "create"], history.Select(c => c.Subject));
        Assert.Equal(["edit"], (await repo.FileHistoryAsync("b.txt", 0, 1, Ct)).Select(c => c.Subject));
    }

    [Fact]
    public async Task BlameStreamsHunksAndNamesTheAuthorAndUncommittedLines()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAtAsync(1_700_000_000, "first by alice", "Alice", ("f.txt", "one\ntwo\nthree\n"));
        await box.CommitAtAsync(1_700_100_000, "second by bob", "Bob", ("f.txt", "one\nTWO\nthree\nfour\n"));

        var hunks = new List<BlameHunk>();
        Assert.True(await repo.BlameAsync("f.txt", null, hunks.Add, Ct));
        BlameHunk At(int line) => hunks.Single(h => line >= h.StartLine && line < h.StartLine + h.LineCount);
        Assert.Equal("Alice", At(0).Author);
        Assert.Equal("first by alice", At(0).Summary);
        Assert.Equal("Bob", At(1).Author);
        Assert.Equal("Alice", At(2).Author);
        Assert.Equal("Bob", At(3).Author);
        Assert.Equal("bob@example.invalid", At(3).AuthorEmail);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_100_000), At(3).Date);
        Assert.Equal(4, hunks.Sum(h => h.LineCount));

        // Unsaved text is blamed too: the edited line is "not committed yet".
        var edited = new List<BlameHunk>();
        Assert.True(await repo.BlameAsync("f.txt", "one\nTWO\nthree changed\nfour\n", edited.Add, Ct));
        var line2 = edited.Single(h => 2 >= h.StartLine && 2 < h.StartLine + h.LineCount);
        Assert.True(line2.IsUncommitted);
        Assert.Equal("Bob", edited.Single(h => 1 >= h.StartLine && 1 < h.StartLine + h.LineCount).Author);

        // An untracked file cannot be blamed.
        box.Write("loose.txt", "x\n");
        Assert.False(await repo.BlameAsync("loose.txt", null, _ => { }, Ct));
    }

    [Fact]
    public void TheBlameLineForTheStatusBarReadsLikeVsCodes()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        BlameHunk Hunk(string email, TimeSpan ago) => new(0, 1, "abc1234567890abcdef1234567890abcdef12345", "Bob", email, now - ago, "Fix the thing");
        Assert.Equal("You, 3 days ago • Fix the thing", BlameText.Format(Hunk("me@example.invalid", TimeSpan.FromDays(3)), "ME@example.invalid", now));
        Assert.Equal("Bob, 1 month ago • Fix the thing", BlameText.Format(Hunk("bob@example.invalid", TimeSpan.FromDays(33)), "me@example.invalid", now));
        Assert.Equal("Bob, just now • Fix the thing", BlameText.Format(Hunk("b@x", TimeSpan.FromSeconds(5)), null, now));
        Assert.Equal("Bob, 2 years ago • Fix the thing", BlameText.Format(Hunk("b@x", TimeSpan.FromDays(800)), null, now));
        Assert.Equal("You, uncommitted changes", BlameText.Format(new BlameHunk(0, 1, new string('0', 40), "Not Committed Yet", "x", now, "x"), null, now));
    }
}

public sealed class GitStashLogTests
{
    [Fact]
    public async Task TheGraphLeavesTheStashesCommitsOut()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        box.Write("a.txt", "2\n");
        Assert.True((await repo.StashPushAsync("parked", true, TestContext.Current.CancellationToken)).Ok);
        var log = await repo.LogAsync(0, 50, null, TestContext.Current.CancellationToken);
        Assert.Equal(["base"], log.Select(c => c.Subject));
    }
}
