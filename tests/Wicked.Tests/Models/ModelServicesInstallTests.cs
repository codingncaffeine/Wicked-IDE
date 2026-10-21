using Wicked.Core.Hub;
using Wicked.Core.Models;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>Pause, resume and cancel; the licence step; gated repos; installing from the Hub; update checks; the settings that reach the services.</summary>
public sealed class ModelServicesInstallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<bool> UntilAsync(Func<bool> done, int seconds = 15)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            await Task.Delay(20, Ct);
        }

        return done();
    }

    private static string[] FilesUnder(string root) => Directory.Exists(root) ? Directory.GetFiles(root, "*", SearchOption.AllDirectories) : [];

    private static async Task<(HubStandIn Hub, ModelServices Services, TempProfile Profile)> StartAsync(HubRepo repo, params int[] engineBuilds)
    {
        var profile = new TempProfile();
        var hub = await HubStandIn.StartAsync(app =>
        {
            if (engineBuilds.Length > 0) StandIns.MapGitHub(app, engineBuilds);
        }, repo);
        var services = StandIns.Services(profile, hub.Server);
        await services.InitializeAsync(allowNetwork: false, Ct);
        return (hub, services, profile);
    }

    [Fact]
    public async Task APausedInstallStopsAndResumesToTheVerifiedFile()
    {
        var weights = StandIns.Weights(1_500_000);
        var repo = new HubRepo("test/slow-GGUF", "rev1") { Files = { ["slow-Q4_K_M.gguf"] = weights } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            hub.ChunkDelay = TimeSpan.FromMilliseconds(40);
            var (model, tier) = StandIns.CatalogModelFor(services, "test/slow-GGUF", "rev1", ("slow-Q4_K_M.gguf", weights));
            var install = Task.Run(() => services.InstallModelAsync(model, tier, Ct), Ct);
            Assert.True(await UntilAsync(() => services.Downloads.Jobs.Any(j => j.BytesDone > 100_000)), "the download did not start");

            Assert.True(await services.PauseInstallAsync(model.Repo));
            Assert.Contains(model.Repo, services.State.Paused!);
            var frozen = services.Downloads.Jobs.Single().BytesDone;
            await Task.Delay(400, Ct);
            Assert.Equal(frozen, services.Downloads.Jobs.Single().BytesDone);
            Assert.False(install.IsCompleted);
            Assert.Contains(model.Repo, services.State.Busy);

            Assert.True(services.ResumeInstall(model.Repo));
            Assert.DoesNotContain(model.Repo, services.State.Paused!);
            hub.ChunkDelay = TimeSpan.Zero;
            await install.WaitAsync(TimeSpan.FromSeconds(30), Ct);

            Assert.Equal(weights, await File.ReadAllBytesAsync(services.Store.PathFor(model.Repo, "slow-Q4_K_M.gguf"), Ct));
            Assert.Empty(services.State.Busy);
            Assert.True(services.State.IsInstalled(model, tier));
        }
    }

    [Fact]
    public async Task ACancelledInstallLeavesNoFilesAtAll()
    {
        var first = StandIns.Weights(300_000, 1);
        var second = StandIns.Weights(1_500_000, 2);
        var repo = new HubRepo("test/split-GGUF", "rev1") { Files = { ["split-00001-of-00002.gguf"] = first, ["split-00002-of-00002.gguf"] = second } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            var (model, tier) = StandIns.CatalogModelFor(services, "test/split-GGUF", "rev1", ("split-00001-of-00002.gguf", first), ("split-00002-of-00002.gguf", second));
            hub.ChunkDelay = TimeSpan.FromMilliseconds(40);
            var install = Task.Run(() => services.InstallModelAsync(model, tier, Ct), Ct);
            // The first shard is done and the second is on its way: the case where a half-finished install could linger.
            Assert.True(await UntilAsync(() => services.Downloads.Jobs.Any(j => j.Request.Destination.EndsWith("00002-of-00002.gguf", StringComparison.Ordinal) && j.BytesDone > 100_000)));

            Assert.True(await services.CancelInstallAsync(model.Repo));
            await Assert.ThrowsAsync<InstallCancelledException>(() => install);

            Assert.Empty(FilesUnder(profile.Paths.Data + "/models"));
            Assert.Empty(services.State.Busy);
            Assert.Empty(services.State.Installed);
            Assert.DoesNotContain(services.Downloads.Jobs, j => !j.IsFinished);
        }
    }

    [Fact]
    public async Task APausedInstallCanBeCancelled()
    {
        var weights = StandIns.Weights(1_500_000);
        var repo = new HubRepo("test/slow-GGUF", "rev1") { Files = { ["slow-Q4_K_M.gguf"] = weights } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            hub.ChunkDelay = TimeSpan.FromMilliseconds(40);
            var (model, tier) = StandIns.CatalogModelFor(services, "test/slow-GGUF", "rev1", ("slow-Q4_K_M.gguf", weights));
            var install = Task.Run(() => services.InstallModelAsync(model, tier, Ct), Ct);
            Assert.True(await UntilAsync(() => services.Downloads.Jobs.Any(j => j.BytesDone > 100_000)));
            await services.PauseInstallAsync(model.Repo);

            await services.CancelInstallAsync(model.Repo);
            await Assert.ThrowsAsync<InstallCancelledException>(() => install);

            Assert.Empty(FilesUnder(profile.Paths.Data + "/models"));
            Assert.Empty(services.State.Paused!);
        }
    }

    [Fact]
    public async Task AModelUnderACommunityLicenceNeedsAcceptingFirst()
    {
        var weights = StandIns.Weights(200_000);
        var repo = new HubRepo("test/community-GGUF", "rev1") { License = "llama4", Files = { ["c-Q4_K_M.gguf"] = weights } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            var (real, tier) = StandIns.CatalogModelFor(services, "test/community-GGUF", "rev1", ("c-Q4_K_M.gguf", weights));
            var model = real with { LicenseId = "llama4", LicenseUrl = "https://example.test/llama4/LICENSE" };

            var needed = await services.LicenceNeededAsync(model.Repo, model.Name, model.LicenseId, model.LicenseUrl, model.Repo, Ct);
            Assert.NotNull(needed);
            Assert.Equal("llama4", needed.LicenseId);
            Assert.Equal("https://example.test/llama4/LICENSE", needed.Url);

            var refused = await Assert.ThrowsAsync<LicenceNotAcceptedException>(() => services.InstallModelAsync(model, tier, Ct));
            Assert.Equal("llama4", refused.Request.LicenseId);
            Assert.Empty(FilesUnder(profile.Paths.Data + "/models"));
            Assert.DoesNotContain(hub.Requests, r => r.Contains("/resolve/", StringComparison.Ordinal)); // nothing was fetched

            await services.InstallModelAsync(model, tier, licenceAccepted: true, Ct);
            var manifest = await services.Store.ReadManifestAsync(model.Repo, Ct);
            Assert.NotNull(manifest!.AcceptedAt);
            Assert.Equal("llama4", manifest.LicenseId);
            // Accepted once: the next install of this model does not ask again.
            Assert.Null(await services.LicenceNeededAsync(model.Repo, model.Name, model.LicenseId, model.LicenseUrl, model.Repo, Ct));
        }
    }

    [Theory]
    [InlineData("mit", true)]
    [InlineData("Apache-2.0", true)]
    [InlineData("bsd-3-clause", true)]
    [InlineData("isc", true)]
    [InlineData("gemma", false)]
    [InlineData("llama4", false)]
    [InlineData("other", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyPermissiveLicencesInstallWithoutAsking(string? id, bool permissive) => Assert.Equal(permissive, Licenses.IsPermissive(id));

    [Fact]
    public async Task AGatedRepoAsksForATokenThenForTheLicenceOnTheHub()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 }, "HF_TOKEN is set in this environment");
        var weights = StandIns.Weights(200_000);
        var repo = new HubRepo("test/gated-GGUF", "rev1") { Gated = "manual", Files = { ["g-Q4_K_M.gguf"] = weights } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            var (real, tier) = StandIns.CatalogModelFor(services, "test/gated-GGUF", "rev1", ("g-Q4_K_M.gguf", weights));
            var model = real with { Gated = true };

            await Assert.ThrowsAsync<AuthRequiredException>(() => services.InstallModelAsync(model, tier, Ct));
            Assert.DoesNotContain(hub.Requests, r => r.Contains("/resolve/", StringComparison.Ordinal));

            services.Configure(new ModelOptions(HuggingFaceToken: "hf_wrong"));
            await Assert.ThrowsAsync<GatedRepoException>(() => services.InstallModelAsync(model, tier, Ct));
            Assert.Empty(FilesUnder(profile.Paths.Data + "/models"));

            services.Configure(new ModelOptions(HuggingFaceToken: "hf_good"));
            await services.InstallModelAsync(model, tier, Ct);
            Assert.True(services.State.IsInstalled(model, tier));
            Assert.Contains("Bearer hf_good", hub.Authorizations);
        }
    }

    [Fact]
    public async Task AnyGgufRepoOnTheHubInstallsFromItsVariants()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        var small = new GgufBuilder().Llama(context: 32768, layers: 32, embedding: 4096, heads: 32, kvHeads: 8).Build(fillerBytes: 150_000);
        var big = new GgufBuilder().Llama(context: 32768, layers: 32, embedding: 4096, heads: 32, kvHeads: 8).Build(fillerBytes: 400_000);
        var repo = new HubRepo("someone/Fresh-7B-GGUF", "abc123") { License = "mit", Files = { ["Fresh-7B-Q4_K_M.gguf"] = small, ["Fresh-7B-Q8_0.gguf"] = big, ["README.md"] = [1, 2, 3] } };
        var (hub, services, profile) = await StartAsync(repo, 999);
        await using (hub) await using (services) using (profile)
        {
            var results = await services.SearchHubAsync("fresh", Ct);
            Assert.Equal("someone/Fresh-7B-GGUF", Assert.Single(results).Id);
            Assert.Contains(hub.Requests, r => r.Contains("filter=gguf", StringComparison.Ordinal) && r.Contains("sort=downloads", StringComparison.Ordinal));

            var detail = await services.GetHubRepoAsync(repo.Id, Ct);
            Assert.Equal(["Q4_K_M", "Q8_0"], detail.Variants.Select(v => v.Quant)); // smallest first, the README is not a variant
            Assert.NotNull(detail.Shape);
            Assert.Equal("GGUF header", detail.ShapeSource);
            Assert.Equal(32768, detail.NativeContext);
            Assert.Equal(32, detail.Shape.Layers);

            await services.InstallFromHubAsync(repo.Id, detail.Variants[1], detail.Model, licenceAccepted: false, Ct);
            Assert.Equal(big, await File.ReadAllBytesAsync(services.Store.PathFor(repo.Id, "Fresh-7B-Q8_0.gguf"), Ct));
            Assert.StartsWith("b999-", services.State.Engine?.Id, StringComparison.Ordinal);
            var manifest = await services.Store.ReadManifestAsync(repo.Id, Ct);
            Assert.Equal("abc123", manifest!.Revision);
            Assert.Equal(repo.Id, manifest.SourceRepo);
            Assert.Equal("mit", manifest.LicenseId);
            Assert.Equal(StandIns.Sha(big), manifest.Files.Single().Sha256);
        }
    }

    [Fact]
    public async Task ARepoWhoseHeaderCannotBeReadHasAnUnknownFit()
    {
        var junk = StandIns.Weights(50_000);
        var repo = new HubRepo("someone/Odd-GGUF", "r1") { Files = { ["Odd-Q4_K_M.gguf"] = junk } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            var detail = await services.GetHubRepoAsync(repo.Id, Ct);
            Assert.Single(detail.Variants);
            Assert.Null(detail.Shape);
            Assert.Contains("header", detail.ShapeSource, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UpdateChecksSeeANewUploadAndASupersedingModel()
    {
        var weights = StandIns.Weights(120_000);
        var repo = new HubRepo("test/old-GGUF", "rev1") { Files = { ["o-Q4_K_M.gguf"] = weights } };
        var gone = new HubRepo("test/gone-GGUF", "rev1") { Files = { ["g-Q4_K_M.gguf"] = weights } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            hub.Add(gone);
            var (model, tier) = StandIns.CatalogModelFor(services, "test/old-GGUF", "rev1", ("o-Q4_K_M.gguf", weights));
            await services.InstallModelAsync(model, tier, Ct);

            Assert.Empty((await services.CheckForUpdatesAsync(Ct)).Found); // same commit: nothing to say

            repo.Sha = "rev2";
            Assert.Empty((await services.CheckForUpdatesAsync(Ct)).Found); // the commit moved but no weights changed: not news
            repo.Upload("rev3", "o-Q4_K_M.gguf", StandIns.Weights(120_000, 9), "Retrained on more data");
            var check = await services.CheckForUpdatesAsync(Ct);
            var update = Assert.Single(check.Found);
            Assert.Equal(UpdateKind.NewUpload, update.Kind);
            Assert.Equal("rev3", update.LatestRevision);
            Assert.Equal(["Retrained on more data"], update.Titles);
            Assert.True(update.CanApply);
            Assert.Equal(update, services.State.UpdateFor("test/old-GGUF"));

            // A newer generation in the catalog replaces a model it supersedes, even when the repo has not changed.
            repo.Sha = "rev1";
            var newer = services.State.Catalog!.Catalog.Models.First(m => m.Supersedes.Count > 0);
            var old = newer.Supersedes[0];
            var (olderModel, olderTier) = StandIns.CatalogModelFor(services, "test/gone-GGUF", "rev1", ("g-Q4_K_M.gguf", weights));
            await services.InstallModelAsync(olderModel with { Id = old }, olderTier, Ct);
            check = await services.CheckForUpdatesAsync(Ct);
            var superseded = Assert.Single(check.Found);
            Assert.Equal(UpdateKind.Superseded, superseded.Kind);
            Assert.Contains(newer.Name, superseded.Detail, StringComparison.Ordinal);

            // A repo that cannot be asked is counted, not fatal.
            gone.InfoStatus = 500;
            check = await services.CheckForUpdatesAsync(Ct);
            Assert.Equal(1, check.Failed);
        }
    }

    [Fact]
    public async Task NewInstallsGoToTheChosenFolderAndOldOnesStayListed()
    {
        var weights = StandIns.Weights(100_000);
        var repo = new HubRepo("test/a-GGUF", "r") { Files = { ["a-Q4_K_M.gguf"] = weights } };
        var repo2 = new HubRepo("test/b-GGUF", "r") { Files = { ["b-Q4_K_M.gguf"] = weights } };
        var (hub, services, profile) = await StartAsync(repo);
        await using (hub) await using (services) using (profile)
        {
            hub.Add(repo2);
            var elsewhere = Path.Combine(profile.Root, "big-drive");
            var (a, tierA) = StandIns.CatalogModelFor(services, "test/a-GGUF", "r", ("a-Q4_K_M.gguf", weights));
            await services.InstallModelAsync(a, tierA, Ct);

            services.Configure(new ModelOptions(StoreLocation: elsewhere));
            var (b, tierB) = StandIns.CatalogModelFor(services, "test/b-GGUF", "r", ("b-Q4_K_M.gguf", weights));
            await services.InstallModelAsync(b, tierB, Ct);

            Assert.True(File.Exists(Path.Combine(elsewhere, "test", "b-GGUF", "b-Q4_K_M.gguf")));
            Assert.True(File.Exists(Path.Combine(profile.Paths.Data, "models", "test", "a-GGUF", "a-Q4_K_M.gguf")));
            Assert.Equal(["test/a-GGUF", "test/b-GGUF"], services.State.Installed.Select(m => m.Id));

            // Each is deleted from where it lives.
            await services.DeleteAsync("test/a-GGUF", Ct);
            Assert.False(File.Exists(Path.Combine(profile.Paths.Data, "models", "test", "a-GGUF", "a-Q4_K_M.gguf")));
            Assert.Equal(["test/b-GGUF"], services.State.Installed.Select(m => m.Id));

            // Choosing another folder later does not lose the one in between (the app remembers where models went).
            services.Configure(new ModelOptions(StoreLocation: Path.Combine(profile.Root, "third")));
            await services.RescanAsync(Ct);
            Assert.Equal(["test/b-GGUF"], services.State.Installed.Select(m => m.Id));
            await using var again = StandIns.Services(profile, hub.Server);
            await again.InitializeAsync(allowNetwork: false, Ct);
            Assert.Equal(["test/b-GGUF"], again.State.Installed.Select(m => m.Id));
        }
    }

    [Fact]
    public async Task TheSpeedCapReachesTheDownloadManager()
    {
        using var profile = new TempProfile();
        await using var services = new ModelServices(profile.Paths, Wicked.Core.Http.WickedHttp.CreateClient(offline: () => false));
        services.Configure(new ModelOptions(BandwidthBytesPerSecond: 5 * 1_048_576));
        Assert.Equal(5 * 1_048_576, services.Downloads.BandwidthLimit);
        services.Configure(new ModelOptions());
        Assert.Null(services.Downloads.BandwidthLimit);
    }
}
