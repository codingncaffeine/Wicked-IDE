using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitWatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheIndexAndHeadAndRefsChangesRaiseOneChangedEach()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        using var watcher = new GitWatcher(repo, TimeSpan.FromMilliseconds(100));
        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);

        box.Write("a.txt", "2\n");
        await repo.StageAsync(["a.txt"], Ct); // the index
        Assert.True(await Until(() => Volatile.Read(ref count) >= 1), "staging raised nothing");

        var afterStage = Volatile.Read(ref count);
        await repo.CommitAsync(new GitCommitOptions("two"), Ct); // HEAD's branch ref
        Assert.True(await Until(() => Volatile.Read(ref count) > afterStage), "committing raised nothing");

        var afterCommit = Volatile.Read(ref count);
        await repo.CreateTagAsync("t1", null, null, Ct); // a ref
        Assert.True(await Until(() => Volatile.Read(ref count) > afterCommit), "tagging raised nothing");
    }

    [Fact]
    public async Task ABurstOfWorkTreeNotificationsIsOneEventAndGitDirPathsAreIgnored()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        using var watcher = new GitWatcher(repo, TimeSpan.FromMilliseconds(150));
        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);

        for (var i = 0; i < 200; i++) watcher.Notify(Path.Combine(repo.Root, $"file{i}.txt"));
        Assert.True(await Until(() => Volatile.Read(ref count) >= 1), "the burst raised nothing");
        await Task.Delay(500, Ct);
        Assert.Equal(1, Volatile.Read(ref count));

        watcher.Notify(Path.Combine(repo.Root, ".git", "objects", "ab", "cdef"));
        await Task.Delay(500, Ct);
        Assert.Equal(1, Volatile.Read(ref count));
    }

    [Fact]
    public async Task AWatcherOfItsOwnSeesWorkTreeFilesWhenAsked()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        using var watcher = new GitWatcher(repo, TimeSpan.FromMilliseconds(100), watchWorkTree: true);
        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);
        box.Write("made.txt", "x\n");
        Assert.True(await Until(() => Volatile.Read(ref count) >= 1), "a new file raised nothing");
    }

    [Fact]
    public async Task OurOwnStatusReadsDoNotTriggerAnotherRefresh()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var _ = box;
        await box.CommitAsync("base", ("a.txt", "1\n"));
        box.Write("a.txt", "2\n");
        using var watcher = new GitWatcher(repo, TimeSpan.FromMilliseconds(100));
        await Task.Delay(300, Ct);
        var count = 0;
        watcher.Changed += () => Interlocked.Increment(ref count);
        for (var i = 0; i < 5; i++) await repo.StatusAsync(Ct);
        await Task.Delay(600, Ct);
        Assert.Equal(0, Volatile.Read(ref count));
    }

    private static async Task<bool> Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (condition()) return true;
            await Task.Delay(25, Ct);
        }

        return condition();
    }
}
