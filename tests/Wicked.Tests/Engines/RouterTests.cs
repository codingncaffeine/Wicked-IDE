using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Engines;
using Wicked.Core.Http;

namespace Wicked.Tests;

public sealed class RouterTests : IDisposable
{
    private readonly TempProfile _profile = new();
    private readonly HttpClient _http = WickedHttp.CreateClient(offline: () => false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _http.Dispose();
        _profile.Dispose();
    }

    private const string ModelsJson = """
        {"data":[
          {"id":"a/small:Q4_K_M","path":"/m/a.gguf","status":{"value":"loaded","args":["llama-server","-ctx","4096"]},"architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}},
          {"id":"b/big:Q8_0","path":"/m/b.gguf","status":{"value":"unloaded","args":["llama-server"],"failed":true,"exit_code":1}},
          {"id":"c/dl","status":{"value":"downloading","progress":{"https://x/y.gguf":{"done":1,"total":2}}}},
          {"id":"d/odd","status":{"value":"someday"}}
        ]}
        """;

    [Fact]
    public async Task ReadsTheModelListInTheDocumentedShape()
    {
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/models", () => Results.Text(ModelsJson, "application/json")));
        var client = new RouterClient(_http, server.BaseAddress);

        var models = await client.GetModelsAsync(ct: Ct);

        Assert.Equal(4, models.Count);
        Assert.Equal(RouterModelStatus.Loaded, models[0].Status);
        Assert.Equal(["llama-server", "-ctx", "4096"], models[0].Args);
        Assert.Equal(["text", "image"], models[0].InputModalities);
        Assert.Equal(RouterModelStatus.Unloaded, models[1].Status);
        Assert.True(models[1].Failed);
        Assert.Equal(1, models[1].ExitCode);
        Assert.Equal(RouterModelStatus.Downloading, models[2].Status);
        Assert.Equal(RouterModelStatus.Unknown, models[3].Status);
    }

