using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Settings;
using Wicked.Daemon;
using Wicked.Tests.Chat;
using Wicked.Tests.Models;

namespace Wicked.Tests.Daemon;

/// <summary>
/// A daemon on a profile of its own, its model services wired to the stand-in Hub and the scripted engine, its gateway on a free
/// port, and a catalog model installed. Requests go to it with a plain <see cref="HttpClient"/>, as any program would send them.
/// </summary>
internal sealed class GatewayRig : IAsyncDisposable
{
    public const string CoderRun = ModelWorld.Coder + ":Q8_0";
    public const string CoderCatalog = "tiny-coder";

    private readonly TempProfile _profile = new();

    private GatewayRig(ModelWorld world)
    {
        World = world;
    }

    public ModelWorld World { get; }

    public TempProfile Profile => _profile;

    public DaemonHost Daemon { get; private set; } = null!;

    public HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

    public string EngineFolder { get; private set; } = "";

    public Uri Base => new(Daemon.Gateway.Address ?? throw new InvalidOperationException("The gateway is not listening"));

    public static async Task<GatewayRig> StartAsync(Func<WickedSettings, WickedSettings>? settings = null, int maxConcurrent = 64, bool install = true)
    {
        Assert.SkipUnless(StandIns.CanRunFakeRouter, "needs a shell for the fake engine wrapper");
        var rig = new GatewayRig(await ModelWorld.StartAsync());
        try
        {
            rig._profile.Paths.EnsureCreated();
            var wanted = (settings ?? (s => s))(new WickedSettings { ServerPort = 0 });
            await SettingsFile.WriteAsync(rig._profile.Paths.SettingsFile, wanted, CancellationToken.None);
            rig.Daemon = await DaemonHost.StartAsync(rig._profile.Paths, CancellationToken.None, new DaemonOptions
            {
                ServicesFactory = paths => rig.World.Services(paths),
                AllowNetwork = true,
                RunUpdates = false,
                MaxConcurrent = maxConcurrent,
                Tuning = new RuntimeTuning { RestartDelayBase = TimeSpan.FromMilliseconds(50), PollInterval = TimeSpan.FromMilliseconds(400), HealthTimeout = TimeSpan.FromSeconds(30), AttachedPoll = TimeSpan.FromMilliseconds(100) },
            });
            await rig.Daemon.Ready;
            var engine = await rig.Daemon.Services.EnsureEngineAsync();
            rig.EngineFolder = Path.GetDirectoryName(engine.ServerPath)!;
            if (install)
            {
                var model = rig.World.Catalog.Models.First(m => m.Repo == ModelWorld.Coder);
                await rig.Daemon.Services.InstallModelAsync(model, model.Tiers.First(t => t.Quant == "Q8_0"), licenceAccepted: true);
                await RuntimeRig.WaitUntilAsync(() => rig.Daemon.Services.Runtime.Models.Count > 0);
            }

            return rig;
        }
        catch
        {
            await rig.DisposeAsync();
            throw;
        }
    }

    public void Script(object script) => File.WriteAllText(Path.Combine(EngineFolder, "fake.json"), JsonSerializer.Serialize(script));

    public List<JsonElement> Logged(string kind)
    {
        var path = Path.Combine(EngineFolder, "fake-log.jsonl");
        if (!File.Exists(path)) return [];
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(e => e.GetProperty("t").GetString() == kind)];
    }

    /// <summary>Puts new settings in force the way the Settings page does: written to settings.json, then the daemon told.</summary>
    public async Task ChangeSettingsAsync(Func<WickedSettings, WickedSettings> change)
    {
        var (current, _) = await SettingsFile.ReadAsync(Profile.Paths.SettingsFile, CancellationToken.None);
        await SettingsFile.WriteAsync(Profile.Paths.SettingsFile, change(current), CancellationToken.None);
        await Daemon.ApplyAsync(TestContext.Current.CancellationToken);
    }

    public Task<HttpResponseMessage> PostAsync(string path, object body, string? bearer = null, string? apiKey = null, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, path)) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (apiKey is not null) request.Headers.Add("x-api-key", apiKey);
        if (origin is not null) request.Headers.Add("Origin", origin);
        return Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }

    public DaemonClient Control() => new(Profile.Paths.DaemonSocket, TimeSpan.FromSeconds(30));

    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        if (Daemon is not null) await Daemon.DisposeAsync();
        _profile.Dispose();
        await World.DisposeAsync();
    }
}
