using System.Diagnostics;
using Wicked.Core;
using Wicked.Core.Catalog;
using Wicked.Core.Models;

namespace Wicked.Tests.Models;

/// <summary>The scheduler against the stand-in Hub, GitHub and catalog: what it asks, when, and what it tells the user.</summary>
public sealed class UpdateSchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string CoderId = ModelWorld.Coder;
    private const string CoderFile = "Tiny-Coder-Q8_0.gguf";
    private static readonly string CoderInfo = "/api/models/" + CoderId;

    private sealed class Rig : IAsyncDisposable
    {
        public required ModelWorld World { get; init; }

        public required TempProfile Profile { get; init; }

        public required ModelServices Services { get; init; }

        public NoticeBoard Board { get; } = new();

        public HubRepo Coder => World.Hub.Repo(CoderId);

        public static async Task<Rig> StartAsync(Func<bool>? offline = null, bool installChat = false)
        {
            var world = await ModelWorld.StartAsync();
            var profile = new TempProfile();
            var services = world.Services(profile.Paths, offline);
            var rig = new Rig { World = world, Profile = profile, Services = services };
            services.Updates.Notices = rig.Board;
            await services.InitializeAsync(allowNetwork: true, Ct);
            await rig.InstallAsync(CoderId, "Q8_0");
            if (installChat) await rig.InstallAsync(ModelWorld.Chat, "Q4_K_M");
            return rig;
        }

        public async Task InstallAsync(string repo, string quant)
        {
            var model = World.Catalog.Models.First(m => m.Repo == repo);
            await Services.InstallModelAsync(model, model.Tiers.First(t => t.Quant == quant), licenceAccepted: true, Ct);
        }

        public int InfoRequests => World.Hub.CountOf(CoderInfo);

        public Notice? Notice(string key) => Board.All.FirstOrDefault(n => n.Key == key);

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            Profile.Dispose();
            await World.DisposeAsync();
        }
    }

    private static async Task WaitAsync(Func<bool> condition, double seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("condition never became true");
            await Task.Delay(20, Ct);
        }
    }

    private static SchedulerOptions Quick(TimeSpan? interval = null, TimeSpan? backoff = null) => new()
    {
        Interval = interval ?? TimeSpan.FromMilliseconds(150),
        Jitter = 0,
        FirstDelay = TimeSpan.FromMilliseconds(50),
        BackoffBase = backoff ?? TimeSpan.FromMilliseconds(300),
    };

    [Fact(Timeout = 60_000)]
    public async Task AFlippedShaBecomesANoticeWithinOneTick()
    {
        await using var rig = await Rig.StartAsync();
        rig.Services.Updates.Options = Quick();
        rig.Services.Updates.Start();
        await WaitAsync(() => rig.Services.Updates.Last is not null);
        Assert.Null(rig.Notice("model-update:" + CoderId));

        rig.Coder.Upload("rev2", CoderFile, StandIns.Weights(1_600_000, 5), "Fix the chat template");

        await WaitAsync(() => rig.Notice("model-update:" + CoderId) is not null);
        var notice = rig.Notice("model-update:" + CoderId)!;
        Assert.Equal(NoticeKind.Action, notice.Kind);
        Assert.Equal("panel/models", notice.Route);
        Assert.Contains("newer upload", notice.Text, StringComparison.Ordinal);
        Assert.Equal(["update", "dismiss", "mute"], notice.Actions!.Select(a => a.Id));
        var update = rig.Services.State.UpdateFor(CoderId)!;
        Assert.Equal(["Fix the chat template"], update.Titles);
        Assert.Equal("rev2", update.LatestRevision);
        Assert.Equal(1_600_000, update.NewSize);
    }

    [Fact(Timeout = 60_000)]
    public async Task ACommitThatChangedNoWeightsIsNotNewsAndItsTreeIsAskedOnlyOnce()
    {
        await using var rig = await Rig.StartAsync();
        rig.Coder.Sha = "rev2"; // a README edit: same files
        var tree = "/api/models/" + CoderId + "/tree/rev2";

        var first = await rig.Services.Updates.CheckNowAsync(Ct);
        var second = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.Empty(first.Models!.Found);
        Assert.Empty(second.Models!.Found);
        Assert.Null(rig.Notice("model-update:" + CoderId));
        Assert.Equal(1, rig.World.Hub.CountOf(tree));
    }

    [Fact(Timeout = 60_000)]
    public async Task OfflineMeansNoRequestsAtAll()
    {
        var offline = true;
        await using var rig = await Rig.StartAsync(() => offline);
        var hubBefore = rig.World.Hub.Requests.Count;
        var catalogBefore = rig.World.CatalogRequests;
        rig.Coder.Upload("rev2", CoderFile, StandIns.Weights(1_600_000, 5), "New");
        rig.World.AppTag = "v9.0.0";
        rig.Services.Updates.Options = Quick();
        rig.Services.Updates.Start();

        var result = await rig.Services.Updates.CheckNowAsync(Ct);
        await Task.Delay(700, Ct); // several ticks

        Assert.Equal(SweepSkip.Offline, result.Skipped);
        Assert.Equal(hubBefore, rig.World.Hub.Requests.Count);
        Assert.Equal(catalogBefore, rig.World.CatalogRequests);
        Assert.Empty(rig.Board.All);

        // Control: the same rig, online, does ask.
        offline = false;
        var online = await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Null(online.Skipped);
        Assert.True(rig.World.Hub.Requests.Count > hubBefore);
        Assert.NotNull(rig.Notice("model-update:" + CoderId));
    }

    [Fact(Timeout = 60_000)]
    public async Task NoNetworkMeansNoRequestsEither()
    {
        await using var rig = await Rig.StartAsync();
        rig.Services.Updates.NetworkAvailable = () => false;
        var before = rig.World.Hub.Requests.Count;

        var result = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.Equal(SweepSkip.NoNetwork, result.Skipped);
        Assert.Equal(before, rig.World.Hub.Requests.Count);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARateLimitBacksOffForAtLeastAsLongAsTheHubAsked()
    {
        await using var rig = await Rig.StartAsync();
        rig.Coder.InfoStatus = 429;
        rig.Coder.RetryAfterSeconds = 1;
        var baseline = rig.InfoRequests;
        rig.Services.Updates.Options = Quick(backoff: TimeSpan.FromMilliseconds(200));
        var started = Stopwatch.StartNew();
        rig.Services.Updates.Start();

        await WaitAsync(() => rig.InfoRequests == baseline + 1);
        var firstAt = started.Elapsed;
        await Task.Delay(700, Ct); // far longer than the interval and the base backoff, shorter than Retry-After
        Assert.Equal(baseline + 1, rig.InfoRequests);

        await WaitAsync(() => rig.InfoRequests == baseline + 2, 5);
        Assert.True(started.Elapsed - firstAt >= TimeSpan.FromSeconds(0.95), $"the second request came after {started.Elapsed - firstAt}");
        Assert.NotNull(rig.Services.Updates.Last!.Problem);
        Assert.Equal(TimeSpan.FromSeconds(1), rig.Services.Updates.Last.RetryAfter);
    }

    [Fact(Timeout = 60_000)]
    public async Task AServerErrorDoublesTheBackoffEachTimeInARow()
    {
        await using var rig = await Rig.StartAsync();
        rig.Coder.InfoStatus = 503;
        rig.Services.Updates.Options = Quick(backoff: TimeSpan.FromMilliseconds(250));
        var baseline = rig.InfoRequests;
        var times = new List<TimeSpan>();
        var watch = Stopwatch.StartNew();
        rig.Services.Updates.Swept += _ =>
        {
            lock (times) times.Add(watch.Elapsed);
        };
        rig.Services.Updates.Start();

        await WaitAsync(() => { lock (times) return times.Count >= 3; }, 8);

        TimeSpan[] seen;
        lock (times) seen = [.. times];
        var gaps = seen.Zip(seen.Skip(1), (a, b) => b - a).ToList();
        Assert.True(gaps[0] >= TimeSpan.FromMilliseconds(240), gaps[0].ToString());
        Assert.True(gaps[1] >= TimeSpan.FromMilliseconds(480), gaps[1].ToString());
    }

    [Fact(Timeout = 60_000)]
    public async Task AnETagTurnsAnUnchangedRepoIntoANotModified()
    {
        await using var rig = await Rig.StartAsync();
        rig.Coder.ETags = true;

        await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Equal(0, rig.World.Hub.NotModified);
        await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.Equal(1, rig.World.Hub.NotModified);

        // A 304 still knows the commit the Hub last described, so a later upload is seen once the tag changes.
        rig.Coder.Upload("rev2", CoderFile, StandIns.Weights(1_600_000, 5), "New");
        var after = await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Equal("rev2", Assert.Single(after.Models!.Found).LatestRevision);
    }

    [Fact(Timeout = 60_000)]
    public async Task APinnedModelIsNeverAskedAbout()
    {
        await using var rig = await Rig.StartAsync();
        rig.Coder.Upload("rev2", CoderFile, StandIns.Weights(1_600_000, 5), "New");
        await rig.Services.SetUpdatePolicyAsync(CoderId, UpdatePolicy.Pin, Ct);
        var baseline = rig.InfoRequests;

        var result = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.Equal(baseline, rig.InfoRequests);
        Assert.Equal(1, result.Models!.Pinned);
        Assert.Empty(result.Models.Found);
        Assert.Null(rig.Notice("model-update:" + CoderId));
        Assert.Equal(UpdatePolicy.Pin, rig.Services.PolicyOf(rig.Services.State.Installed.Single(m => m.Id == CoderId)));
        // The choice lives in the model's own wicked.json.
        Assert.Equal("pin", (await rig.Services.Store.ReadManifestAsync(CoderId, Ct))!.Settings["update.policy"]);

        await rig.Services.SetUpdatePolicyAsync(CoderId, null, Ct);
        Assert.Equal(UpdatePolicy.Notify, rig.Services.PolicyOf(rig.Services.State.Installed.Single(m => m.Id == CoderId)));
    }

    [Fact(Timeout = 60_000)]
    public async Task DismissHidesThisUploadAndMuteHidesTheModel()
    {
        await using var rig = await Rig.StartAsync();
        rig.Coder.Upload("rev2", CoderFile, StandIns.Weights(1_600_000, 5), "New");
        await rig.Services.Updates.CheckNowAsync(Ct);
        var key = "model-update:" + CoderId;
        Assert.NotNull(rig.Notice(key));

        await rig.Services.Updates.PerformAsync(key, "dismiss", Ct);
        Assert.Null(rig.Notice(key));
        await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Null(rig.Notice(key)); // the same upload stays dismissed

        rig.Coder.Upload("rev3", CoderFile, StandIns.Weights(1_600_000, 6), "Newer still");
        await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.NotNull(rig.Notice(key)); // a newer commit is news again

        await rig.Services.Updates.PerformAsync(key, "mute", Ct);
        Assert.Null(rig.Notice(key));
        Assert.Equal(UpdatePolicy.Pin, rig.Services.PolicyOf(rig.Services.State.Installed.Single(m => m.Id == CoderId)));
    }

    [Fact(Timeout = 60_000)]
    public async Task ANewCatalogGenerationSaysWhenABetterPickExistsForThisPc()
    {
        await using var rig = await Rig.StartAsync(installChat: true);
        await rig.InstallAsync(ModelWorld.Quick, "Q4_K_M"); // everything the picks name is installed: nothing to suggest yet
        var generation = rig.World.Catalog.Generated;
        var before = await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Empty(before.Catalog!.Better);
        Assert.Null(rig.Notice("better-pick:tiny-chat-2"));

        var chat = rig.World.Catalog.Models.First(m => m.Id == "tiny-chat");
        var better = chat with { Id = "tiny-chat-2", Name = "Tiny Chat 2", Repo = "test/Tiny-Chat-2-GGUF", Released = new DateOnly(2026, 10, 1), Scores = new RoleScores(Everyday: 99, Coding: 70, Fast: 50), Supersedes = [] };
        var families = rig.World.Catalog.Families.Select(f => f.Id == "tiny" ? f with { Models = [.. f.Models, better] } : f).ToList();
        rig.World.PublishCatalog(rig.World.Catalog with { Generated = generation.AddDays(1), Families = families });

        var after = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.True(after.Catalog!.Changed);
        Assert.Equal("tiny-chat-2", Assert.Single(after.Catalog.Better).Model.Id);
        var notice = rig.Notice("better-pick:tiny-chat-2")!;
        Assert.Contains("A better pick for this PC: Tiny Chat 2", notice.Text, StringComparison.Ordinal);

        await rig.Services.Updates.PerformAsync(notice.Key, "dismiss", Ct);
        await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Null(rig.Notice(notice.Key));
    }

    [Fact(Timeout = 60_000)]
    public async Task ASupersedingModelInTheNewCatalogIsFlaggedOnTheInstalledOne()
    {
        await using var rig = await Rig.StartAsync();
        await rig.InstallAsync(ModelWorld.Old, "Q4_K_M"); // tiny-chat-1, which tiny-chat supersedes

        var result = await rig.Services.Updates.CheckNowAsync(Ct);

        var update = Assert.Single(result.Models!.Found, u => u.Kind == UpdateKind.Superseded);
        Assert.Equal("Tiny Chat", update.ReplacementName);
        Assert.Equal(["dismiss", "mute"], rig.Notice("model-update:" + ModelWorld.Old)!.Actions!.Select(a => a.Id));
    }

    [Fact(Timeout = 60_000)]
    public async Task TheCatalogIsAskedWithItsETagSoAnUnchangedOneCostsNothing()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Services.Updates.CheckNowAsync(Ct);
        var first = rig.World.CatalogRequests;

        var again = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.True(rig.World.CatalogRequests > first); // it asked...
        Assert.False(again.Catalog!.Changed);          // ...and was told nothing changed
        Assert.Null(again.Problem);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheAppAndTheEngineGetBannersAndTheAppOnlyOnceDismissed()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in engine is a Linux build and a shell script");
        await using var rig = await Rig.StartAsync();
        await rig.Services.EnsureEngineAsync(Ct); // b999
        rig.World.Builds = [999, 1000];
        rig.World.AppTag = "v9.0.0";

        var result = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.Equal("9.0.0", result.App!.Version);
        Assert.Equal("b1000", result.Engine!.Release.Tag);
        var app = rig.Notice("app-update")!;
        Assert.Equal("Wicked 9.0.0 is available", app.Text);
        Assert.Equal("url/https://example.test/releases/v9.0.0", app.Route);
        Assert.Equal(["notes", "dismiss"], app.Actions!.Select(a => a.Id));
        Assert.Equal("settings/runtime", rig.Notice("engine-update")!.Route);

        await rig.Services.Updates.PerformAsync("app-update", "dismiss", Ct);
        await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Null(rig.Notice("app-update"));
        Assert.NotNull(rig.Notice("engine-update"));

        rig.World.AppTag = "v0.0.0"; // the running build is newer than the latest release: no banner
        rig.World.Builds = [999];
        await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Null(rig.Notice("engine-update"));
    }

    [Fact(Timeout = 60_000)]
    public async Task TurningChecksOffStopsTheScheduleButNotCheckNow()
    {
        await using var rig = await Rig.StartAsync();
        rig.Services.Configure(new ModelOptions(CheckForUpdates: false));
        rig.Services.Updates.Options = Quick();
        var baseline = rig.InfoRequests;
        rig.Services.Updates.Start();

        await Task.Delay(600, Ct);
        Assert.Equal(baseline, rig.InfoRequests);

        var manual = await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Null(manual.Skipped);
        Assert.Equal(baseline + 1, rig.InfoRequests);
    }
}
