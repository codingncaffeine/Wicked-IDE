using System.Diagnostics;
using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Tests.Chat;

namespace Wicked.Tests;

public sealed class RouterOwnershipTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Timeout = 60_000)]
    public async Task ASecondProcessAttachesToTheRunningRouterInsteadOfStartingAnother()
    {
        await using var rig = await RuntimeRig.StartAsync();
        var other = await rig.OtherAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

        await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

        Assert.IsType<RouterProcess>(rig.Runtime.Router);
        var attached = Assert.IsType<AttachedRouter>(other.Runtime.Router);
        Assert.Equal(rig.Runtime.Router!.ProcessId, attached.ProcessId);
        Assert.Single(rig.Logged("start")); // one router for the profile
        Assert.Equal(RunState.Loaded, other.Runtime.Models.Single().State);
        var info = RouterOwnership.Read(rig.Paths)!;
        Assert.Equal(Environment.ProcessId, info.OwnerPid);
        Assert.Equal(rig.Runtime.Router.ProcessId, info.RouterPid);
        Assert.Equal(rig.Runtime.Router.BaseAddress.Port, info.Port);
        Assert.Equal(rig.Services.Engines.Current!.Id, info.EngineTag);
    }

    [Fact(Timeout = 60_000)]
    public async Task TheAttachedProcessFollowsTheModelsTheOwnerLoadsAndUnloads()
    {
        await using var rig = await RuntimeRig.StartAsync();
        var other = await rig.OtherAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

        await rig.Runtime.UnloadAsync(RuntimeRig.ChatId, Ct);

        await RuntimeRig.WaitUntilAsync(() => other.Runtime.Models.Single().State == RunState.Unloaded);
        await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct); // the attached side can load through the owner's router too
        await RuntimeRig.WaitUntilAsync(() => rig.Runtime.Models.Single().State == RunState.Loaded);
        Assert.Single(rig.Logged("start"));
    }

    [Fact(Timeout = 60_000)]
    public async Task WhenTheOwnerStopsTheNextProcessThatNeedsTheRouterTakesOver()
    {
        await using var rig = await RuntimeRig.StartAsync();
        var other = await rig.OtherAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        var exited = new TaskCompletionSource();
        other.Runtime.Router!.Exited += (_, _) => exited.TrySetResult();

        await rig.Services.Runtime.DisposeAsync(); // the owner goes: its router stops and the lock is released

        await exited.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
        Assert.False(RouterOwnership.IsHeld(rig.Paths));
        await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        Assert.IsType<RouterProcess>(other.Runtime.Router);
        Assert.Equal(RunState.Loaded, other.Runtime.Models.Single().State);
        Assert.Equal(2, rig.Logged("start").Count);
        Assert.Equal(Environment.ProcessId, RouterOwnership.Read(rig.Paths)!.OwnerPid);
        Assert.True(RouterOwnership.IsHeld(rig.Paths));
    }

    [Fact(Timeout = 60_000)]
    public async Task ARouterItsOwnerLeftRunningIsTakenOverNotStartedTwice()
    {
        await using var rig = await RuntimeRig.StartAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        var engine = rig.Services.Engines.Current!;
        var modelsMax = rig.Runtime.Plan!.ModelsMax;
        await rig.Runtime.UnloadAsync(RuntimeRig.ChatId, Ct);
        await rig.Services.Runtime.DisposeAsync(); // the lock is free, the preset is on disk
        using var orphan = StartOrphan(rig, engine.ServerPath, out var port);
        try
        {
            await WaitHealthyAsync(port);
            Directory.CreateDirectory(rig.Paths.Runtime);
            await File.WriteAllTextAsync(RouterOwnership.InfoPath(rig.Paths),
                $$"""{"port":{{port}},"routerPid":{{orphan.Id}},"ownerPid":999999,"engineTag":"{{engine.Id}}","modelsMax":{{modelsMax}},"startedAt":"2026-10-20T10:00:00Z"}""", Ct);
            var other = await rig.OtherAsync();

            await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

            var attached = Assert.IsType<AttachedRouter>(other.Runtime.Router);
            Assert.Equal(orphan.Id, attached.ProcessId);
            Assert.True(RouterOwnership.IsHeld(rig.Paths)); // it took the ownership with the router
            Assert.Equal(Environment.ProcessId, RouterOwnership.Read(rig.Paths)!.OwnerPid);
            Assert.Equal(RunState.Loaded, other.Runtime.Models.Single().State);

            // The adopted router dies; its new owner starts one of its own on the next need.
            var exited = new TaskCompletionSource();
            attached.Exited += (_, _) => exited.TrySetResult();
            orphan.Kill(entireProcessTree: true);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(20), Ct);
            await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
            Assert.IsType<RouterProcess>(other.Runtime.Router);
            Assert.Equal(RunState.Loaded, other.Runtime.Models.Single().State);
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task ARouterLeftByAnotherEngineIsEndedBeforeTheNewOneStarts()
    {
        await using var rig = await RuntimeRig.StartAsync();
        await rig.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);
        var engine = rig.Services.Engines.Current!;
        await rig.Runtime.UnloadAsync(RuntimeRig.ChatId, Ct);
        await rig.Services.Runtime.DisposeAsync();
        using var orphan = StartOrphan(rig, engine.ServerPath, out var port);
        try
        {
            await WaitHealthyAsync(port);
            await File.WriteAllTextAsync(RouterOwnership.InfoPath(rig.Paths),
                $$"""{"port":{{port}},"routerPid":{{orphan.Id}},"ownerPid":999999,"engineTag":"b1-older","modelsMax":1,"startedAt":"2026-10-20T10:00:00Z"}""", Ct);
            var other = await rig.OtherAsync();

            await other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct);

            Assert.IsType<RouterProcess>(other.Runtime.Router);
            Assert.True(orphan.WaitForExit(10_000), "the router of the other engine was left running");
            Assert.Equal(RunState.Loaded, other.Runtime.Models.Single().State);
        }
        finally
        {
            if (!orphan.HasExited) orphan.Kill(entireProcessTree: true);
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task AProcessThatCannotFindARouterInTimeSaysSoInsteadOfWaitingForever()
    {
        await using var rig = await RuntimeRig.StartAsync();
        var other = await rig.OtherAsync();
        other.Runtime.Tuning = other.Runtime.Tuning with { AttachTimeout = TimeSpan.FromMilliseconds(600) };
        using var held = RouterOwnership.TryAcquire(rig.Paths); // an owner that never publishes a router
        Assert.NotNull(held);

        var ex = await Assert.ThrowsAsync<ChatException>(() => other.Runtime.LoadAsync(RuntimeRig.ChatId, Ct));

        Assert.Contains("did not answer in time", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockIsExclusiveAndAProbeLeavesTheOwnersFileAlone()
    {
        using var profile = new TempProfile();
        using var owner = RouterOwnership.TryAcquire(profile.Paths);
        Assert.NotNull(owner);
        owner.Publish(new RouterRuntimeInfo(1234, 55, Environment.ProcessId, "b1-cpu", 2, DateTimeOffset.UtcNow));

        Assert.Null(RouterOwnership.TryAcquire(profile.Paths));
        Assert.True(RouterOwnership.IsHeld(profile.Paths));
        Assert.Equal(1234, RouterOwnership.Read(profile.Paths)!.Port);

        owner.Dispose();
        Assert.False(File.Exists(RouterOwnership.InfoPath(profile.Paths)));
        Assert.False(RouterOwnership.IsHeld(profile.Paths));
    }

    [Fact]
    public void AProbeOfAFreeLockDoesNotDeleteAFileItNeverWrote()
    {
        using var profile = new TempProfile();
        profile.Paths.EnsureCreated();
        File.WriteAllText(RouterOwnership.InfoPath(profile.Paths), """{"port":9,"routerPid":1,"ownerPid":2,"modelsMax":1,"startedAt":"2026-10-20T10:00:00Z"}""");

        Assert.False(RouterOwnership.IsHeld(profile.Paths));

        Assert.True(File.Exists(RouterOwnership.InfoPath(profile.Paths)));
    }

    private static Process StartOrphan(RuntimeRig rig, string serverPath, out int port)
    {
        port = FreePort();
        var info = new ProcessStartInfo(serverPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in new[] { "--models-preset", rig.Runtime.PresetPath, "--host", "127.0.0.1", "--port", port.ToString(System.Globalization.CultureInfo.InvariantCulture), "--models-max", "1", "--no-models-autoload", "--no-webui" })
        {
            info.ArgumentList.Add(a);
        }

        var process = Process.Start(info)!;
        process.StandardInput.Close();
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WaitHealthyAsync(int port)
    {
        using var http = new HttpClient();
        for (var i = 0; i < 400; i++)
        {
            try
            {
                if ((await http.GetAsync($"http://127.0.0.1:{port}/health", Ct)).IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            await Task.Delay(50, Ct);
        }

        throw new TimeoutException("the orphan router never answered");
    }
}
