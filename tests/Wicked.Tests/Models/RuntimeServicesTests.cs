using Wicked.Core.Engines;
using Wicked.Core.Models;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>The Runtime page's operations: what the engine reports, the backend override, updating to a newer build, and rolling back.</summary>
public sealed class RuntimeServicesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRuntimeReportsTheEngineItsDevicesAndTheBackendChoices()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var hub = await HubStandIn.StartAsync(app => StandIns.MapGitHub(app, 999));
        await using var services = StandIns.Services(profile, hub.Server);
        await services.InitializeAsync(allowNetwork: false, Ct);
        services.Configure(new ModelOptions(Backend: EngineBackendKind.Cpu));

        Assert.Null((await services.GetRuntimeAsync(Ct)).Current);
        await services.EnsureEngineAsync(Ct);
        var runtime = await services.GetRuntimeAsync(Ct);

        Assert.Equal("b999-cpu", runtime.Current!.Id);
        Assert.Equal(999, runtime.Report!.Version!.Build);
        var device = Assert.Single(runtime.Report.Devices);
        Assert.Equal("Test GPU", device.Name);
        Assert.Equal(8192L * 1024 * 1024, device.TotalBytes);
        Assert.Equal(EngineBackendKind.Cpu, runtime.Preferred);
        Assert.Equal(EngineBackendKind.Cpu, runtime.Choices[0].Backend.Kind);
        Assert.Null(runtime.Previous);
    }

    [Fact]
    public async Task ANewerBuildIsOfferedInstalledAndRolledBack()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        var shown = new[] { 999 };
        await using var hub = await HubStandIn.StartAsync(app => StandIns.MapGitHub(app, [999, 1000], () => shown));
        await using var services = StandIns.Services(profile, hub.Server);
        await services.InitializeAsync(allowNetwork: false, Ct);
        services.Configure(new ModelOptions(Backend: EngineBackendKind.Cpu));
        await services.EnsureEngineAsync(Ct);

        Assert.Null(await services.CheckEngineUpdateAsync(Ct)); // up to date

        shown = [999, 1000];
        var update = await services.CheckEngineUpdateAsync(Ct);
        Assert.Equal("b1000-cpu", update!.Id);
        Assert.Equal("b999-cpu", update.From!.Id);

        await services.UpdateEngineAsync(update, Ct);
        var runtime = await services.GetRuntimeAsync(Ct);
        Assert.Equal("b1000-cpu", runtime.Current!.Id);
        Assert.Equal("b999-cpu", runtime.Previous!.Id); // kept for rollback
        Assert.Equal(1000, runtime.Report!.Version!.Build);
        Assert.Equal("b1000-cpu", services.State.Engine!.Id);
        Assert.Null(await services.CheckEngineUpdateAsync(Ct));

        await services.RollbackEngineAsync();
        Assert.Equal("b999-cpu", (await services.GetRuntimeAsync(Ct)).Current!.Id);
        Assert.Equal("b999-cpu", services.State.Engine!.Id);
    }

    [Fact]
    public async Task ChoosingAnotherBackendOffersThatBuildAndNeverDowngrades()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        var shown = new[] { 1000 };
        await using var hub = await HubStandIn.StartAsync(app => StandIns.MapGitHub(app, [999, 1000], () => shown));
        await using var services = StandIns.Services(profile, hub.Server);
        await services.InitializeAsync(allowNetwork: false, Ct);
        services.Configure(new ModelOptions(Backend: EngineBackendKind.Cpu));
        await services.EnsureEngineAsync(Ct);

        // The user picks Vulkan: the same build for that backend is the "update".
        services.Configure(new ModelOptions(Backend: EngineBackendKind.Vulkan));
        var update = await services.CheckEngineUpdateAsync(Ct);
        Assert.Equal("b1000-vulkan", update!.Id);
        await services.UpdateEngineAsync(update, Ct);
        var runtime = await services.GetRuntimeAsync(Ct);
        Assert.Equal("b1000-vulkan", runtime.Current!.Id);
        Assert.Equal("b1000-cpu", runtime.Previous!.Id);

        // An older build of the same backend is not an update.
        shown = [999];
        Assert.Null(await services.CheckEngineUpdateAsync(Ct));
    }
}
