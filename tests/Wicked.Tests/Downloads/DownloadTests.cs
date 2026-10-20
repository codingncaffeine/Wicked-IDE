using System.Collections.Concurrent;
using System.Diagnostics;
using Wicked.Core.Downloads;
using Wicked.Core.Http;

namespace Wicked.Tests;

public sealed class DownloadTests : IDisposable
{
    private readonly TempProfile _profile = new();
    private readonly HttpClient _http = WickedHttp.CreateClient(offline: () => false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string StateFile => Path.Combine(_profile.Root, "state", "downloads.json");

    private string Dest(string name = "model.gguf") => Path.Combine(_profile.Root, "models", name);

    private DownloadManager NewManager(Action<DownloadOptions>? tweak = null)
    {
        var options = new DownloadOptions { BackoffBase = TimeSpan.FromMilliseconds(5), BackoffCap = TimeSpan.FromMilliseconds(20), MinFreeBytes = 0 };
        tweak?.Invoke(options);
        return new DownloadManager(_http, StateFile, options);
    }

    private static async Task<DownloadSnapshot> Finish(DownloadManager m, string id) =>
        await m.WaitAsync(id, Ct).WaitAsync(TimeSpan.FromSeconds(60), Ct);

    public void Dispose()
    {
        _http.Dispose();
        _profile.Dispose();
    }

    [Fact]
    public async Task DownloadsAndVerifiesAFile()
    {
        await using var server = await FileServer.StartAsync();
        await using var manager = NewManager();

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
        var done = await Finish(manager, id);

        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(server.Sha256, done.Sha256);
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Dest(), Ct));
        Assert.False(File.Exists(Dest() + ".part"));
        Assert.False(File.Exists(Dest() + ".part.json"));
    }

    [Fact]
    public async Task ResumesAfterTheConnectionIsCutAtHalf()
    {
        await using var server = await FileServer.StartAsync();
        server.CutAfter = server.Content.Length / 2;
        server.FailStatus = 503;
        server.FailTimes = 100;
        server.FailOnlyResumes = true;
        await using var manager = NewManager(o => o.MaxAttempts = 2);

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
        var failed = await Finish(manager, id);

        Assert.Equal(DownloadState.Failed, failed.State);
        // The reset can discard bytes the server had already written, so the part is some prefix, not exactly the cut.
        var partLength = new FileInfo(Dest() + ".part").Length;
        Assert.InRange(partLength, 1, server.Content.Length / 2);

        server.FailStatus = 0;

        manager.Resume(id);
        var done = await Finish(manager, id);

        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Dest(), Ct));
        var resumed = server.Requests[^1];
        Assert.Equal($"bytes={partLength}-", resumed.Range);
        Assert.Equal("\"v1\"", resumed.IfRange);
    }

    [Fact]
    public async Task RetriesByItselfAfterACut()
    {
        await using var server = await FileServer.StartAsync();
        server.CutAfter = 3_000_000;
        await using var manager = NewManager();

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
        var done = await Finish(manager, id);

        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(2, server.Requests.Count);
        Assert.NotNull(server.Requests[1].Range);
    }

    [Fact]
    public async Task AWrongHashFailsAndRemovesThePart()
    {
        await using var server = await FileServer.StartAsync(1_000_000);
        await using var manager = NewManager();

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), ExpectedSha256: new string('0', 64)));
        var failed = await Finish(manager, id);

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("Checksum mismatch", failed.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Dest()));
        Assert.False(File.Exists(Dest() + ".part"));
        Assert.False(File.Exists(Dest() + ".part.json"));
    }

    [Fact]
    public async Task AServerThatIgnoresRangeRestartsFromZeroAndStillVerifies()
    {
        await using var server = await FileServer.StartAsync();
        server.HonourRange = false;
        server.CutAfter = 2_000_000;
        await using var manager = NewManager();

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
        var done = await Finish(manager, id);

        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Dest(), Ct));
        Assert.NotNull(server.Requests[1].Range);
    }

    [Fact]
    public async Task AChangedFileRestartsInsteadOfStitching()
    {
        await using var server = await FileServer.StartAsync();
        server.CutAfter = 2_000_000;
        server.FailStatus = 503;
        server.FailTimes = 100;
        server.FailOnlyResumes = true;
        await using var manager = NewManager(o => o.MaxAttempts = 2);
        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
        Assert.Equal(DownloadState.Failed, (await Finish(manager, id)).State);
        Assert.True(File.Exists(Dest() + ".part"));
        server.FailStatus = 0;

        server.ETag = "\"v2\"";
        manager.Resume(id);
        var done = await Finish(manager, id);

        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Dest(), Ct));
    }

    [Fact]
    public async Task PauseKeepsThePartAndResumeFinishes()
    {
        await using var server = await FileServer.StartAsync();
        server.ChunkDelay = TimeSpan.FromMilliseconds(5);
        await using var manager = NewManager();
        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
        await WaitUntil(() => manager.Get(id)!.BytesDone > 500_000);

        await manager.PauseAsync(id);

        var paused = manager.Get(id)!;
        Assert.Equal(DownloadState.Paused, paused.State);
        Assert.True(File.Exists(Dest() + ".part"));
        Assert.True(paused.BytesDone < server.Content.Length);

        server.ChunkDelay = TimeSpan.Zero;
        manager.Resume(id);
        var done = await Finish(manager, id);

        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(server.Sha256, done.Sha256);
        Assert.StartsWith("bytes=", server.Requests[^1].Range, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelRemovesThePartAndTheJob()
    {
        await using var server = await FileServer.StartAsync();
        server.ChunkDelay = TimeSpan.FromMilliseconds(5);
        await using var manager = NewManager();
        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
        await WaitUntil(() => manager.Get(id)!.BytesDone > 300_000);

        await manager.CancelAsync(id);

        Assert.Null(manager.Get(id));
        Assert.False(File.Exists(Dest() + ".part"));
        Assert.False(File.Exists(Dest() + ".part.json"));
        Assert.False(File.Exists(Dest()));
    }

    [Fact]
    public async Task RunsAtMostTheConcurrencyLimit()
    {
        await using var server = await FileServer.StartAsync(2_000_000);
        server.ChunkDelay = TimeSpan.FromMilliseconds(2);
        await using var manager = NewManager(o => o.MaxConcurrent = 2);

        var ids = Enumerable.Range(0, 4).Select(i => manager.Enqueue(new DownloadRequest(server.Url, Dest($"m{i}.gguf"), ExpectedSha256: server.Sha256))).ToList();
        await WaitUntil(() => manager.Jobs.Count(j => j.State == DownloadState.Running) > 0);
        Assert.True(manager.Jobs.Count(j => j.State == DownloadState.Running) <= 2);
        foreach (var id in ids) Assert.Equal(DownloadState.Done, (await Finish(manager, id)).State);

        Assert.Equal(2, server.Peak);
    }

    [Fact]
    public async Task RetriesTransientStatusesButNotNotFound()
    {
        await using var server = await FileServer.StartAsync(1_000_000);
        server.FailStatus = 503;
        server.FailTimes = 2;
        await using var manager = NewManager();

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), ExpectedSha256: server.Sha256));
        Assert.Equal(DownloadState.Done, (await Finish(manager, id)).State);
        Assert.Equal(3, server.Requests.Count);

        server.Requests.Clear();
        server.FailStatus = 404;
        server.FailTimes = 10;
        var missing = manager.Enqueue(new DownloadRequest(server.Url, Dest("other.gguf")));
        var failed = await Finish(manager, missing);
        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("404", failed.Error, StringComparison.Ordinal);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task RefusesToStartWithoutFreeSpace()
    {
        await using var server = await FileServer.StartAsync(1_000_000);
        await using var manager = NewManager(o => o.FreeSpace = _ => 10_000);

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest()));
        var failed = await Finish(manager, id);

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("free space", failed.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Dest() + ".part"));
    }

    [Fact]
    public async Task ASizeMismatchFailsWithoutDownloading()
    {
        await using var server = await FileServer.StartAsync(1_000_000);
        await using var manager = NewManager();

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), ExpectedSize: 999));
        var failed = await Finish(manager, id);

        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Contains("Expected 999", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBandwidthCapSlowsAndLiftingItSpeedsUp()
    {
        await using var server = await FileServer.StartAsync(2_000_000);
        await using var manager = NewManager();
        manager.BandwidthLimit = 2_000_000;

        var watch = Stopwatch.StartNew();
        var first = manager.Enqueue(new DownloadRequest(server.Url, Dest("a.gguf"), ExpectedSha256: server.Sha256));
        await Finish(manager, first);
        var capped = watch.Elapsed;

        manager.BandwidthLimit = null;
        watch.Restart();
        var second = manager.Enqueue(new DownloadRequest(server.Url, Dest("b.gguf"), ExpectedSha256: server.Sha256));
        await Finish(manager, second);

        Assert.True(capped > TimeSpan.FromSeconds(0.8), $"capped run took {capped}");
        Assert.True(watch.Elapsed < capped);
    }

    [Fact]
    public async Task ChangingTheCapWhileRunningTakesEffect()
    {
        await using var server = await FileServer.StartAsync(4_000_000);
        await using var manager = NewManager();
        manager.BandwidthLimit = 500_000;
        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), ExpectedSha256: server.Sha256));
        await WaitUntil(() => manager.Get(id)!.BytesDone > 100_000);

        var watch = Stopwatch.StartNew();
        manager.BandwidthLimit = null;
        await Finish(manager, id);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed} after lifting the cap");
    }

    [Fact]
    public async Task ProgressEventsAreThrottledToTenPerSecond()
    {
        await using var server = await FileServer.StartAsync();
        server.ChunkDelay = TimeSpan.FromMilliseconds(5);
        await using var manager = NewManager();
        var progress = new ConcurrentBag<long>();
        var id = "";
        manager.Changed += (_, s) =>
        {
            if (s.Id == id && s.State == DownloadState.Running) progress.Add(Stopwatch.GetTimestamp());
        };
        var watch = Stopwatch.StartNew();
        id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), ExpectedSha256: server.Sha256));
        await Finish(manager, id);
        watch.Stop();

        Assert.True(progress.Count <= watch.Elapsed.TotalSeconds * 10 + 3, $"{progress.Count} events in {watch.Elapsed}");
        Assert.True(progress.Count >= 2);
    }

    [Fact]
    public async Task JobsSurviveARestartAndResumeWithRange()
    {
        await using var server = await FileServer.StartAsync();
        server.ChunkDelay = TimeSpan.FromMilliseconds(3);
        string id;
        long partial;
        await using (var first = NewManager())
        {
            id = first.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256, Title: "A model"));
            await WaitUntil(() => first.Get(id)!.BytesDone > 1_000_000);
        }

        partial = new FileInfo(Dest() + ".part").Length;
        Assert.True(File.Exists(StateFile));
        Assert.True(partial > 0 && partial < server.Content.Length);

        server.ChunkDelay = TimeSpan.Zero;
        await using var second = NewManager();
        await second.LoadAsync(Ct);
        var restored = Assert.Single(second.Jobs);
        Assert.Equal(id, restored.Id);
        Assert.Equal("A model", restored.Request.Title);
        var done = await Finish(second, id);

        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(server.Content, await File.ReadAllBytesAsync(Dest(), Ct));
        Assert.StartsWith("bytes=", server.Requests[^1].Range, StringComparison.Ordinal);
        Assert.True(long.Parse(server.Requests[^1].Range!["bytes=".Length..^1], System.Globalization.CultureInfo.InvariantCulture) >= partial - 1);
    }

    [Fact]
    public async Task RestoredJobsComeBackPausedWhenAutoResumeIsOff()
    {
        await using var server = await FileServer.StartAsync();
        server.ChunkDelay = TimeSpan.FromMilliseconds(3);
        await using (var first = NewManager())
        {
            var id = first.Enqueue(new DownloadRequest(server.Url, Dest(), server.Content.Length, server.Sha256));
            await WaitUntil(() => first.Get(id)!.BytesDone > 300_000);
        }

        await using var second = NewManager(o => o.AutoResume = false);
        await second.LoadAsync(Ct);

        Assert.Equal(DownloadState.Paused, Assert.Single(second.Jobs).State);
    }

    [Fact]
    public async Task TheTokenIsSentButNeverWrittenToTheStateFile()
    {
        await using var server = await FileServer.StartAsync(1_000_000);
        server.ChunkDelay = TimeSpan.FromMilliseconds(3);
        await using var manager = NewManager();

        var id = manager.Enqueue(new DownloadRequest(server.Url, Dest(), Authenticated: true) { AuthToken = "hf_supersecret" });
        await WaitUntil(() => manager.Get(id)!.BytesDone > 100_000);
        await manager.PauseAsync(id);
        Assert.Equal("Bearer hf_supersecret", server.Requests[0].Auth);
        await WaitUntil(() => File.Exists(StateFile));
        Assert.DoesNotContain("hf_supersecret", await File.ReadAllTextAsync(StateFile, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnqueueingTheSameDestinationTwiceReturnsTheSameJob()
    {
        await using var server = await FileServer.StartAsync();
        server.ChunkDelay = TimeSpan.FromMilliseconds(3);
        await using var manager = NewManager();

        var a = manager.Enqueue(new DownloadRequest(server.Url, Dest()));
        var b = manager.Enqueue(new DownloadRequest(server.Url, Dest()));

        Assert.Equal(a, b);
        await manager.CancelAsync(a);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("condition never became true");
            await Task.Delay(10, Ct);
        }
    }
}
