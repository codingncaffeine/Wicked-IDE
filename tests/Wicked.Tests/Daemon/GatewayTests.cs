using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Wicked.Core.Chat;
using Wicked.Daemon.Api;
using Wicked.Tests.Chat;

namespace Wicked.Tests.Daemon;

/// <summary>The API gateway, asked the way Continue, Zed, Aider and curl ask: plain HTTP to a port, with the scripted engine behind it.</summary>
public sealed class GatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object Hi = new { model = GatewayRig.CoderCatalog, messages = new[] { new { role = "user", content = "hi" } } };

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();

    [Fact(Timeout = 90_000)]
    public async Task ModelsListsWhatIsInstalledByRouterIdAndByCatalogId()
    {
        await using var rig = await GatewayRig.StartAsync();

        var response = await rig.Http.GetAsync(new Uri(rig.Base, "/v1/models"), Ct);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("list", json.GetProperty("object").GetString());
        var ids = json.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.Equal([GatewayRig.CoderRun, GatewayRig.CoderCatalog], ids);
        Assert.All(json.GetProperty("data").EnumerateArray(), m => Assert.Equal("model", m.GetProperty("object").GetString()));
        var one = await rig.Http.GetAsync(new Uri(rig.Base, "/v1/models/" + GatewayRig.CoderCatalog), Ct);
        Assert.Equal(GatewayRig.CoderCatalog, (await JsonOf(one)).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.GetAsync(new Uri(rig.Base, "/v1/models/nope"), Ct)).StatusCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task AChatCompletionByCatalogIdLoadsTheModelAndAnswersInOpenAisShape()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "Hello from the fake engine." });
        Assert.Equal(RunState.Unloaded, rig.Daemon.Services.Runtime.Models.Single().State);

        var response = await rig.PostAsync("/v1/chat/completions", Hi);
        var json = await JsonOf(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Hello from the fake engine.", json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(7, json.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        // The engine was asked for the router's own id, and the model is now loaded (loading on the first request).
        var asked = Assert.Single(rig.Logged("chat"));
        Assert.Equal(GatewayRig.CoderRun, asked.GetProperty("body").GetProperty("model").GetString());
        Assert.Equal(RunState.Loaded, rig.Daemon.Services.Runtime.Models.Single().State);
        Assert.Single(rig.Logged("load"));
    }

    [Fact(Timeout = 90_000)]
    public async Task AStreamedReplyReachesTheClientAsItIsProducedNotAfterwards()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "one two three four five six", streamGapMs = 250 });
        await rig.Daemon.Services.Runtime.LoadAsync(GatewayRig.CoderRun, Ct);

        var watch = Stopwatch.StartNew();
        var response = await rig.PostAsync("/v1/chat/completions", new { model = GatewayRig.CoderCatalog, stream = true, messages = new[] { new { role = "user", content = "hi" } } });
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(Ct));
        var text = new StringBuilder();
        TimeSpan? first = null;
        var done = false;
        while (await reader.ReadLineAsync(Ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            first ??= watch.Elapsed;
            var data = line[6..];
            if (data == "[DONE]")
            {
                done = true;
                break;
            }

            var json = JsonDocument.Parse(data).RootElement;
            if (json.GetProperty("choices").GetArrayLength() > 0 && json.GetProperty("choices")[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var c)) text.Append(c.GetString());
        }

        Assert.True(done);
        Assert.Equal("one two three four five six", text.ToString());
        // Six words 250 ms apart take over 1.5 s; the first of them arrived in the first part of that, not at the end.
        Assert.True(first < TimeSpan.FromMilliseconds(900), $"the first event came after {first}");
        Assert.True(watch.Elapsed > TimeSpan.FromMilliseconds(1300), $"the whole reply took only {watch.Elapsed}");
    }

    [Fact(Timeout = 90_000)]
    public async Task AClientThatHangsUpStopsTheEngineGenerating()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = string.Join(' ', Enumerable.Range(0, 200).Select(i => "w" + i)), streamGapMs = 50 });
        await rig.Daemon.Services.Runtime.LoadAsync(GatewayRig.CoderRun, Ct);
        using var hangup = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(rig.Base, "/v1/chat/completions"))
        {
            Content = new StringContent(JsonSerializer.Serialize(new { model = GatewayRig.CoderCatalog, stream = true, messages = new[] { new { role = "user", content = "hi" } } }), Encoding.UTF8, "application/json"),
        };
        var response = await rig.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, hangup.Token);
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(hangup.Token)))
        {
            Assert.NotNull(await reader.ReadLineAsync(hangup.Token)); // one event arrived
        }

        response.Dispose();

        await RuntimeRig.WaitUntilAsync(() => rig.Logged("chat-aborted").Count == 1);
    }

    [Fact(Timeout = 90_000)]
    public async Task AnthropicMessagesWorkStreamedAndNot()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "Hi there friend" });

        var plain = await rig.PostAsync("/v1/messages", new { model = GatewayRig.CoderCatalog, max_tokens = 64, messages = new[] { new { role = "user", content = "hi" } } });
        var json = await JsonOf(plain);
        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal("message", json.GetProperty("type").GetString());
        Assert.Equal("Hi there friend", json.GetProperty("content")[0].GetProperty("text").GetString());

        var streamed = await rig.PostAsync("/v1/messages", new { model = GatewayRig.CoderCatalog, max_tokens = 64, stream = true, messages = new[] { new { role = "user", content = "hi" } } });
        var text = await streamed.Content.ReadAsStringAsync(Ct);
        Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType?.MediaType);
        Assert.Contains("event: message_start", text, StringComparison.Ordinal);
        Assert.Contains("event: content_block_delta", text, StringComparison.Ordinal);
        Assert.EndsWith("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n", text, StringComparison.Ordinal);
        Assert.Equal(2, rig.Logged("messages").Count);
        Assert.All(rig.Logged("messages"), m => Assert.Equal(GatewayRig.CoderRun, m.GetProperty("body").GetProperty("model").GetString()));
    }

    [Fact(Timeout = 90_000)]
    public async Task CompletionsEmbeddingsAndResponsesGoThroughToo()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "text out" });

        var completions = await JsonOf(await rig.PostAsync("/v1/completions", new { model = GatewayRig.CoderCatalog, prompt = "once" }));
        Assert.Equal("text out", completions.GetProperty("choices")[0].GetProperty("text").GetString());
        var embeddings = await JsonOf(await rig.PostAsync("/v1/embeddings", new { model = GatewayRig.CoderCatalog, input = "x" }));
        Assert.Equal(3, embeddings.GetProperty("data")[0].GetProperty("embedding").GetArrayLength());
        var responses = await JsonOf(await rig.PostAsync("/v1/responses", new { model = GatewayRig.CoderCatalog, input = "x" }));
        Assert.Equal("text out", responses.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        // Clients that leave out /v1 are served the same.
        var bare = await JsonOf(await rig.PostAsync("/chat/completions", Hi));
        Assert.Equal("text out", bare.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact(Timeout = 90_000)]
    public async Task AnUnknownModelIs404InOpenAisShapeAndAnthropicsOnTheMessagesRoute()
    {
        await using var rig = await GatewayRig.StartAsync();

        var openAi = await rig.PostAsync("/v1/chat/completions", new { model = "gpt-4o", messages = Array.Empty<object>() });
        var body = await JsonOf(openAi);
        Assert.Equal(HttpStatusCode.NotFound, openAi.StatusCode);
        Assert.Equal("model_not_found", body.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("invalid_request_error", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("model", body.GetProperty("error").GetProperty("param").GetString());
        Assert.Contains("gpt-4o", body.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);

        var anthropic = await rig.PostAsync("/v1/messages", new { model = "claude-sonnet", max_tokens = 5, messages = Array.Empty<object>() });
        var abody = await JsonOf(anthropic);
        Assert.Equal(HttpStatusCode.NotFound, anthropic.StatusCode);
        Assert.Equal("error", abody.GetProperty("type").GetString());
        Assert.Equal("not_found_error", abody.GetProperty("error").GetProperty("type").GetString());
        Assert.Empty(rig.Logged("chat")); // nothing reached the engine

        var junk = await rig.Http.PostAsync(new Uri(rig.Base, "/v1/chat/completions"), new StringContent("not json", Encoding.UTF8, "application/json"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.GetAsync(new Uri(rig.Base, "/v1/nothing"), Ct)).StatusCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task WithLoadingOnFirstRequestOffAnUnloadedModelIsRefusedUntilItIsLoaded()
    {
        await using var rig = await GatewayRig.StartAsync(s => s with { ServerAutoLoad = false });
        rig.Script(new { reply = "ok" });

        var refused = await rig.PostAsync("/v1/chat/completions", Hi);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("model_not_loaded", (await JsonOf(refused)).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(rig.Logged("load"));

        await rig.Daemon.Services.Runtime.LoadAsync(GatewayRig.CoderRun, Ct);
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi)).StatusCode);

        // Turned on while running, it takes effect without a restart.
        await rig.Daemon.Services.Runtime.UnloadAsync(GatewayRig.CoderRun, Ct);
        await rig.ChangeSettingsAsync(s => s with { ServerAutoLoad = true });
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi)).StatusCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task KeysAreRequiredOnceOneExistsAcceptedAsBearerOrXApiKeyAndRevocable()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi)).StatusCode); // none yet: open on this machine

        var (info, key) = await rig.Daemon.Keys.CreateAsync("Continue", Ct);
        await rig.Daemon.ApplyAsync(Ct);

        var missing = await rig.PostAsync("/v1/chat/completions", Hi);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("invalid_api_key", (await JsonOf(missing)).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.PostAsync("/v1/chat/completions", Hi, bearer: "wk-wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi, bearer: key)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi, apiKey: key)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.Http.GetAsync(new Uri(rig.Base, "/v1/models"), Ct)).StatusCode);
        var anthropic = await rig.PostAsync("/v1/messages", new { model = "x", max_tokens = 1, messages = Array.Empty<object>() });
        Assert.Equal("authentication_error", (await JsonOf(anthropic)).GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.GetAsync(new Uri(rig.Base, "/health"), Ct)).StatusCode); // a health check needs no key

        // The log names the key by its name, never by the key.
        var entries = rig.Daemon.Log.Since(0);
        Assert.Contains(entries, e => e.Client == "Continue" && e.Status == 200);
        Assert.DoesNotContain(entries, e => e.Client == key || (e.Error ?? "").Contains(key, StringComparison.Ordinal));
        Assert.Equal("Continue", info.Name);
        Assert.StartsWith("wk-", key, StringComparison.Ordinal);
        Assert.DoesNotContain(key, await File.ReadAllTextAsync(rig.Profile.Paths.ApiKeysFile, Ct), StringComparison.Ordinal); // only its hash is kept

        await rig.Daemon.Keys.RevokeAsync(info.Id, Ct);
        await rig.Daemon.ApplyAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi, bearer: key)).StatusCode); // no keys left: open again
    }

    [Fact(Timeout = 90_000)]
    public async Task ARevokedKeyStopsWorkingWhileAnotherStillDoes()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "ok" });
        var (first, firstKey) = await rig.Daemon.Keys.CreateAsync("Zed", Ct);
        var (_, secondKey) = await rig.Daemon.Keys.CreateAsync("Aider", Ct);
        await rig.Daemon.ApplyAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi, bearer: firstKey)).StatusCode);

        await rig.Daemon.Keys.RevokeAsync(first.Id, Ct);
        await rig.Daemon.ApplyAsync(Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, (await rig.PostAsync("/v1/chat/completions", Hi, bearer: firstKey)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi, bearer: secondKey)).StatusCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task CorsAnswersPreflightOnlyForAllowedOrigins()
    {
        await using var rig = await GatewayRig.StartAsync(s => s with { ServerCors = ["http://localhost:3000"] });
        rig.Script(new { reply = "ok" });

        HttpRequestMessage Preflight(string origin)
        {
            var request = new HttpRequestMessage(HttpMethod.Options, new Uri(rig.Base, "/v1/chat/completions"));
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            return request;
        }

        var allowed = await rig.Http.SendAsync(Preflight("http://localhost:3000"), Ct);
        Assert.Equal(HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.Equal("http://localhost:3000", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("POST", allowed.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.Ordinal);
        Assert.Equal("authorization,content-type", allowed.Headers.GetValues("Access-Control-Allow-Headers").Single());

        var denied = await rig.Http.SendAsync(Preflight("http://evil.example"), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));

        // The real request carries the allow header for the allowed origin and not for another.
        var ok = await rig.PostAsync("/v1/chat/completions", Hi, origin: "http://localhost:3000");
        Assert.Equal("http://localhost:3000", ok.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var other = await rig.PostAsync("/v1/chat/completions", Hi, origin: "http://evil.example");
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));

        // The list is live: removed, the origin is refused at once.
        await rig.ChangeSettingsAsync(s => s with { ServerCors = [] });
        Assert.Equal(HttpStatusCode.Forbidden, (await rig.Http.SendAsync(Preflight("http://localhost:3000"), Ct)).StatusCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task ARequestOverTheLimitIsRefusedBeforeItReachesTheEngine()
    {
        await using var rig = await GatewayRig.StartAsync(s => s with { ServerMaxRequestMB = 1 });
        rig.Script(new { reply = "ok" });
        var big = new string('x', 2 * 1024 * 1024);

        var response = await rig.PostAsync("/v1/chat/completions", new { model = GatewayRig.CoderCatalog, messages = new[] { new { role = "user", content = big } } });

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("request_too_large", (await JsonOf(response)).GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(rig.Logged("chat"));
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi)).StatusCode); // a small one still goes
    }

    [Fact(Timeout = 90_000)]
    public async Task TooManyAtOnceAreToldToComeBack()
    {
        await using var rig = await GatewayRig.StartAsync(maxConcurrent: 1);
        rig.Script(new { reply = "slow reply with several words", streamGapMs = 400 });
        await rig.Daemon.Services.Runtime.LoadAsync(GatewayRig.CoderRun, Ct);
        var slow = await rig.PostAsync("/v1/chat/completions", new { model = GatewayRig.CoderCatalog, stream = true, messages = new[] { new { role = "user", content = "hi" } } });
        using var reader = new StreamReader(await slow.Content.ReadAsStreamAsync(Ct));
        Assert.NotNull(await reader.ReadLineAsync(Ct)); // it holds the only slot

        var busy = await rig.PostAsync("/v1/chat/completions", Hi);

        Assert.Equal((HttpStatusCode)429, busy.StatusCode);
        Assert.Equal("1", busy.Headers.RetryAfter?.Delta?.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await reader.ReadToEndAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, (await rig.PostAsync("/v1/chat/completions", Hi)).StatusCode); // the slot is free again
    }

    [Fact(Timeout = 90_000)]
    public async Task AHostNameThatIsNotLocalIsRefusedWhileNoKeyExists()
    {
        await using var rig = await GatewayRig.StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(rig.Base, "/v1/models"));
        request.Headers.Host = "evil.example";

        var response = await rig.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("invalid_host", (await JsonOf(response)).GetProperty("error").GetProperty("code").GetString());
        var local = new HttpRequestMessage(HttpMethod.Get, new Uri(rig.Base, "/v1/models"));
        local.Headers.Host = "localhost";
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.SendAsync(local, Ct)).StatusCode);
    }

    [Fact(Timeout = 90_000)]
    public async Task TheRequestLogNamesMethodRouteModelStatusTokensAndTime()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "a b c" });
        await rig.PostAsync("/v1/chat/completions", Hi);
        await rig.PostAsync("/v1/chat/completions", new { model = "nope", messages = Array.Empty<object>() });
        await rig.PostAsync("/v1/chat/completions", new { model = GatewayRig.CoderCatalog, stream = true, messages = new[] { new { role = "user", content = "hi" } } });

        // A request is logged when its middleware finishes, which can be just after the client has read the reply.
        var until = DateTime.UtcNow.AddSeconds(10);
        while (rig.Daemon.Log.Since(0).Count < 3 && DateTime.UtcNow < until) await Task.Delay(20, Ct);
        var entries = rig.Daemon.Log.Since(0);

        Assert.Equal(3, entries.Count);
        var ok = entries[0];
        Assert.Equal("POST", ok.Method);
        Assert.Equal("/v1/chat/completions", ok.Route);
        Assert.Equal(GatewayRig.CoderRun, ok.Model);
        Assert.Equal(200, ok.Status);
        Assert.Equal(7, ok.PromptTokens);
        Assert.Equal(3, ok.CompletionTokens);
        Assert.True(ok.Milliseconds >= 0);
        Assert.False(ok.Streamed);
        Assert.Equal(404, entries[1].Status);
        Assert.Equal("nope", entries[1].Model);
        Assert.Equal("unknown model", entries[1].Error);
        Assert.True(entries[2].Streamed);
        Assert.Equal(3, entries[2].CompletionTokens);
        // The same entries are what the window's log view reads over the daemon's socket.
        using var control = rig.Control();
        var over = await control.GetAsync("v1/wicked/log", Wicked.Contracts.WickedJson.Default.ApiLogEntryArray, Ct);
        Assert.Equal(entries.Select(e => e.Id), over!.Select(e => e.Id));
    }

    [Fact(Timeout = 90_000)]
    public async Task ARouterAnotherProcessOwnsIsUsedNotStartedAgain()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "shared" });
        // Another process (the window) on the same profile loads the model first and owns the router.
        await using var window = rig.World.Services(rig.Profile.Paths);
        await window.InitializeAsync(allowNetwork: true, Ct);
        await RuntimeRig.WaitUntilAsync(() => window.Runtime.Models.Count > 0);
        await window.Runtime.LoadAsync(GatewayRig.CoderRun, Ct);

        var response = await rig.PostAsync("/v1/chat/completions", Hi);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(rig.Logged("start"));
        Assert.IsType<Wicked.Core.Engines.AttachedRouter>(rig.Daemon.Services.Runtime.Router);
        using var control = rig.Control();
        Assert.Equal("other", (await control.GetStatusAsync(Ct))!.RouterOwner);
    }

    [Fact]
    public void TheUsageSnifferFindsCountsInTheHeadAndTheTailOfAnyDialect()
    {
        var sniffer = new UsageSniffer();
        sniffer.Add(Encoding.UTF8.GetBytes("event: message_start\ndata: {\"message\":{\"usage\":{\"input_tokens\":11,\"output_tokens\":0}}}\n\n"));
        for (var i = 0; i < 2000; i++) sniffer.Add(Encoding.UTF8.GetBytes("event: content_block_delta\ndata: {\"delta\":{\"text\":\"word \"}}\n\n"));
        sniffer.Add(Encoding.UTF8.GetBytes("event: message_delta\ndata: {\"usage\":{\"output_tokens\":2000}}\n\n"));

        Assert.Equal((11, 2000), sniffer.Result());

        var openAi = new UsageSniffer();
        openAi.Add(Encoding.UTF8.GetBytes("{\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":9,\"total_tokens\":14}}"));
        Assert.Equal((5, 9), openAi.Result());

        Assert.Equal((null, null), new UsageSniffer().Result());
    }
}
