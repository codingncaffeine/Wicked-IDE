using System.Text.Json;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Core.Models;
using Wicked.Core.Store;
using Wicked.Tests.Chat;

namespace Wicked.Tests.Models;

/// <summary>
/// Updating a model's files safely: the new file arrives beside the old, is swapped in when verified, and the old one stays until
/// the new one has answered a prompt through the router; a bad upload (planted in the fake engine as a marker at the head of
/// the file) puts the old one back.
/// </summary>
public sealed class AutoUpdateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Repo = "test/Swap-GGUF";
    private const string File1 = "Swap-Q4_K_M.gguf";
    private const string RunId = Repo + ":Q4_K_M";

    private static byte[] Bytes(byte seed, string marker = "") => [.. System.Text.Encoding.ASCII.GetBytes(marker), .. StandIns.Weights(60_000, seed)];

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempProfile _profile = new();

        public required HubStandIn Hub { get; init; }

        public required ModelServices Services { get; init; }

        public required string EngineFolder { get; init; }

        public HubRepo Repo => Hub.Repo(AutoUpdateTests.Repo);

        public NoticeBoard Board { get; } = new();

        public string ModelFile => Path.Combine(Services.Store.Root, "test", "Swap-GGUF", File1);

        public static async Task<Rig> StartAsync(byte[]? initial = null)
        {
            Assert.SkipUnless(StandIns.CanRunFakeRouter, "needs a shell for the fake engine wrapper");
            var repo = new HubRepo(AutoUpdateTests.Repo, "rev1") { Files = { [File1] = initial ?? Bytes(1) } };
            repo.Commits.Add(("rev1", "First upload"));
            var hub = await HubStandIn.StartAsync(app => StandIns.MapGitHub(app, 999), repo);
            var holder = new TempProfile();
            var services = StandIns.Services(holder, hub.Server);
            services.Configure(new ModelOptions(Backend: EngineBackendKind.Cpu));
            services.Runtime.Tuning = new RuntimeTuning { RestartDelayBase = TimeSpan.FromMilliseconds(50), PollInterval = TimeSpan.FromMilliseconds(300), HealthTimeout = TimeSpan.FromSeconds(30) };
            await services.InitializeAsync(allowNetwork: false, Ct);
            var engine = await services.EnsureEngineAsync(Ct);
            var detail = await services.GetHubRepoAsync(AutoUpdateTests.Repo, Ct);
            await services.InstallFromHubAsync(AutoUpdateTests.Repo, detail.Variants.Single(), detail.Model, licenceAccepted: true, Ct);
            var rig = new Rig { Hub = hub, Services = services, EngineFolder = Path.GetDirectoryName(engine.ServerPath)!, Holder = holder };
            services.Updates.Notices = rig.Board;
            rig.Script(new { reply = "ok then" });
            await RuntimeRig.WaitUntilAsync(() => services.Runtime.Models.Count == 1);
            return rig;
        }

        public required TempProfile Holder { get; init; }

        public void Script(object script) => File.WriteAllText(Path.Combine(EngineFolder, "fake.json"), JsonSerializer.Serialize(script));

        public List<JsonElement> Logged(string kind)
        {
            var path = Path.Combine(EngineFolder, "fake-log.jsonl");
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("t").GetString() == kind)];
        }

        public int Downloads(string revision) => Hub.CountOf($"/{AutoUpdateTests.Repo}/resolve/{revision}/{File1}");

        public async Task<ModelUpdate> FindUpdateAsync()
        {
            await Services.CheckForUpdatesAsync(Ct);
            return Services.State.UpdateFor(AutoUpdateTests.Repo)!;
        }

        public ModelManifest Manifest => Services.Store.ReadManifestAsync(AutoUpdateTests.Repo, Ct).GetAwaiter().GetResult()!;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            await Hub.DisposeAsync();
            Holder.Dispose();
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task AnUpdateIsDownloadedBesideTheOldFileSwappedInAndAnsweredWithBeforeTheOldOneGoes()
    {
        await using var rig = await Rig.StartAsync();
        var fresh = Bytes(2);
        rig.Hub.ChunkDelay = TimeSpan.Zero;
        rig.Repo.Upload("rev2", File1, fresh, "Better quantisation");
        await rig.FindUpdateAsync();
        rig.Script(new { reply = "one two three four five six seven eight nine ten", streamGapMs = 150 });

        var running = rig.Services.UpdateModelAsync(Repo, ct: Ct);

        // While the new file is answering its prompt, both are on disk: the new one in place, the old one kept.
        await RuntimeRig.WaitUntilAsync(() => rig.Logged("chat").Count == 1 && File.Exists(rig.ModelFile + ".previous"));
        Assert.Equal(fresh, await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile + ".previous", Ct));
        Assert.False(File.Exists(rig.ModelFile + ".update"));

        var outcome = await running;

        Assert.Equal(UpdateResult.Updated, outcome.Result);
        Assert.Equal("rev1", outcome.FromRevision);
        Assert.Equal("rev2", outcome.ToRevision);
        Assert.False(File.Exists(rig.ModelFile + ".previous")); // only now
        Assert.Equal(fresh, await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        var manifest = rig.Manifest;
        Assert.Equal("rev2", manifest.Revision);
        Assert.Equal(StandIns.Sha(fresh), manifest.Files.Single().Sha256);
        Assert.Equal(1, rig.Downloads("rev2"));
        Assert.Null(rig.Services.State.UpdateFor(Repo));
        Assert.Equal(RunState.Unloaded, rig.Services.Runtime.Models.Single().State); // it was not loaded before, so it is not left loaded
        Assert.Empty((await rig.Services.CheckForUpdatesAsync(Ct)).Found);
    }

    [Fact(Timeout = 90_000)]
    public async Task ANewFileThatErrorsOnItsFirstPromptIsRolledBackAndNotTriedAgain()
    {
        await using var rig = await Rig.StartAsync();
        var bad = Bytes(2, "FAILCHAT");
        rig.Repo.Upload("rev2", File1, bad, "Broken upload");
        await rig.FindUpdateAsync();

        var outcome = await rig.Services.UpdateModelAsync(Repo, ct: Ct);

        Assert.Equal(UpdateResult.RolledBack, outcome.Result);
        Assert.Contains("did not work", outcome.Message, StringComparison.Ordinal);
        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        Assert.False(File.Exists(rig.ModelFile + ".previous"));
        Assert.False(File.Exists(rig.ModelFile + ".update"));
        Assert.Equal("rev1", rig.Manifest.Revision);
        Assert.Equal(StandIns.Sha(Bytes(1)), rig.Manifest.Files.Single().Sha256);
        Assert.Equal(RunState.Unloaded, rig.Services.Runtime.Models.Single().State);

        // The old file still works.
        rig.Script(new { reply = "fine" });
        await rig.Services.Runtime.LoadAsync(RunId, Ct);
        Assert.Equal(RunState.Loaded, rig.Services.Runtime.Models.Single().State);
    }

    [Fact(Timeout = 90_000)]
    public async Task ANewFileThatFailsToLoadIsRolledBack()
    {
        await using var rig = await Rig.StartAsync();
        rig.Repo.Upload("rev2", File1, Bytes(2, "FAILLOAD"), "Broken upload");
        await rig.FindUpdateAsync();

        var outcome = await rig.Services.UpdateModelAsync(Repo, ct: Ct);

        Assert.Equal(UpdateResult.RolledBack, outcome.Result);
        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        Assert.Equal("rev1", rig.Manifest.Revision);
        await rig.Services.Runtime.LoadAsync(RunId, Ct); // the restored file loads
    }

    [Fact(Timeout = 90_000)]
    public async Task ADownloadThatDoesNotMatchTheHubsHashChangesNothing()
    {
        await using var rig = await Rig.StartAsync();
        rig.Repo.Upload("rev2", File1, Bytes(2), "New");
        var update = await rig.FindUpdateAsync();
        // The Hub's tree promises one hash and serves other bytes.
        rig.Repo.Files[File1] = Bytes(3);
        Assert.NotEqual(update.Files.Single().Sha256, StandIns.Sha(Bytes(3)));

        await Assert.ThrowsAnyAsync<IOException>(() => rig.Services.UpdateModelAsync(Repo, ct: Ct));

        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        Assert.False(File.Exists(rig.ModelFile + ".update"));
        Assert.Equal("rev1", rig.Manifest.Revision);
    }

    [Fact(Timeout = 90_000)]
    public async Task AutomaticModelsAreUpdatedByTheSweepAndNobodyIsAskedAboutThem()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Services.SetUpdatePolicyAsync(Repo, UpdatePolicy.Auto, Ct);
        rig.Repo.Upload("rev2", File1, Bytes(2), "New");

        var sweep = await rig.Services.Updates.CheckNowAsync(Ct);

        var applied = Assert.Single(sweep.Applied);
        Assert.Equal(UpdateResult.Updated, applied.Result);
        Assert.Equal("rev2", rig.Manifest.Revision);
        Assert.Equal(NoticeKind.Success, rig.Board.All.Single(n => n.Key == "model-update:" + Repo).Kind);
        Assert.DoesNotContain(rig.Board.All, n => n.Kind == NoticeKind.Action);
    }

    [Fact(Timeout = 90_000)]
    public async Task AnAutomaticUpdateThatRolledBackIsReportedAndNeverRepeatedForTheSameUpload()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Services.SetUpdatePolicyAsync(Repo, UpdatePolicy.Auto, Ct);
        rig.Repo.Upload("rev2", File1, Bytes(2, "FAILCHAT"), "Broken");

        var first = await rig.Services.Updates.CheckNowAsync(Ct);
        var second = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.Equal(UpdateResult.RolledBack, Assert.Single(first.Applied).Result);
        var notice = rig.Board.All.Single(n => n.Key == "model-update:" + Repo);
        // After the first sweep it is an error; after the second the upload is offered by hand, with the old one kept.
        Assert.Empty(second.Applied);
        Assert.Equal(1, rig.Downloads("rev2"));
        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        Assert.Equal(NoticeKind.Action, rig.Board.All.Single(n => n.Key == "model-update:" + Repo).Kind);
        Assert.Contains("newer upload", rig.Board.All.Single(n => n.Key == "model-update:" + Repo).Text, StringComparison.Ordinal);
        Assert.NotNull(notice);
    }

    [Fact(Timeout = 90_000)]
    public async Task AModelThatIsLoadedIsLeftAloneUntilItIsNot()
    {
        await using var rig = await Rig.StartAsync();
        await rig.Services.SetUpdatePolicyAsync(Repo, UpdatePolicy.Auto, Ct);
        await rig.Services.Runtime.LoadAsync(RunId, Ct);
        rig.Repo.Upload("rev2", File1, Bytes(2), "New");

        var busy = await rig.Services.Updates.CheckNowAsync(Ct);

        Assert.Equal(UpdateResult.Deferred, Assert.Single(busy.Applied).Result);
        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile, Ct));

        await rig.Services.Runtime.UnloadAsync(RunId, Ct);
        var idle = await rig.Services.Updates.CheckNowAsync(Ct);
        Assert.Equal(UpdateResult.Updated, Assert.Single(idle.Applied).Result);
    }

    [Fact(Timeout = 90_000)]
    public async Task AnUpdateTheRepoMadeByRenamingFilesIsNotAppliedBlind()
    {
        await using var rig = await Rig.StartAsync();
        rig.Repo.Files.Remove(File1);
        rig.Repo.Files["Swap-v2-Q4_K_M.gguf"] = Bytes(2);
        rig.Repo.Commits.Insert(0, ("rev2", "Renamed"));
        rig.Repo.Sha = "rev2";
        var update = await rig.FindUpdateAsync();

        var outcome = await rig.Services.UpdateModelAsync(Repo, ct: Ct);

        Assert.False(update.CanApply);
        Assert.Equal(UpdateResult.NothingToDo, outcome.Result);
        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile, Ct));
    }

    [Fact(Timeout = 90_000)]
    public async Task AnUpdateInterruptedAfterTheSwapIsJudgedAtTheNextStartAndRolledBackWhenBad()
    {
        await using var rig = await Rig.StartAsync();
        var store = rig.Services.Store;
        var oldManifest = JsonSerializer.Serialize(rig.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        // The state a crash leaves: the new (bad) file in place, the old one beside it, the journal naming both.
        File.Move(rig.ModelFile, rig.ModelFile + ".previous");
        await File.WriteAllBytesAsync(rig.ModelFile, Bytes(2, "FAILCHAT"), Ct);
        await store.RegisterAsync(Repo, Repo, "rev2", [(File1, StandIns.Sha(Bytes(2, "FAILCHAT")))], ct: Ct);
        var journal = Path.Combine(rig.Holder.Paths.State, "updates-pending.json");
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(new[] { new UpdateJournalEntry { ModelId = Repo, StoreRoot = store.Root, FromRevision = "rev1", ToRevision = "rev2", Files = [File1], OldManifest = oldManifest } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Ct);
        await rig.Services.RescanAsync(Ct);

        await rig.Services.RecoverUpdatesAsync(Ct);

        Assert.Equal(Bytes(1), await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        Assert.False(File.Exists(rig.ModelFile + ".previous"));
        Assert.Equal("rev1", rig.Manifest.Revision);
        Assert.Equal("[]", (await File.ReadAllTextAsync(journal, Ct)).Replace("\n", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal));
    }

    [Fact(Timeout = 90_000)]
    public async Task AnInterruptedUpdateWhoseNewFileWorksIsFinished()
    {
        await using var rig = await Rig.StartAsync();
        var store = rig.Services.Store;
        var oldManifest = JsonSerializer.Serialize(rig.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        File.Move(rig.ModelFile, rig.ModelFile + ".previous");
        await File.WriteAllBytesAsync(rig.ModelFile, Bytes(2), Ct);
        await store.RegisterAsync(Repo, Repo, "rev2", [(File1, StandIns.Sha(Bytes(2)))], ct: Ct);
        await File.WriteAllTextAsync(Path.Combine(rig.Holder.Paths.State, "updates-pending.json"),
            JsonSerializer.Serialize(new[] { new UpdateJournalEntry { ModelId = Repo, StoreRoot = store.Root, FromRevision = "rev1", ToRevision = "rev2", Files = [File1], OldManifest = oldManifest } }, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Ct);
        await rig.Services.RescanAsync(Ct);

        await rig.Services.RecoverUpdatesAsync(Ct);

        Assert.Equal(Bytes(2), await File.ReadAllBytesAsync(rig.ModelFile, Ct));
        Assert.False(File.Exists(rig.ModelFile + ".previous"));
        Assert.Equal("rev2", rig.Manifest.Revision);
    }
}
