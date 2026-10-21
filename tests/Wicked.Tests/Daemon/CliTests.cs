using Wicked.Cli;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Daemon;
using Wicked.Tests.Models;

namespace Wicked.Tests.Daemon;

/// <summary>The command line, run against a daemon on a throwaway profile: what a person would type, and what comes back.</summary>
public sealed class CliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Code, string Out, string Err)> RunAsync(WickedPaths paths, string input, CliHooks? hooks, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var app = new CliApp(paths, stdout, stderr, new StringReader(input), hooks ?? new CliHooks());
        var code = await app.RunAsync(args, Ct);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact(Timeout = 90_000)]
    public async Task ModelsAndPsListWhatIsInstalledAndWhatIsLoaded()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "hi" });

        var models = await RunAsync(rig.Profile.Paths, "", null, "models");
        var none = await RunAsync(rig.Profile.Paths, "", null, "ps");
        await rig.Daemon.Services.Runtime.LoadAsync(GatewayRig.CoderRun, Ct);
        var loaded = await RunAsync(rig.Profile.Paths, "", null, "ps");

        Assert.Equal(0, models.Code);
        var row = models.Out.Split('\n').Skip(1).First(l => l.Length > 0);
        Assert.StartsWith(GatewayRig.CoderRun, row, StringComparison.Ordinal);
        Assert.Contains("unloaded", row, StringComparison.Ordinal);
        Assert.Contains(GatewayRig.CoderCatalog, row, StringComparison.Ordinal);
        Assert.Equal("No model is loaded.\n", none.Out.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("loaded", loaded.Out.Split('\n')[1], StringComparison.Ordinal);

        var unloaded = await RunAsync(rig.Profile.Paths, "", null, "unload");
        Assert.Contains("Unloaded " + GatewayRig.CoderRun, unloaded.Out, StringComparison.Ordinal);
        Assert.Equal(RunState.Unloaded, rig.Daemon.Services.Runtime.Models.Single().State);
        var missing = await RunAsync(rig.Profile.Paths, "", null, "unload", "nope");
        Assert.Equal(1, missing.Code);
        Assert.Contains("nope is not installed", missing.Err, StringComparison.Ordinal);
    }

    [Fact(Timeout = 90_000)]
    public async Task RunAnswersOnePromptOrChatsInTheTerminalKeepingTheConversation()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "Hello there" });

        var single = await RunAsync(rig.Profile.Paths, "", null, "run", GatewayRig.CoderCatalog, "say", "hello");
        Assert.Equal(0, single.Code);
        Assert.Equal("Hello there", single.Out);
        Assert.Contains("Loading tiny-coder", single.Err, StringComparison.Ordinal);

        var chat = await RunAsync(rig.Profile.Paths, "first\nsecond\n/clear\nthird\n/bye\n", null, "run", GatewayRig.CoderCatalog);
        Assert.Equal(0, chat.Code);
        Assert.Equal(5, chat.Out.Split(">>> ").Length - 1); // a prompt for each of the five lines it read
        Assert.Contains("Hello there", chat.Out, StringComparison.Ordinal);
        Assert.Contains("The conversation is cleared.", chat.Out, StringComparison.Ordinal);
        // The second message carried the first exchange with it; after /clear the third started fresh.
        var counts = rig.Logged("chat").Select(e => e.GetProperty("body").GetProperty("messages").GetArrayLength()).ToList();
        Assert.Equal([1, 1, 3, 1], counts);
    }

    [Fact(Timeout = 90_000)]
    public async Task PullAsksBeforeATermsLicenceInstallsOnYesAndRefusesOnNo()
    {
        await using var rig = await GatewayRig.StartAsync();

        var no = await RunAsync(rig.Profile.Paths, "n\n", null, "pull", "tiny-quick");
        Assert.Equal(1, no.Code);
        Assert.Contains("gemma licence", no.Out, StringComparison.Ordinal);
        Assert.Contains("Not installed.", no.Out, StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Daemon.Services.State.Installed, m => m.Id == ModelWorld.Quick);

        var yes = await RunAsync(rig.Profile.Paths, "y\n", null, "pull", "tiny-quick");
        Assert.Equal(0, yes.Code);
        Assert.Contains("is installed", yes.Out, StringComparison.Ordinal);
        Assert.Contains(rig.Daemon.Services.State.Installed, m => m.Id == ModelWorld.Quick);

        // A permissive licence needs no question, a repo is accepted as it stands, and the failures say what is wrong.
        var coder = await RunAsync(rig.Profile.Paths, "", null, "pull", "tiny-chat:Q4_K_M");
        Assert.Equal(0, coder.Code);
        var quant = await RunAsync(rig.Profile.Paths, "", null, "pull", "tiny-chat:Q99");
        Assert.Equal(1, quant.Code);
        Assert.Contains("no Q99 build", quant.Err, StringComparison.Ordinal);
        var nonsense = await RunAsync(rig.Profile.Paths, "", null, "pull", "what is this");
        Assert.Equal(1, nonsense.Code);
        Assert.Contains("not a catalog id", nonsense.Err, StringComparison.Ordinal);
        var flag = await RunAsync(rig.Profile.Paths, "", null, "pull", "--accept-licence", "test/Tiny-Quick-GGUF:Q8_0");
        Assert.Equal(0, flag.Code);
    }

    [Fact(Timeout = 90_000)]
    public async Task UpdateListsWhatIsNewAndUpdatesOneOrAll()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        var upload = StandIns.Weights(1_700_000, 3);
        rig.World.Hub.Repo(ModelWorld.Coder).Upload("rev2", "Tiny-Coder-Q8_0.gguf", upload, "Retrained on more code");

        var none = await RunAsync(rig.Profile.Paths, "", null, "update", "--check");
        Assert.Equal(0, none.Code);
        Assert.Contains(ModelWorld.Coder, none.Out, StringComparison.Ordinal);
        Assert.Contains("newer upload", none.Out, StringComparison.Ordinal);
        Assert.Contains("rev2", none.Out, StringComparison.Ordinal);
        Assert.Contains("  - Retrained on more code", none.Out, StringComparison.Ordinal);
        Assert.Contains("wicked-cli update --all", none.Out, StringComparison.Ordinal);

        var updated = await RunAsync(rig.Profile.Paths, "", null, "update", "--all");
        Assert.Equal(0, updated.Code);
        Assert.Contains("is updated", updated.Out, StringComparison.Ordinal);
        var file = Path.Combine(rig.Profile.Paths.Data, "models", "test", "Tiny-Coder-GGUF", "Tiny-Coder-Q8_0.gguf");
        Assert.Equal(upload, await File.ReadAllBytesAsync(file, Ct));

        var again = await RunAsync(rig.Profile.Paths, "", null, "update");
        Assert.Contains("up to date", again.Out, StringComparison.Ordinal);
        var nothing = await RunAsync(rig.Profile.Paths, "", null, "update", "--all");
        Assert.Contains("Nothing to update.", nothing.Out, StringComparison.Ordinal);
        var unknown = await RunAsync(rig.Profile.Paths, "", null, "update", "nope");
        Assert.Equal(1, unknown.Code);
    }

    [Fact(Timeout = 90_000)]
    public async Task AnUpdateThatCannotLoadIsRolledBackAndTheCommandSaysSo()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        rig.World.Hub.Repo(ModelWorld.Coder).Upload("rev2", "Tiny-Coder-Q8_0.gguf", [.. "FAILLOAD"u8.ToArray(), .. StandIns.Weights(1_700_000, 3)], "Broken");

        var result = await RunAsync(rig.Profile.Paths, "", null, "update", ModelWorld.Coder);

        Assert.Equal(1, result.Code);
        Assert.Contains("the old one is back", result.Out, StringComparison.Ordinal);
        var file = Path.Combine(rig.Profile.Paths.Data, "models", "test", "Tiny-Coder-GGUF", "Tiny-Coder-Q8_0.gguf");
        Assert.Equal(ModelWorld.Weights(ModelWorld.Coder, "Q8_0"), await File.ReadAllBytesAsync(file, Ct));
    }

    [Fact(Timeout = 90_000)]
    public async Task ServeRunsTheDaemonInFrontStopsItAndSaysWhenOneIsAlreadyUp()
    {
        using var profile = new TempProfile();
        IReadOnlyList<string>? asked = null;
        var hooks = new CliHooks { ServeInForeground = (daemonArgs, _) => { asked = daemonArgs; return Task.FromResult(0); } };

        var started = await RunAsync(profile.Paths, "", hooks, "serve", "--port", "1338");
        Assert.Equal(0, started.Code);
        Assert.Equal(["--serve", "--port", "1338"], asked);
        var bad = await RunAsync(profile.Paths, "", hooks, "serve", "--nonsense");
        Assert.Equal(2, bad.Code);
        var notRunning = await RunAsync(profile.Paths, "", hooks, "serve", "--stop");
        Assert.Contains("not running", notRunning.Out, StringComparison.Ordinal);

        await using var daemon = await DaemonHost.StartAsync(profile.Paths, Ct, new DaemonOptions { AllowNetwork = false, Port = 0, RunUpdates = false });
        await daemon.Ready;
        var already = await RunAsync(profile.Paths, "", hooks, "serve");
        Assert.Equal(3, already.Code);
        Assert.Contains("already running", already.Out, StringComparison.Ordinal);
        Assert.Contains(daemon.Gateway.Address!, already.Out, StringComparison.Ordinal);

        var stopped = await RunAsync(profile.Paths, "", hooks, "serve", "--stop");
        Assert.Contains("has stopped", stopped.Out, StringComparison.Ordinal);
        await daemon.WaitForShutdownAsync(Ct);
    }

    [Fact(Timeout = 90_000)]
    public async Task WithNoDaemonTheCommandsStartOneOrSayHowToGetOne()
    {
        using var profile = new TempProfile();
        var without = await RunAsync(profile.Paths, "", new CliHooks(), "models");
        Assert.Equal(1, without.Code);
        Assert.Contains("wicked-cli serve", without.Err, StringComparison.Ordinal);

        DaemonHost? started = null;
        try
        {
            var hooks = new CliHooks
            {
                StartInBackground = async ct =>
                {
                    started = await DaemonHost.StartAsync(profile.Paths, ct, new DaemonOptions { AllowNetwork = false, Port = 0, RunUpdates = false });
                    return true;
                },
                StartupWait = TimeSpan.FromSeconds(30),
            };

            var with = await RunAsync(profile.Paths, "", hooks, "models");

            Assert.Equal(0, with.Code);
            Assert.Contains("Started the daemon.", with.Err, StringComparison.Ordinal);
            Assert.Contains("No models are installed", with.Out, StringComparison.Ordinal);
        }
        finally
        {
            if (started is not null) await started.DisposeAsync();
        }
    }

    [Fact(Timeout = 90_000)]
    public async Task StatusSaysWhatTheDaemonServes()
    {
        await using var rig = await GatewayRig.StartAsync();
        await rig.Daemon.Keys.CreateAsync("Zed", Ct);

        var status = await RunAsync(rig.Profile.Paths, "", null, "status");

        Assert.Equal(0, status.Code);
        Assert.Contains("The daemon is running", status.Out, StringComparison.Ordinal);
        Assert.Contains($"The API is served at {rig.Daemon.Gateway.Address}/v1 (this machine only; 1 API key)", status.Out, StringComparison.Ordinal);
        Assert.Contains("1 model installed, 0 loaded.", status.Out, StringComparison.Ordinal);
    }
}
