using System.Diagnostics;
using System.Text.Json;
using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Core.Models;
using Wicked.Tests.Models;

namespace Wicked.Tests.Chat;

/// <summary>
/// A model runtime on a throwaway profile whose engine is the scripted router stand-in: models are placeholder GGUF files in the
/// store, the engine archive comes from a loopback GitHub, and <see cref="Script"/> says how the stand-in behaves next.
/// </summary>
internal sealed class RuntimeRig : IAsyncDisposable
{
    public const string Chat = "test/Chat";
    public const string Coder = "test/Coder";
    public const string ChatId = Chat + ":Q4_K_M";
    public const string CoderId = Coder + ":Q4_K_M";

    private readonly TempProfile _profile = new();
    private HubStandIn? _hub;

    private RuntimeRig()
    {
    }

    public ModelServices Services { get; private set; } = null!;

    public ModelRuntime Runtime => Services.Runtime;

    public string EngineFolder { get; private set; } = "";

    public string Root => _profile.Root;

    public Wicked.Core.WickedPaths Paths => _profile.Paths;

    public static async Task<RuntimeRig> StartAsync(params string[] repos)
    {
        Assert.SkipUnless(StandIns.CanRunFakeRouter, "needs a shell and python3 for the scripted router stand-in");
        var rig = new RuntimeRig();
        try
        {
            return await rig.StartCoreAsync(repos);
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    private async Task<RuntimeRig> StartCoreAsync(string[] repos)
    {
        var rig = this;
        rig._hub = await HubStandIn.StartAsync(app => StandIns.MapGitHub(app, 999));
        foreach (var repo in repos.Length == 0 ? [Chat] : repos) rig.AddModel(repo);
        rig.Services = StandIns.Services(rig._profile, rig._hub.Server);
        rig.Services.Configure(new ModelOptions(Backend: EngineBackendKind.Cpu));
        rig.Runtime.Tuning = new RuntimeTuning { RestartDelayBase = TimeSpan.FromMilliseconds(50), PollInterval = TimeSpan.FromMilliseconds(400), HealthTimeout = TimeSpan.FromSeconds(30) };
        await rig.Services.InitializeAsync(allowNetwork: false, TestContext.Current.CancellationToken);
        var engine = await rig.Services.EnsureEngineAsync(TestContext.Current.CancellationToken);
        rig.EngineFolder = Path.GetDirectoryName(engine.ServerPath)!;
        await rig.WaitForModelsAsync(repos.Length == 0 ? 1 : repos.Length);
        return rig;
    }

    /// <summary>Puts a placeholder GGUF in the store for <paramref name="repo"/> (not yet scanned; see <see cref="RescanAsync"/>).</summary>
    public void AddModel(string repo, string quant = "Q4_K_M")
    {
        var parts = repo.Split('/'); // not a path: a repo id, publisher/name
        var folder = Path.Combine(_profile.Paths.Data, "models", parts[0], parts[1]);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, $"{parts[1]}-{quant}.gguf"), StandIns.Weights(4096));
    }

    public async Task RescanAsync() => await Services.RescanAsync(TestContext.Current.CancellationToken);

    /// <summary>Waits for the runtime's list to follow the store (it does so on a worker).</summary>
    public async Task WaitForModelsAsync(int count) => await WaitUntilAsync(() => Runtime.Models.Count == count);

    /// <summary>Tells the stand-in what to do from the next request on.</summary>
    public void Script(object script) => File.WriteAllText(Path.Combine(EngineFolder, "fake.json"), JsonSerializer.Serialize(script));

    /// <summary>Everything the stand-in logged, oldest first.</summary>
    public List<JsonElement> FakeLog()
    {
        var path = Path.Combine(EngineFolder, "fake-log.jsonl");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    public List<JsonElement> Logged(string kind) => [.. FakeLog().Where(e => e.GetProperty("t").GetString() == kind)];

    public RunningModel Model(string id) => Runtime.Models.Single(m => m.Id == id);

    public static async Task WaitUntilAsync(Func<bool> condition, int seconds = 20)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("condition never became true");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Services is not null) await Services.DisposeAsync();
        if (_hub is not null) await _hub.DisposeAsync();
        _profile.Dispose();
    }
}
