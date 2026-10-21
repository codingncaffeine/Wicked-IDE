using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitConflictTests
{
    private const string Merge2 = "top\n<<<<<<< HEAD\nours 1\nours 2\n=======\ntheirs 1\n>>>>>>> feature/x\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\nC\n>>>>>>> feature/x\nbottom\n";

    private const string Diff3 = "<<<<<<< HEAD\ncur\n||||||| merged common ancestors\nbase\n=======\ninc\n>>>>>>> other\n";

    [Fact]
    public void RegionsAreFoundWithTheirMarkersAndLabels()
    {
        var regions = ConflictParser.Parse(TextLines.Split(Merge2));
        Assert.Equal(2, regions.Length);
        var first = regions[0];
        Assert.Equal((1, 4, 6), (first.StartLine, first.SeparatorLine, first.EndLine));
        Assert.Null(first.BaseLine);
        Assert.Equal("HEAD", first.CurrentLabel);
        Assert.Equal("feature/x", first.IncomingLabel);
        Assert.Equal((8, 10, 13), (regions[1].StartLine, regions[1].SeparatorLine, regions[1].EndLine));
    }

    [Fact]
    public void AcceptCurrentIncomingAndBothResolveARegion()
    {
        var lines = TextLines.Split(Merge2);
        var first = ConflictParser.Parse(lines)[0];
        Assert.Equal("top\nours 1\nours 2\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\nC\n>>>>>>> feature/x\nbottom\n", ConflictParser.Apply(Merge2, first, ConflictChoice.Current));
        Assert.Equal("top\ntheirs 1\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\nC\n>>>>>>> feature/x\nbottom\n", ConflictParser.Apply(Merge2, first, ConflictChoice.Incoming));
        Assert.Equal("top\nours 1\nours 2\ntheirs 1\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\nC\n>>>>>>> feature/x\nbottom\n", ConflictParser.Apply(Merge2, first, ConflictChoice.Both));
        Assert.Equal("top\ntheirs 1\nours 1\nours 2\nmiddle\n<<<<<<< HEAD\nA\n=======\nB\nC\n>>>>>>> feature/x\nbottom\n", ConflictParser.Apply(Merge2, first, ConflictChoice.BothIncomingFirst));
    }

    [Fact]
    public void AcceptAllResolvesEveryRegionAndLeavesNoMarkers()
    {
        Assert.Equal("top\nours 1\nours 2\nmiddle\nA\nbottom\n", ConflictParser.ApplyAll(Merge2, ConflictChoice.Current));
        Assert.Equal("top\ntheirs 1\nmiddle\nB\nC\nbottom\n", ConflictParser.ApplyAll(Merge2, ConflictChoice.Incoming));
        Assert.Equal("top\nours 1\nours 2\ntheirs 1\nmiddle\nA\nB\nC\nbottom\n", ConflictParser.ApplyAll(Merge2, ConflictChoice.Both));
        Assert.Empty(ConflictParser.Parse(TextLines.Split(ConflictParser.ApplyAll(Merge2, ConflictChoice.Both))));
    }

    [Fact]
    public void Diff3MarkersGiveABaseThatCanBeAccepted()
    {
        var region = ConflictParser.Parse(TextLines.Split(Diff3)).Single();
        Assert.True(region.HasBase);
        Assert.Equal("merged common ancestors", region.BaseLabel);
        Assert.Equal("base\n", ConflictParser.Apply(Diff3, region, ConflictChoice.Base));
        Assert.Equal("cur\n", ConflictParser.Apply(Diff3, region, ConflictChoice.Current));
        Assert.Equal("inc\n", ConflictParser.Apply(Diff3, region, ConflictChoice.Incoming));
    }

    [Fact]
    public void AStrayMarkerLineAndCrlfAreHandled()
    {
        // A "=======" heading underline is not a conflict.
        Assert.Empty(ConflictParser.Parse(TextLines.Split("Title\n=======\ntext\n")));
        Assert.Empty(ConflictParser.Parse(TextLines.Split("<<<<<<< HEAD\nunfinished\n")));
        var crlf = "a\r\n<<<<<<< HEAD\r\nx\r\n=======\r\ny\r\n>>>>>>> o\r\nz\r\n";
        Assert.Equal("a\r\nx\r\ny\r\nz\r\n", ConflictParser.ApplyAll(crlf, ConflictChoice.Both));
    }

    [Fact]
    public async Task ResolvingARealMergeConflictAndStagingItFinishesTheMerge()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("f.txt", "top\nline\nbottom\n"));
        await box.GitAsync("switch", "-q", "-c", "other");
        await box.CommitAsync("other", ("f.txt", "top\nother side\nbottom\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.CommitAsync("main", ("f.txt", "top\nmain side\nbottom\n"));
        await repo.MergeAsync("other", TestContext.Current.CancellationToken);

        var text = box.Read("f.txt");
        var region = Assert.Single(ConflictParser.Parse(TextLines.Split(text)));
        box.Write("f.txt", ConflictParser.Apply(text, region, ConflictChoice.Both));
        Assert.True((await repo.StageAsync(["f.txt"], TestContext.Current.CancellationToken)).Ok);
        Assert.Empty((await repo.StatusAsync(TestContext.Current.CancellationToken)).Conflicts);

        var commit = await repo.CommitAsync(new GitCommitOptions("Merge other"), TestContext.Current.CancellationToken);
        Assert.True(commit.Ok, commit.Error);
        Assert.Equal(GitOperation.None, (await repo.StatusAsync(TestContext.Current.CancellationToken)).Operation);
        Assert.Equal("top\nmain side\nother side\nbottom\n", await box.GitAsync("show", "HEAD:f.txt"));
    }

    [Fact]
    public async Task TakingOneSideWholeResolvesTheFile()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("f.txt", "x\n"));
        await box.GitAsync("switch", "-q", "-c", "other");
        await box.CommitAsync("other", ("f.txt", "theirs\n"));
        await box.GitAsync("switch", "-q", "main");
        await box.CommitAsync("main", ("f.txt", "ours\n"));
        await repo.MergeAsync("other", TestContext.Current.CancellationToken);
        Assert.True((await repo.TakeSideAsync("f.txt", ours: false, TestContext.Current.CancellationToken)).Ok);
        Assert.Equal("theirs\n", box.Read("f.txt"));
        Assert.Empty((await repo.StatusAsync(TestContext.Current.CancellationToken)).Conflicts);
    }
}
