using System.Diagnostics;
using Wicked.Core.Chat;
using Wicked.Core.Models;

namespace Wicked.Tests.Chat;

public sealed class ModelRuntimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ModelsAreListedUnloadedAndNothingStartsUntilALoadIsAsked()
    {
        await using var rig = await RuntimeRig.StartAsync(RuntimeRig.Chat, RuntimeRig.Coder);

        Assert.Equal([RuntimeRig.ChatId, RuntimeRig.CoderId], rig.Runtime.Models.Select(m => m.Id));
        Assert.All(rig.Runtime.Models, m => Assert.Equal(RunState.Unloaded, m.State));
        Assert.Equal("Chat (Q4_K_M)", rig.Model(RuntimeRig.ChatId).DisplayName);
        Assert.Null(rig.Runtime.Router);
        Assert.Empty(rig.FakeLog());
        Assert.False(File.Exists(rig.Runtime.PresetPath));
    }

    [Fact]
    public async Task ALoadStartsTheRouterWithThePresetAndReportsLoadingThenLoaded()
    {
        await using var rig = await RuntimeRig.StartAsync();
        var seen = new List<(RunState State, double? Progress)>();
        rig.Runtime.Changed += (_, _) =>
        {
            var m = rig.Runtime.Models.Single();
            lock (seen) seen.Add((m.State, m.LoadProgress));
        };
        rig.Script(new { loadMs = 300 });

        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

        Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.ChatId).State);
        await RuntimeRig.WaitUntilAsync(() => { lock (seen) return seen.Count(s => s.State == RunState.Loaded) >= 1 && seen.Any(s => s.State == RunState.Loading); });
        lock (seen)
        {
            Assert.True(seen.FindIndex(s => s.State == RunState.Loading) < seen.FindIndex(s => s.State == RunState.Loaded));
            Assert.Contains(seen, s => s is { State: RunState.Loading, Progress: 0.5 }); // the router's own progress events
        }

        var start = Assert.Single(rig.Logged("start"));
        var args = start.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal(rig.Runtime.PresetPath, args[args.IndexOf("--models-preset") + 1]);
        Assert.Equal("127.0.0.1", args[args.IndexOf("--host") + 1]);
        Assert.Equal("1", args[args.IndexOf("--models-max") + 1]);
        Assert.Contains("--no-models-autoload", args);
        Assert.DoesNotContain("--models-dir", args);
        var preset = await File.ReadAllTextAsync(rig.Runtime.PresetPath, Ct);
        Assert.Equal(preset, start.GetProperty("preset").GetString());
        Assert.Contains($"[{RuntimeRig.ChatId}]\n", preset, StringComparison.Ordinal);
        Assert.Contains("n-gpu-layers = 0\n", preset, StringComparison.Ordinal); // a CPU engine
        Assert.Contains("jinja = true\n", preset, StringComparison.Ordinal);
        Assert.Single(rig.Logged("load"));

        // The context is what the engine says it runs with (the placeholder file has no header to plan from).
        await RuntimeRig.WaitUntilAsync(() => rig.Model(RuntimeRig.ChatId).ContextSize == 4096);
    }

    [Fact]
    public async Task LoadingALoadedModelDoesNothingAndUnloadingReturnsItToUnloaded()
    {
        await using var rig = await RuntimeRig.StartAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        Assert.Single(rig.Logged("load"));

        await rig.Runtime.UnloadAsync(RuntimeRig.ChatId, Ct);
        Assert.Equal(RunState.Unloaded, rig.Model(RuntimeRig.ChatId).State);
        Assert.Single(rig.Logged("unload"));
        await rig.Runtime.UnloadAsync(RuntimeRig.ChatId, Ct); // already unloaded
        Assert.Single(rig.Logged("unload"));
    }

    [Fact]
    public async Task AnUnknownModelIsRefusedWithoutStartingAnything()
    {
        await using var rig = await RuntimeRig.StartAsync();

        var ex = await Assert.ThrowsAsync<ChatException>(() => rig.Runtime.LoadAsync("nobody/home:Q4", Ct));

        Assert.Contains("not an installed model", ex.Message, StringComparison.Ordinal);
        Assert.Null(rig.Runtime.Router);
    }

    [Fact]
    public async Task AFailedLoadIsFailedWithTheTailOfTheRouterLogAndTheOthersStillLoad()
    {
        await using var rig = await RuntimeRig.StartAsync(RuntimeRig.Chat, RuntimeRig.Coder);
        rig.Script(new { failLoad = new[] { RuntimeRig.CoderId } });

        var ex = await Assert.ThrowsAsync<ChatException>(() => rig.Runtime.LoadAsync(RuntimeRig.CoderId, Ct));

        Assert.Contains("fake failure for test/Coder:Q4_K_M", ex.Message, StringComparison.Ordinal);
        var failed = rig.Model(RuntimeRig.CoderId);
        Assert.Equal(RunState.Failed, failed.State);
        Assert.Contains("exit code 1", failed.Error, StringComparison.Ordinal);
        Assert.Contains("fake failure", failed.Error, StringComparison.Ordinal);
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.ChatId).State);

        // Fixed, a second try clears the failure.
        rig.Script(new { });
        await rig.Runtime.LoadAsync(RuntimeRig.CoderId, Ct);
        Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.CoderId).State);
        Assert.Null(rig.Model(RuntimeRig.CoderId).Error);
    }

    [Fact]
    public async Task ARouterThatDiesIsRestartedAndTheStatesAreReadAgain()
    {
        await using var rig = await RuntimeRig.StartAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        var first = rig.Runtime.Router!.ProcessId!.Value;

        Process.GetProcessById(first).Kill();

        await RuntimeRig.WaitUntilAsync(() => rig.Logged("start").Count == 2);
        await RuntimeRig.WaitUntilAsync(() => rig.Model(RuntimeRig.ChatId).State == RunState.Unloaded);
        Assert.NotEqual(first, rig.Runtime.Router!.ProcessId);
        await RuntimeRig.WaitUntilAsync(() => rig.Runtime.Router!.IsRunning);

        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct); // the same runtime object works again, no new start by hand
        Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.ChatId).State);
        Assert.Equal(2, rig.Logged("start").Count);
    }

    [Fact]
    public async Task ARouterThatDiesWhileLoadingFailsTheLoad()
    {
        await using var rig = await RuntimeRig.StartAsync();
        rig.Script(new { loadMs = 5000 });
        var load = rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        await RuntimeRig.WaitUntilAsync(() => load.IsFaulted || rig.Logged("load").Count == 1); // the engine has the request
        Assert.False(load.IsFaulted, load.Exception?.ToString());

        Process.GetProcessById(rig.Runtime.Router!.ProcessId!.Value).Kill();

        var ex = await Assert.ThrowsAsync<ChatException>(() => load);
        Assert.Contains("stopped while loading", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RunState.Failed, rig.Model(RuntimeRig.ChatId).State);
    }

    [Fact]
    public async Task InstallingAModelRewritesThePresetAndTellsTheRunningRouter()
    {
        await using var rig = await RuntimeRig.StartAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        Assert.DoesNotContain("test/Coder", await File.ReadAllTextAsync(rig.Runtime.PresetPath, Ct), StringComparison.Ordinal);

        rig.AddModel(RuntimeRig.Coder);
        await rig.RescanAsync();

        await RuntimeRig.WaitUntilAsync(() => rig.Runtime.Models.Count == 2);
        await RuntimeRig.WaitUntilAsync(() => rig.Logged("reload").Count == 1);
        Assert.Contains($"[{RuntimeRig.CoderId}]", await File.ReadAllTextAsync(rig.Runtime.PresetPath, Ct), StringComparison.Ordinal);
        Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.ChatId).State);
        await rig.Runtime.LoadAsync(RuntimeRig.CoderId, Ct); // the router knows the new model
        Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.CoderId).State);

        // Deleting one takes it out again.
        await rig.Runtime.UnloadAsync(RuntimeRig.CoderId, Ct);
        await rig.Services.DeleteAsync(RuntimeRig.Coder, Ct);
        await RuntimeRig.WaitUntilAsync(() => rig.Runtime.Models.Count == 1);
        await RuntimeRig.WaitUntilAsync(() => !File.ReadAllText(rig.Runtime.PresetPath).Contains("test/Coder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SavingSettingsRewritesThePresetKeepsThemAcrossRestartsAndReloadsALoadedModel()
    {
        var settings = new ModelRunSettings(ContextSize: 2048, KvCacheType: "q8_0", Reasoning: false);
        await using (var rig = await RuntimeRig.StartAsync())
        {
            Assert.Equal(new ModelRunSettings(), rig.Runtime.GetSettings(RuntimeRig.ChatId));
            await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

            await rig.Runtime.SaveSettingsAsync(RuntimeRig.ChatId, settings, Ct);

            Assert.Equal(settings, rig.Runtime.GetSettings(RuntimeRig.ChatId));
            var preset = await File.ReadAllTextAsync(rig.Runtime.PresetPath, Ct);
            Assert.Contains("c = 2048\n", preset, StringComparison.Ordinal);
            Assert.Contains("cache-type-k = q8_0\n", preset, StringComparison.Ordinal);
            Assert.Contains("reasoning-budget = 0\n", preset, StringComparison.Ordinal);
            Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.ChatId).State);
            Assert.Equal(2, rig.Logged("load").Count); // reloaded for the change to take effect
            Assert.Single(rig.Logged("unload"));
            await RuntimeRig.WaitUntilAsync(() => rig.Model(RuntimeRig.ChatId).ContextSize == 2048);

            await rig.Runtime.SaveSettingsAsync(RuntimeRig.ChatId, settings, Ct); // unchanged: no reload
            Assert.Equal(2, rig.Logged("load").Count);

            // Kept in a file in the config folder.
            Assert.True(File.Exists(Path.Combine(rig.Paths.Config, "model-run-settings.json")));
            await using var again = new ModelServices(rig.Paths);
            await again.Runtime.InitializeAsync(Ct);
            Assert.Equal(settings, again.Runtime.GetSettings(RuntimeRig.ChatId));
        }
    }

    [Fact]
    public async Task SettingsThatCannotWorkAreRefusedBeforeAnythingIsWritten()
    {
        await using var rig = await RuntimeRig.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => rig.Runtime.SaveSettingsAsync(RuntimeRig.ChatId, new ModelRunSettings(KvCacheType: "q3_k"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Runtime.SaveSettingsAsync(RuntimeRig.ChatId, new ModelRunSettings(ContextSize: 0), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Runtime.SaveSettingsAsync(RuntimeRig.ChatId, new ModelRunSettings(GpuLayers: "lots"), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Runtime.SaveSettingsAsync(RuntimeRig.ChatId, new ModelRunSettings(DraftModelId: RuntimeRig.ChatId), Ct));

        Assert.Equal(new ModelRunSettings(), rig.Runtime.GetSettings(RuntimeRig.ChatId));
        Assert.False(File.Exists(Path.Combine(rig.Paths.Config, "model-run-settings.json")));
    }

    [Fact]
    public async Task TheModelsMaxChoiceRestartsTheRouterWithIt()
    {
        await using var rig = await RuntimeRig.StartAsync(RuntimeRig.Chat, RuntimeRig.Coder);
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

        await rig.Runtime.SetModelsMaxAsync(3, Ct);

        Assert.Equal(3, rig.Runtime.ModelsMax);
        Assert.Equal(RunState.Unloaded, rig.Model(RuntimeRig.ChatId).State);
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        var starts = rig.Logged("start");
        Assert.Equal(2, starts.Count);
        var args = starts[1].GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
        Assert.Equal("3", args[args.IndexOf("--models-max") + 1]);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => rig.Runtime.SetModelsMaxAsync(0, Ct));
    }

    [Fact]
    public async Task ChangedRunsOnAWorkerSoASlowListenerNeverHoldsUpTheCaller()
    {
        await using var rig = await RuntimeRig.StartAsync();
        // The listener blocks until the load is over: a load that waited on it would only end when the hold times out.
        using var hold = new ManualResetEventSlim();
        rig.Runtime.Changed += (_, _) => hold.Wait(TimeSpan.FromSeconds(20));
        var watch = Stopwatch.StartNew();

        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        hold.Set();

        Assert.Equal(RunState.Loaded, rig.Model(RuntimeRig.ChatId).State);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"the load waited on a listener: {watch.Elapsed}");
    }

    [Fact]
    public async Task DisposingStopsTheRouter()
    {
        var rig = await RuntimeRig.StartAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        var pid = rig.Runtime.Router!.ProcessId!.Value;

        await rig.DisposeAsync();

        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
    }
}
