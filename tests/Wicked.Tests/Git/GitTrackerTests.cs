using System.Collections.Immutable;
using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitTrackerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ImmutableArray<LineChange>> NextAsync(LineChangeTracker tracker, Action trigger)
    {
        var tcs = new TaskCompletionSource<ImmutableArray<LineChange>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(ImmutableArray<LineChange> marks) => tcs.TrySetResult(marks);
        tracker.MarksChanged += Handler;
        try
        {
            trigger();
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
        finally
        {
            tracker.MarksChanged -= Handler;
        }
    }

    [Fact]
    public async Task MarksFollowTheTextAgainstTheIndexAndClearWhenStaged()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("f.txt", "a\nb\nc\n"));
        using var tracker = new LineChangeTracker(repo, "f.txt", TimeSpan.FromMilliseconds(30));
        var clean = await NextAsync(tracker, () => tracker.TextChanged(() => "a\nb\nc\n"));
        Assert.Empty(clean); // before the base is read, the tracker has nothing to compare with
        Assert.Empty(await NextAsync(tracker, () => _ = tracker.RefreshBaseAsync(Ct)));

        var edited = await NextAsync(tracker, () => tracker.TextChanged(() => "a\nB\nc\nnew\n"));
        Assert.Equal([new LineChange(1, LineChangeKind.Modified), new LineChange(3, LineChangeKind.Added)], edited);
        Assert.Equal(2, tracker.Blocks.Length);

        // Staging the file moves the index; the marks go.
        box.Write("f.txt", "a\nB\nc\nnew\n");
        await repo.StageAsync(["f.txt"], Ct);
        var staged = await NextAsync(tracker, () => _ = tracker.RefreshBaseAsync(Ct));
        Assert.Empty(staged);
    }

    [Fact]
    public async Task ABurstOfKeystrokesIsOneDiffOfTheLatestText()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("f.txt", "a\nb\n"));
        using var tracker = new LineChangeTracker(repo, "f.txt", TimeSpan.FromMilliseconds(150));
        await tracker.RefreshBaseAsync(Ct);
        var raised = 0;
        tracker.MarksChanged += _ => Interlocked.Increment(ref raised);
        var snapshots = 0;
        var marks = await NextAsync(tracker, () =>
        {
            for (var i = 1; i <= 30; i++)
            {
                var text = "a\nb\n" + new string('x', i);
                tracker.TextChanged(() =>
                {
                    Interlocked.Increment(ref snapshots);
                    return text;
                });
            }
        });
        await Task.Delay(400, Ct);
        Assert.Equal([new LineChange(2, LineChangeKind.Modified)], marks);
        Assert.Equal(1, Volatile.Read(ref raised));
        Assert.Equal(1, Volatile.Read(ref snapshots));
    }

    [Fact]
    public async Task AnUntrackedFileHasNoMarks()
    {
        var (box, repo) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("f.txt", "a\n"));
        box.Write("loose.txt", "x\n");
        using var tracker = new LineChangeTracker(repo, "loose.txt", TimeSpan.FromMilliseconds(30));
        var marks = await NextAsync(tracker, () =>
        {
            tracker.TextChanged(() => "x\ny\n");
            _ = tracker.RefreshBaseAsync(Ct);
        });
        Assert.Empty(marks);
        Assert.True(tracker.IsUntracked);
    }
}
