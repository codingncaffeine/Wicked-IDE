using Wicked.App.Git;
using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class ScmRowsTests
{
    private static GitChange Staged(string path, GitChangeKind kind = GitChangeKind.Modified) => new(path, null, kind, GitChangeKind.None, "M.");

    private static GitChange Unstaged(string path, GitChangeKind kind = GitChangeKind.Modified) => new(path, null, GitChangeKind.None, kind, ".M");

    private static GitChange Untracked(string path) => new(path, null, GitChangeKind.None, GitChangeKind.Untracked, "??");

    private static GitChange Conflict(string path) => new(path, null, GitChangeKind.Conflicted, GitChangeKind.Conflicted, "UU");

    private static GitStatus StatusOf(params GitChange[] changes) => new(GitBranchInfo.Unknown, [.. changes], GitOperation.None);

    private static string Show(ScmRow row) => row switch
    {
        ScmGroupRow g => $"[{g.Title} {g.Count}{(g.Expanded ? "" : " folded")}]",
        ScmFolderRow f => $"{new string(' ', f.Depth * 2)}{f.Name}/ {f.Count}{(f.Expanded ? "" : " folded")}",
        ScmFileRow f => $"{new string(' ', f.Depth * 2)}{f.Letter} {f.Name}{(f.Directory.Length > 0 ? $" ({f.Directory})" : "")}",
        _ => "?",
    };

    [Fact]
    public void ListModeGroupsInVsCodesOrderWithCountsAndSortedFiles()
    {
        var status = StatusOf(Untracked("z.txt"), Unstaged("src/b.cs"), Staged("src/a.cs", GitChangeKind.Added), Conflict("clash.txt"), Unstaged("README.md", GitChangeKind.Deleted));
        var rows = ScmRowBuilder.Build(status, tree: false, new HashSet<ScmGroup>(), new HashSet<string>());
        Assert.Equal(
            [
                "[Merge Changes 1]", "! clash.txt",
                "[Staged Changes 1]", "A a.cs (src)",
                "[Changes 2]", "D README.md", "M b.cs (src)",
                "[Untracked Changes 1]", "U z.txt",
            ],
            rows.Select(Show));
    }

    [Fact]
    public void TreeModeNestsFoldersFirstAndCountsTheFilesBelow()
    {
        var status = StatusOf(Unstaged("src/ui/view.cs"), Unstaged("src/core/x.cs"), Unstaged("src/core/y.cs"), Unstaged("top.txt"), Unstaged("src/root.cs"));
        var rows = ScmRowBuilder.Build(status, tree: true, new HashSet<ScmGroup>(), new HashSet<string>());
        Assert.Equal(
            [
                "[Changes 5]",
                "src/ 4",
                "  core/ 2", "    M x.cs", "    M y.cs",
                "  ui/ 1", "    M view.cs",
                "  M root.cs",
                "M top.txt",
            ],
            rows.Select(Show));
    }

    [Fact]
    public void FoldedGroupsAndFoldersHideTheirRows()
    {
        var status = StatusOf(Staged("a.txt"), Unstaged("src/x.cs"), Unstaged("src/y.cs"));
        var folded = ScmRowBuilder.Build(status, true, new HashSet<ScmGroup> { ScmGroup.Staged }, new HashSet<string> { "Changes:src" });
        Assert.Equal(["[Staged Changes 1 folded]", "[Changes 2]", "src/ 2 folded"], folded.Select(Show));
    }

    [Fact]
    public void ARenameShowsItsOriginAndAConflictItsMeaning()
    {
        var renamed = new GitChange("new name.txt", "old name.txt", GitChangeKind.Renamed, GitChangeKind.None, "R.");
        var row = (ScmFileRow)ScmRowBuilder.Build(StatusOf(renamed), false, new HashSet<ScmGroup>(), new HashSet<string>())[1];
        Assert.Equal("R", row.Letter);
        Assert.Equal("from old name.txt", row.Detail);
        var conflict = (ScmFileRow)ScmRowBuilder.Build(StatusOf(Conflict("c.txt")), false, new HashSet<ScmGroup>(), new HashSet<string>())[1];
        Assert.Equal("Both modified", conflict.Detail);
        Assert.True(conflict.IsConflict);
        Assert.True(conflict.CanStage);
        Assert.False(conflict.CanDiscard);
    }

    [Fact]
    public void ThousandsOfChangesBuildQuicklyInBothModes()
    {
        var changes = Enumerable.Range(0, 20_000).Select(i => Unstaged($"dir{i % 200}/sub{i % 40}/file{i}.cs")).ToArray();
        var status = StatusOf(changes);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var list = ScmRowBuilder.Build(status, false, new HashSet<ScmGroup>(), new HashSet<string>());
        var tree = ScmRowBuilder.Build(status, true, new HashSet<ScmGroup>(), new HashSet<string>());
        clock.Stop();
        Assert.Equal(20_001, list.Length);
        Assert.True(tree.Length > 20_001);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"building 20,000 rows twice took {clock.Elapsed}");
    }
}