    [Fact]
    public async Task ReloadAddsTheQueryAndLoadUnloadPostTheModelName()
    {
        var posts = new List<(string Path, string Body)>();
        string? query = null;
        await using var server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/models", (HttpContext c) =>
            {
                query = c.Request.QueryString.Value;
                return Results.Text(ModelsJson, "application/json");
            });
            app.MapPost("/models/load", async (HttpContext c) =>
            {
                posts.Add(("load", await new StreamReader(c.Request.Body).ReadToEndAsync()));
                return Results.Text("""{"success":true}""", "application/json");
            });
            app.MapPost("/models/unload", async (HttpContext c) =>
            {
                posts.Add(("unload", await new StreamReader(c.Request.Body).ReadToEndAsync()));
                return Results.Text("""{"success":false,"error":"nope"}""", "application/json");
            });
        });
        var client = new RouterClient(_http, server.BaseAddress);

        await client.GetModelsAsync(reload: true, ct: Ct);
        await client.LoadAsync("a/small:Q4_K_M", Ct);
        await Assert.ThrowsAsync<RouterException>(() => client.UnloadAsync("a/small:Q4_K_M", Ct));

        Assert.Equal("?reload=1", query);
        Assert.Equal("""{"model":"a/small:Q4_K_M"}""", posts[0].Body);
        Assert.Equal("unload", posts[1].Path);
    }

    [Fact]
    public async Task ParsesTheEventStream()
    {
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/models/sse", async (HttpContext c) =>
        {
            c.Response.ContentType = "text/event-stream";
            foreach (var json in new[]
            {
                """{"model":"a","event":"model_status","data":{"status":"loading"}}""",
                """{"model":"a","event":"model_status","data":{"status":"loading","progress":{"stages":["text_model"],"current":"text_model","value":0.5}}}""",
                """{"model":"a","event":"download_progress","data":{"https://x/y.gguf":{"done":1,"total":2}}}""",
                """{"model":"a","event":"model_status","data":{"status":"loaded","info":{}}}""",
                """{"model":"*","event":"models_reload"}""",
            })
            {
                await c.Response.WriteAsync($"data: {json}\n\n");
                await c.Response.Body.FlushAsync();
            }

            await c.Response.WriteAsync(": comment line\n\n");
        }));
        var client = new RouterClient(_http, server.BaseAddress);

        var events = new List<RouterEvent>();
        await foreach (var e in client.EventsAsync(Ct)) events.Add(e);

        Assert.Equal(5, events.Count);
        Assert.Equal(RouterModelStatus.Loading, events[0].Status);
        Assert.Equal(0.5, events[1].LoadProgress);
        Assert.Equal("download_progress", events[2].Kind);
        Assert.Equal(RouterModelStatus.Loaded, events[3].Status);
        Assert.Equal(("*", "models_reload"), (events[4].Model, events[4].Kind));
    }

    private RouterOptions Options(string server, int? port = null, bool supervise = true) =>
        new(server, Path.Combine(_profile.Root, "preset.ini"), Path.Combine(_profile.Root, "logs", "router.log"),
            ModelsDirectory: Path.Combine(_profile.Root, "models"), ModelsMax: 3, Port: port, Supervise: supervise,
            RestartDelayBase: TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task StartsWithTheDocumentedArgumentsAndLogsItsOutput()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        var argsFile = Path.Combine(_profile.Root, "args.txt");
        var script = Shell.Script(_profile.Root, "llama-server", $"printf '%s\\n' \"$@\" > '{argsFile}'; echo hello-out; echo hello-err >&2; trap 'kill $!; exit 0' TERM; sleep 30 & wait");
        await using var router = new RouterProcess(Options(script), _http);

        await router.StartAsync(Ct);
        await WaitUntil(() => File.Exists(argsFile) && File.ReadAllText(argsFile).Contains("--port", StringComparison.Ordinal));
        var args = (await File.ReadAllLinesAsync(argsFile, Ct)).ToList();

        Assert.Equal(Path.Combine(_profile.Root, "models"), args[args.IndexOf("--models-dir") + 1]);
        Assert.Equal(Path.Combine(_profile.Root, "preset.ini"), args[args.IndexOf("--models-preset") + 1]);
        Assert.Equal("127.0.0.1", args[args.IndexOf("--host") + 1]);
        Assert.Equal(router.BaseAddress.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), args[args.IndexOf("--port") + 1]);
        Assert.Equal("3", args[args.IndexOf("--models-max") + 1]);
        Assert.Contains("--no-models-autoload", args);
        await WaitUntil(() => router.RecentOutput.Contains("hello-err", StringComparison.Ordinal));
        await router.StopAsync(ct: Ct);
        var log = await File.ReadAllTextAsync(Path.Combine(_profile.Root, "logs", "router.log"), Ct);
        Assert.Contains("[out] hello-out", log, StringComparison.Ordinal);
        Assert.Contains("[err] hello-err", log, StringComparison.Ordinal);
        Assert.Contains("exited with code 0", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthPollingWaitsOutA503ThenSucceeds()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        var calls = 0;
        await using var health = await LocalServer.StartAsync(app => app.MapGet("/health", () => ++calls < 4 ? Results.StatusCode(503) : Results.Text("""{"status":"ok"}""", "application/json")));
        var script = Shell.Script(_profile.Root, "llama-server", "trap 'kill $!; exit 0' TERM; sleep 30 & wait");
        await using var router = new RouterProcess(Options(script, port: health.BaseAddress.Port), _http);
        await router.StartAsync(Ct);

        await router.WaitHealthyAsync(TimeSpan.FromSeconds(20), Ct);

        Assert.True(calls >= 4);
    }

    [Fact]
    public async Task HealthTimeoutNamesTheRecentOutput()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        var script = Shell.Script(_profile.Root, "llama-server", "echo loading-forever >&2; trap 'kill $!; exit 0' TERM; sleep 30 & wait");
        await using var router = new RouterProcess(Options(script), _http);
        await router.StartAsync(Ct);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => router.WaitHealthyAsync(TimeSpan.FromMilliseconds(600), Ct));

        Assert.Contains("loading-forever", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopSendsSigtermAndReportsAnIntentionalExit()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        var script = Shell.Script(_profile.Root, "llama-server", "trap 'kill $!; exit 42' TERM; sleep 30 & wait");
        await using var router = new RouterProcess(Options(script), _http);
        var exits = new List<RouterExit>();
        router.Exited += (_, e) => exits.Add(e);
        await router.StartAsync(Ct);
        await WaitUntil(() => router.IsRunning);
        await Task.Delay(200, Ct);

        await router.StopAsync(ct: Ct);

        Assert.False(router.IsRunning);
        var exit = Assert.Single(exits);
        Assert.Equal(42, exit.Code);
        Assert.True(exit.Intentional);
        Assert.False(exit.WillRestart);
    }

    [Fact]
    public async Task WhenWickedEndsHoweverItEndsTheRouterIsStoppedWithIt()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh (Windows ties the router to a job object)");
        var pidFile = Path.Combine(_profile.Root, "router.pid");
        var termed = Path.Combine(_profile.Root, "termed");
        var script = Shell.Script(_profile.Root, "llama-server", $"echo $$ > '{pidFile}'; trap 'touch \"{termed}\"; kill $!; exit 0' TERM; sleep 60 & wait");
        await using var router = new RouterProcess(Options(script, supervise: false), _http);
        await router.StartAsync(Ct);
        await WaitUntil(() => File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
        var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);

        // Wicked crashed or was killed: nothing calls Stop, the system closes Wicked's end of the pipe.
        router.CloseTetherForTests();

        await WaitUntil(() => File.Exists(termed), 10);
        await WaitUntil(() => !RouterOwnership.IsAlive(pid), 10);
        await WaitUntil(() => !router.IsRunning, 10);
    }

    [Fact]
    public async Task StopKillsARouterThatIgnoresSigterm()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        var script = Shell.Script(_profile.Root, "llama-server", "trap '' TERM; while true; do sleep 1; done");
        await using var router = new RouterProcess(Options(script), _http);
        await router.StartAsync(Ct);
        await Task.Delay(200, Ct);
        var watch = Stopwatch.StartNew();

        await router.StopAsync(TimeSpan.FromMilliseconds(500), Ct);

        Assert.False(router.IsRunning);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task RestartsWithGrowingDelaysAfterCrashesAndGivesUp()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        var script = Shell.Script(_profile.Root, "llama-server", "exit 3");
        var options = Options(script) with { MaxConsecutiveFailures = 3 };
        await using var router = new RouterProcess(options, _http);
        var exits = new List<RouterExit>();
        var starts = 0;
        router.Exited += (_, e) => exits.Add(e);
        router.Started += (_, _) => Interlocked.Increment(ref starts);

        await router.StartAsync(Ct);
        await WaitUntil(() => exits.Any(e => !e.WillRestart));

        Assert.Equal(4, exits.Count);
        Assert.All(exits, e => Assert.Equal(3, e.Code));
        Assert.Equal([true, true, true, false], exits.Select(e => e.WillRestart));
        Assert.Equal([TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)], exits.Take(3).Select(e => e.RestartIn!.Value));
        Assert.Equal(4, starts);
        var ex = await Assert.ThrowsAsync<RouterException>(() => router.WaitHealthyAsync(TimeSpan.FromSeconds(2), Ct));
        Assert.Contains("will not be restarted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutSupervisionAnExitStaysDown()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        var script = Shell.Script(_profile.Root, "llama-server", "exit 9");
        await using var router = new RouterProcess(Options(script, supervise: false), _http);
        var exits = new List<RouterExit>();
        router.Exited += (_, e) => exits.Add(e);

        await router.StartAsync(Ct);
        await WaitUntil(() => exits.Count > 0);
        await Task.Delay(300, Ct);

        Assert.Single(exits);
        Assert.False(exits[0].WillRestart);
    }

    /// <summary>
    /// The real thing, when the environment names a llama-server and a tiny GGUF:
    /// <c>WICKED_TEST_LLAMA_SERVER</c> and <c>WICKED_TEST_GGUF</c>. Skipped everywhere else (CI has neither).
    /// </summary>
    [Fact]
    public async Task RealRouterLoadsAModelAndAnswers()
    {
        var server = Environment.GetEnvironmentVariable("WICKED_TEST_LLAMA_SERVER");
        var gguf = Environment.GetEnvironmentVariable("WICKED_TEST_GGUF");
        Assert.SkipWhen(server is null || gguf is null, "set WICKED_TEST_LLAMA_SERVER and WICKED_TEST_GGUF to run against a real llama-server");
        var preset = Path.Combine(_profile.Root, "preset.ini");
        await PresetWriter.WriteAsync(preset, new Dictionary<string, string> { ["c"] = "512", ["n-gpu-layers"] = "0" }, [PresetSection.ForModel("tiny/stories:Q8", gguf!)], Ct);
        var options = new RouterOptions(server!, preset, Path.Combine(_profile.Root, "logs", "router.log"), ModelsMax: 1);
        await using var router = new RouterProcess(options, _http);
        await router.StartAsync(Ct);
        await router.WaitHealthyAsync(TimeSpan.FromSeconds(60), Ct);
        var client = new RouterClient(_http, () => router.BaseAddress);

        var listed = await client.GetModelsAsync(ct: Ct);
        Assert.Contains(listed, m => m.Id == "tiny/stories:Q8");
        await client.LoadAsync("tiny/stories:Q8", Ct);
        await WaitUntil(async () => (await client.GetModelsAsync(ct: Ct)).First(m => m.Id == "tiny/stories:Q8").Status == RouterModelStatus.Loaded, 60);
        using var content = new StringContent("""{"model":"tiny/stories:Q8","prompt":"Once upon a time","n_predict":8,"max_tokens":8}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(new Uri(router.BaseAddress, "v1/completions"), content, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, body);
        Assert.Contains("choices", body, StringComparison.Ordinal);
        await client.UnloadAsync("tiny/stories:Q8", Ct);
        await router.StopAsync(ct: Ct);
        Assert.False(router.IsRunning);
    }

    private static Task WaitUntil(Func<bool> condition, int seconds = 30) => WaitUntil(() => Task.FromResult(condition()), seconds);

    private static async Task WaitUntil(Func<Task<bool>> condition, int seconds = 30)
    {
        var deadline = Stopwatch.StartNew();
        while (!await condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(seconds)) throw new TimeoutException("condition never became true");
            await Task.Delay(25, Ct);
        }
    }
}
