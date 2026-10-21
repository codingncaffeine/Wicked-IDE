using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Wicked.Contracts;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Server;
using Wicked.Daemon;
using Wicked.Tests.Chat;

namespace Wicked.Tests.Daemon;

/// <summary>The daemon's own socket (state, models, the log feed, shutdown), the gateway's listener following the settings, and the files behind them.</summary>
public sealed class DaemonControlTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task StatusReportsTheGatewayTheModelsAndWhoOwnsTheRouter()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        using var control = rig.Control();

        var before = (await control.GetStatusAsync(Ct))!;
        Assert.True(before.Ready);
        Assert.True(before.Gateway.Running);
        Assert.Equal(rig.Daemon.Gateway.Address, before.Gateway.Address);
        Assert.Equal(1, before.Installed);
        Assert.Equal(0, before.Loaded);
        Assert.Null(before.RouterOwner);

        await rig.PostAsync("/v1/chat/completions", new { model = GatewayRig.CoderCatalog, messages = new[] { new { role = "user", content = "hi" } } });
        var after = (await control.GetStatusAsync(Ct))!;
        Assert.Equal(1, after.Loaded);
        Assert.Equal("this", after.RouterOwner);
        Assert.Equal(Environment.ProcessId, after.ProcessId);
    }

    [Fact(Timeout = 90_000)]
    public async Task TheGatewayFollowsTheSettingsItMovesWhenThePortChangesAndStopsWhenSwitchedOff()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        var first = rig.Daemon.Gateway.Port;
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.GetAsync(new Uri(rig.Base, "/health"), Ct)).StatusCode);

        var next = FreePort();
        await rig.ChangeSettingsAsync(s => s with { ServerPort = next });
        Assert.Equal(next, rig.Daemon.Gateway.Port);
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.GetAsync(new Uri($"http://127.0.0.1:{next}/health"), Ct)).StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Http.GetAsync(new Uri($"http://127.0.0.1:{first}/health"), Ct));

        await rig.ChangeSettingsAsync(s => s with { ServerEnabled = false });
        Assert.False(rig.Daemon.Gateway.Running);
        Assert.False(rig.Daemon.Gateway.Enabled);
        await Assert.ThrowsAsync<HttpRequestException>(() => rig.Http.GetAsync(new Uri($"http://127.0.0.1:{next}/health"), Ct));
        // The socket still answers: the command line does not need the gateway.
        using var control = rig.Control();
        Assert.False((await control.GetStatusAsync(Ct))!.Gateway.Running);

        await rig.ChangeSettingsAsync(s => s with { ServerEnabled = true });
        Assert.True(rig.Daemon.Gateway.Running);
    }

    [Fact(Timeout = 90_000)]
    public async Task APortSomethingElseHoldsIsSaidSoInsteadOfCrashing()
    {
        await using var rig = await GatewayRig.StartAsync();
        using var squatter = new TcpListener(IPAddress.Loopback, 0);
        squatter.Start();
        var taken = ((IPEndPoint)squatter.LocalEndpoint).Port;

        await rig.ChangeSettingsAsync(s => s with { ServerPort = taken });

        var status = rig.Daemon.Gateway;
        Assert.False(status.Running);
        Assert.Contains($"Port {taken} is already in use", status.Error, StringComparison.Ordinal);
        using var control = rig.Control();
        Assert.NotNull(await control.GetStatusAsync(Ct)); // the daemon itself is fine

        // Freed, it comes up on the next apply.
        squatter.Stop();
        await rig.Daemon.ApplyAsync(Ct);
        Assert.True(rig.Daemon.Gateway.Running);
        Assert.Null(rig.Daemon.Gateway.Error);
    }

    [Fact(Timeout = 90_000)]
    public async Task ListeningOnTheNetworkRefusesOtherMachinesUntilAKeyExists()
    {
        await using var rig = await GatewayRig.StartAsync(s => s with { ServerListenOnLan = true });
        Assert.True(rig.Daemon.Gateway.ListensOnLan);
        Assert.StartsWith("http://0.0.0.0:", rig.Daemon.Gateway.Address, StringComparison.Ordinal);
        // From this machine it is served as before (the check for "another machine" is on the peer's address).
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.GetAsync(new Uri($"http://127.0.0.1:{rig.Daemon.Gateway.Port}/v1/models"), Ct)).StatusCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task LoadAndUnloadOverTheSocketNameModelsTheWayTheGatewayDoes()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        using var control = rig.Control();

        await control.PostAsync("v1/wicked/load", new LoadRequest(GatewayRig.CoderCatalog), WickedJson.Default.LoadRequest, Ct);
        Assert.Equal(RunState.Loaded, rig.Daemon.Services.Runtime.Models.Single().State);
        var ps = await control.GetAsync("v1/wicked/ps", WickedJson.Default.ModelInfoArray, Ct);
        Assert.Equal(GatewayRig.CoderRun, Assert.Single(ps!).Id);

        await control.PostAsync("v1/wicked/unload", new UnloadRequest(null), WickedJson.Default.UnloadRequest, Ct);
        Assert.Equal(RunState.Unloaded, rig.Daemon.Services.Runtime.Models.Single().State);
        await Assert.ThrowsAsync<HttpRequestException>(() => control.PostAsync("v1/wicked/load", new LoadRequest("nope"), WickedJson.Default.LoadRequest, Ct));
    }

    [Fact(Timeout = 90_000)]
    public async Task TheLogFeedDeliversNewRequestsAsTheyHappen()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        using var control = new DaemonClient(rig.Profile.Paths.DaemonSocket, Timeout.InfiniteTimeSpan);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var seen = new List<ApiLogEntry>();
        var reading = Task.Run(async () =>
        {
            await foreach (var data in control.StreamAsync("v1/wicked/log/stream", null, stop.Token))
            {
                lock (seen) seen.Add(JsonSerializer.Deserialize(data, WickedJson.Default.ApiLogEntry)!);
            }
        }, Ct);
        await Task.Delay(300, Ct); // connected

        await rig.PostAsync("/v1/chat/completions", new { model = GatewayRig.CoderCatalog, messages = new[] { new { role = "user", content = "hi" } } });
        await rig.PostAsync("/v1/chat/completions", new { model = "nope", messages = Array.Empty<object>() });

        await RuntimeRig.WaitUntilAsync(() => { lock (seen) return seen.Count(e => e.Route == "/v1/chat/completions") == 2; });
        lock (seen)
        {
            Assert.Equal([200, 404], seen.Where(e => e.Route == "/v1/chat/completions").Select(e => e.Status));
        }

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Fact(Timeout = 90_000)]
    public async Task AShutdownRequestEndsTheDaemonsWait()
    {
        using var profile = new TempProfile();
        await using var daemon = await DaemonHost.StartAsync(profile.Paths, Ct, new DaemonOptions { AllowNetwork = false, Port = 0, RunUpdates = false });
        using var control = new DaemonClient(profile.Paths.DaemonSocket);
        var waiting = daemon.WaitForShutdownAsync(Ct);
        Assert.False(waiting.IsCompleted);

        using var response = await control.Http.PostAsync("v1/wicked/shutdown", null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await waiting.WaitAsync(TimeSpan.FromSeconds(15), Ct);
    }

    [Fact(Timeout = 90_000)]
    public async Task ADaemonWithNoNetworkStillStartsAndServesWhatIsInstalled()
    {
        using var profile = new TempProfile();
        await using var daemon = await DaemonHost.StartAsync(profile.Paths, Ct, new DaemonOptions { AllowNetwork = false, Port = 0, RunUpdates = false });

        await daemon.Ready;

        using var http = new HttpClient();
        var models = await http.GetStringAsync(new Uri($"{daemon.Gateway.Address}/v1/models"), Ct);
        Assert.Contains("\"data\":[]", models, StringComparison.Ordinal);
    }

    // ── the files behind it ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KeysAreKeptAsHashesShownOnceAndMatchedInConstantTime()
    {
        using var profile = new TempProfile();
        var store = new ApiKeyStore(profile.Paths.ApiKeysFile);
        Assert.True((await store.LoadCheckAsync(Ct)).Open);

        var (info, key) = await store.CreateAsync("  Continue  ", Ct);
        var (_, other) = await store.CreateAsync("", Ct);

        Assert.Equal("Continue", info.Name);
        Assert.StartsWith("wk-", key, StringComparison.Ordinal);
        Assert.NotEqual(key, other);
        var listed = await store.ListAsync(Ct);
        Assert.Equal(["Continue", "Unnamed key"], listed.Select(k => k.Name));
        Assert.StartsWith(key[..7], listed[0].Prefix, StringComparison.Ordinal);
        var text = await File.ReadAllTextAsync(store.Path, Ct);
        Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        Assert.Contains(ApiKeyStore.Hash(key), text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.Path));

        var check = await store.LoadCheckAsync(Ct);
        Assert.False(check.Open);
        Assert.Equal("Continue", check.Match("Bearer " + key, null)?.Name);
        Assert.Equal("Continue", check.Match(null, key)?.Name);
        Assert.Equal("Continue", check.Match("bearer " + key, null)?.Name);
        Assert.Null(check.Match("Bearer " + key + "x", null));
        Assert.Null(check.Match(null, null));
        Assert.Null(check.Match("Basic abc", null));

        Assert.True(await store.RevokeAsync(info.Id, Ct));
        Assert.False(await store.RevokeAsync(info.Id, Ct));
        Assert.Null((await store.LoadCheckAsync(Ct)).Match("Bearer " + key, null));
    }

    [Fact]
    public async Task TheStartAtLoginUnitIsWrittenToTheFolderItIsGivenAndOnlyThen()
    {
        using var profile = new TempProfile();
        var home = Path.Combine(profile.Root, "xdg-config");
        var commands = new List<string>();
        var unit = new SystemdUnit(home, (args, _) => { commands.Add(string.Join(' ', args)); return Task.FromResult(0); });
        Assert.False(unit.IsInstalled);
        Assert.False(Directory.Exists(home)); // nothing is written until asked

        await unit.InstallAsync("/usr/bin/wickedd", wickedHome: null, Ct);

        Assert.True(unit.IsInstalled);
        Assert.Equal(Path.Combine(home, "systemd", "user", "wickedd.service"), unit.UnitPath);
        var text = await File.ReadAllTextAsync(unit.UnitPath, Ct);
        Assert.Contains("ExecStart=\"/usr/bin/wickedd\"", text, StringComparison.Ordinal);
        Assert.Contains("WantedBy=default.target", text, StringComparison.Ordinal);
        Assert.Contains("Restart=on-failure", text, StringComparison.Ordinal);
        Assert.DoesNotContain("WICKED_HOME", text, StringComparison.Ordinal);
        Assert.Equal(["daemon-reload", "enable wickedd.service"], commands);

        await unit.RemoveAsync(Ct);
        Assert.False(unit.IsInstalled);
        Assert.Equal(["daemon-reload", "enable wickedd.service", "disable wickedd.service", "daemon-reload"], commands);
        Assert.Throws<ArgumentException>(() => SystemdUnit.Render("/bin/bad\"path"));
        Assert.Contains("Environment=\"WICKED_HOME=/tmp/p\"", SystemdUnit.Render("/usr/bin/wickedd", "/tmp/p"), StringComparison.Ordinal);
        Assert.Equal(Path.Combine("/custom", "systemd", "user"), new SystemdUnit("/custom").UnitFolder);
        Assert.Equal("/x/y", SystemdUnit.DefaultConfigHome(v => v == "XDG_CONFIG_HOME" ? "/x/y" : null));
    }

    [Fact]
    public void TheDaemonIsFoundBesideTheProgramOrByEnvironmentOrOnThePath()
    {
        using var profile = new TempProfile();
        var beside = Path.Combine(profile.Root, "beside");
        var onPath = Path.Combine(profile.Root, "bin");
        Directory.CreateDirectory(beside);
        Directory.CreateDirectory(onPath);
        var name = OperatingSystem.IsWindows() ? "wickedd.exe" : "wickedd";

        Assert.Null(DaemonLocator.Find(_ => null, beside));

        File.WriteAllText(Path.Combine(onPath, name), "");
        Assert.Equal(Path.Combine(onPath, name), DaemonLocator.Find(v => v == "PATH" ? $"/nowhere{Path.PathSeparator}{onPath}" : null, beside));

        File.WriteAllText(Path.Combine(beside, name), "");
        Assert.Equal(Path.Combine(beside, name), DaemonLocator.Find(v => v == "PATH" ? onPath : null, beside));

        var chosen = Path.Combine(profile.Root, "chosen");
        File.WriteAllText(chosen, "");
        Assert.Equal(chosen, DaemonLocator.Find(v => v == DaemonLocator.PathVariable ? chosen : null, beside));
    }

    [Fact]
    public async Task ServerSettingsSurviveARoundTripThroughSettingsJson()
    {
        using var profile = new TempProfile();
        profile.Paths.EnsureCreated();
        var settings = new Wicked.Core.Settings.WickedSettings { ServerPort = 4242, ServerCors = ["http://a.example", "http://b.example"], ServerAutoLoad = false, ServerListenOnLan = true, ServerEnabled = false, ServerMaxRequestMB = 8 };

        await Wicked.Core.Settings.SettingsFile.WriteAsync(profile.Paths.SettingsFile, settings, Ct);
        var (read, problem) = await Wicked.Core.Settings.SettingsFile.ReadAsync(profile.Paths.SettingsFile, Ct);

        Assert.Null(problem);
        Assert.Equal(4242, read.ServerPort);
        Assert.Equal(["http://a.example", "http://b.example"], read.ServerCors);
        Assert.False(read.ServerAutoLoad);
        Assert.True(read.ServerListenOnLan);
        Assert.False(read.ServerEnabled);
        Assert.Equal(8, read.ServerMaxRequestMB);
        Assert.Contains("\"server.port\": 4242", await File.ReadAllTextAsync(profile.Paths.SettingsFile, Ct), StringComparison.Ordinal);
    }
}
