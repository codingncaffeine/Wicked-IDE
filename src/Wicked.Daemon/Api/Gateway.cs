using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Wicked.Core.Chat;

namespace Wicked.Daemon.Api;

/// <summary>The three dialects of an error body: what OpenAI's clients and Anthropic's each expect to parse.</summary>
internal enum Dialect
{
    OpenAi,
    Anthropic,
}

/// <summary>
/// The OpenAI and Anthropic routes (D12) in front of the router: the model a client names is mapped to the router's id, loaded on
/// the first request when asked to, and the reply is streamed back as it arrives, never buffered, with the counts that pass by
/// noted for the request log. The same routes are mounted on the daemon's socket (no keys there) for the command line's chat.
/// </summary>
internal static class Gateway
{
    /// <summary>Every POST route that goes to the router, with and without the <c>/v1</c> that OpenAI's clients write.</summary>
    private static readonly string[] Posts =
    [
        "chat/completions", "completions", "embeddings", "responses", "messages", "messages/count_tokens", "rerank",
        "completion", "infill", "tokenize", "detokenize", "embedding",
    ];

    private const string RequestKey = "wicked.log";

    private sealed class Note
    {
        public string? Model;
        public int? Prompt;
        public int? Completion;
        public bool Streamed;
        public string? Error;
        public string? Client;
    }

    public static void Map(IEndpointRouteBuilder app, GatewayContext g)
    {
        app.MapGet("/health", () => Results.Text("{\"status\":\"ok\"}", "application/json"));
        app.MapGet("/v1/models", (HttpContext http) => ListModelsAsync(http, g));
        app.MapGet("/models", (HttpContext http) => ListModelsAsync(http, g));
        app.MapGet("/v1/models/{**id}", (HttpContext http, string id) => OneModelAsync(http, g, id));
        foreach (var route in Posts)
        {
            var dialect = route.StartsWith("messages", StringComparison.Ordinal) ? Dialect.Anthropic : Dialect.OpenAi;
            app.MapPost("/v1/" + route, (HttpContext http) => ProxyAsync(http, g, "/v1/" + route, dialect));
            app.MapPost("/" + route, (HttpContext http) => ProxyAsync(http, g, "/v1/" + route, dialect));
        }
    }

    /// <summary>The checks every request passes before a route sees it: the host, CORS, the key, the limit; and the log line after.</summary>
    public static void Guard(WebApplication app, GatewayContext g)
    {
        app.Use(async (http, next) =>
        {
            var path = http.Request.Path.Value ?? "/";
            if (path is "/health" or "/v1/health")
            {
                await next(http).ConfigureAwait(false);
                return;
            }

            var note = new Note();
            http.Items[RequestKey] = note;
            var watch = Stopwatch.StartNew();
            var started = DateTimeOffset.UtcNow;
            var config = g.Config;
            var dialect = path.Contains("/messages", StringComparison.Ordinal) ? Dialect.Anthropic : Dialect.OpenAi;
            var acquired = false;
            try
            {
                if (!g.Control && !await ScreenAsync(http, config, note, dialect).ConfigureAwait(false)) return;
                acquired = await g.Slots.WaitAsync(0).ConfigureAwait(false);
                if (!acquired)
                {
                    http.Response.Headers.RetryAfter = "1";
                    note.Error = "too many requests at once";
                    await WriteErrorAsync(http, dialect, 429, "The server is busy with other requests. Try again in a moment.", "rate_limit_exceeded").ConfigureAwait(false);
                    return;
                }

                await next(http).ConfigureAwait(false);
            }
            finally
            {
                if (acquired) g.Slots.Release();
                if (http.Request.Method != HttpMethods.Options)
                {
                    g.Log.Add(started, http.Request.Method, path, note.Model, http.Response.StatusCode, note.Prompt, note.Completion, watch.ElapsedMilliseconds, note.Streamed, note.Client, note.Error);
                }
            }
        });
    }

    // ── Who may ask ──────────────────────────────────────────────────────────────────────────────

    /// <summary>False when the request was answered here (a preflight, a refusal): the route is not to run.</summary>
    private static async Task<bool> ScreenAsync(HttpContext http, GatewayConfig config, Note note, Dialect dialect)
    {
        var origin = http.Request.Headers.Origin.ToString();
        var allowedOrigin = origin.Length > 0 && config.Cors.Any(o => o == "*" || string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
        if (allowedOrigin)
        {
            http.Response.Headers.AccessControlAllowOrigin = origin;
            http.Response.Headers.Vary = "Origin";
            http.Response.Headers.AccessControlExposeHeaders = "x-request-id, retry-after";
        }

        if (HttpMethods.IsOptions(http.Request.Method) && http.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            if (!allowedOrigin)
            {
                note.Error = "origin not allowed";
                http.Response.StatusCode = 403;
                return false;
            }

            http.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            var asked = http.Request.Headers.AccessControlRequestHeaders.ToString();
            http.Response.Headers.AccessControlAllowHeaders = asked.Length > 0 ? asked : "authorization, content-type, x-api-key, anthropic-version, anthropic-beta";
            http.Response.Headers.AccessControlMaxAge = "600";
            if (http.Request.Headers.ContainsKey("Access-Control-Request-Private-Network")) http.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
            http.Response.StatusCode = 204;
            return false;
        }

        var keys = config.Keys;
        var remote = http.Connection.RemoteIpAddress;
        var local = remote is null || IPAddress.IsLoopback(remote) || remote.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remote.MapToIPv4());
        if (keys.Open)
        {
            if (!local)
            {
                note.Error = "no API key exists";
                await WriteErrorAsync(http, dialect, 401, "This server only answers this machine until an API key is created in Wicked's settings.", "invalid_api_key").ConfigureAwait(false);
                return false;
            }

            // A web page can aim a hostile name at this machine's loopback port (DNS rebinding); only addresses and localhost are served keyless.
            if (!IsLocalHost(http.Request.Host.Host))
            {
                note.Error = "host not allowed";
                await WriteErrorAsync(http, dialect, 403, "Use http://127.0.0.1 or http://localhost to reach this server.", "invalid_host").ConfigureAwait(false);
                return false;
            }

            return true;
        }

        var key = keys.Match(http.Request.Headers.Authorization.ToString(), http.Request.Headers["x-api-key"].ToString());
        if (key is null)
        {
            note.Error = "invalid API key";
            http.Response.Headers.WWWAuthenticate = "Bearer";
            await WriteErrorAsync(http, dialect, 401, "The API key is missing or wrong.", "invalid_api_key").ConfigureAwait(false);
            return false;
        }

        note.Client = key.Name;
        return true;
    }

    private static bool IsLocalHost(string host) =>
        host.Length == 0 || IPAddress.TryParse(host.Trim('[', ']'), out _) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    // ── /v1/models ───────────────────────────────────────────────────────────────────────────────

    private static async Task ListModelsAsync(HttpContext http, GatewayContext g)
    {
        await g.Services.Runtime.AttachExistingAsync(http.RequestAborted).ConfigureAwait(false);
        var state = g.Services.State;
        var running = g.Services.Runtime.Models;
        var data = new JsonArray();
        foreach (var (id, owner, created, aliasOf) in ModelResolver.Names(state))
        {
            var entry = new JsonObject { ["id"] = id, ["object"] = "model", ["created"] = created.ToUnixTimeSeconds(), ["owned_by"] = owner };
            var target = aliasOf ?? id;
            var live = running.FirstOrDefault(r => r.Id == target);
            entry["wicked"] = new JsonObject { ["state"] = (live?.State ?? RunState.Unloaded).ToString().ToLowerInvariant(), ["alias_of"] = aliasOf };
            data.Add(entry);
        }

        await WriteJsonAsync(http, 200, new JsonObject { ["object"] = "list", ["data"] = data }).ConfigureAwait(false);
    }

    private static async Task OneModelAsync(HttpContext http, GatewayContext g, string id)
    {
        var state = g.Services.State;
        var name = ModelResolver.Names(state).FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
        if (name.Id is null)
        {
            NoteOf(http).Error = "unknown model";
            await WriteErrorAsync(http, Dialect.OpenAi, 404, $"The model '{id}' does not exist.", "model_not_found", "model").ConfigureAwait(false);
            return;
        }

        NoteOf(http).Model = name.Id;
        await WriteJsonAsync(http, 200, new JsonObject { ["id"] = name.Id, ["object"] = "model", ["created"] = name.Created.ToUnixTimeSeconds(), ["owned_by"] = name.Owner }).ConfigureAwait(false);
    }

    // ── The proxy ────────────────────────────────────────────────────────────────────────────────

    private static async Task ProxyAsync(HttpContext http, GatewayContext g, string upstreamPath, Dialect dialect)
    {
        var note = NoteOf(http);
        var ct = http.RequestAborted;
        var config = g.Config;
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = config.MaxBodyBytes;
        byte[] body;
        try
        {
            if (http.Request.ContentLength > config.MaxBodyBytes)
            {
                // Read and drop what is coming (up to a point) so the client gets the answer instead of a reset connection.
                if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = null;
                var scrap = new byte[64 * 1024];
                long dropped = 0;
                int n;
                while (dropped < 256L * 1024 * 1024 && (n = await http.Request.Body.ReadAsync(scrap, ct).ConfigureAwait(false)) > 0) dropped += n;
                throw new BodyTooLarge();
            }

            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
            body = buffer.ToArray();
        }
        catch (Exception ex) when (ex is BodyTooLarge or Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            note.Error = "request too large";
            await WriteErrorAsync(http, dialect, 413, $"The request is larger than the {config.MaxBodyBytes / (1024 * 1024)} MB this server reads. It can be raised in Wicked's settings.", "request_too_large").ConfigureAwait(false);
            return;
        }

        JsonObject? json;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }

        if (json is null)
        {
            note.Error = "body is not a JSON object";
            await WriteErrorAsync(http, dialect, 400, "The request body must be a JSON object.", "invalid_json").ConfigureAwait(false);
            return;
        }

        var state = g.Services.State;
        var running = g.Services.Runtime.Models;
        await g.Services.Runtime.AttachExistingAsync(ct).ConfigureAwait(false);
        running = g.Services.Runtime.Models;
        var named = json["model"] is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
        string? runId;
        if (named is null)
        {
            // Without a model, the one that is loaded answers (llama.cpp's own habit); with none or several the client has to say.
            var loaded = running.Where(r => r.State == RunState.Loaded).ToList();
            runId = loaded.Count == 1 ? loaded[0].Id : null;
            if (runId is null)
            {
                note.Error = "no model named";
                await WriteErrorAsync(http, dialect, 400, "The request needs a \"model\". GET /v1/models lists what is installed.", "missing_model", "model").ConfigureAwait(false);
                return;
            }
        }
        else
        {
            runId = ModelResolver.Resolve(named, state, running);
            if (runId is null)
            {
                note.Model = named;
                note.Error = "unknown model";
                await WriteErrorAsync(http, dialect, 404, $"The model '{named}' is not installed. GET /v1/models lists what is.", "model_not_found", "model").ConfigureAwait(false);
                return;
            }
        }

        note.Model = runId;
        Uri router;
        try
        {
            if (config.AutoLoad || g.Control || running.Any(r => r.Id == runId && r.State is RunState.Loaded or RunState.Sleeping))
            {
                router = await g.Services.Runtime.EnsureLoadedAsync(runId, ct).ConfigureAwait(false);
            }
            else
            {
                note.Error = "model not loaded";
                await WriteErrorAsync(http, dialect, 400, $"The model '{runId}' is not loaded. Load it in Wicked, or turn on loading on first request.", "model_not_loaded", "model").ConfigureAwait(false);
                return;
            }
        }
        catch (ChatException ex)
        {
            note.Error = "model failed to load";
            await WriteErrorAsync(http, dialect, 503, ex.Message, "model_load_failed").ConfigureAwait(false);
            return;
        }

        json["model"] = runId;
        var stream = json["stream"] is JsonValue s && s.TryGetValue<bool>(out var wanted) && wanted;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(router, upstreamPath.TrimStart('/') + http.Request.QueryString)) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json.ToJsonString())) }; // not a path: a URL route
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        if (http.Request.Headers.TryGetValue("anthropic-version", out var version)) request.Headers.TryAddWithoutValidation("anthropic-version", version.ToString());
        if (stream) request.Headers.Accept.ParseAdd("text/event-stream");

        HttpResponseMessage response;
        try
        {
            response = await g.Upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            note.Error = "the model engine did not answer";
            await WriteErrorAsync(http, dialect, 502, $"The model engine did not answer: {ex.Message}", "engine_unreachable").ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            note.Error = "client closed the connection";
            return;
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (contentType is not null) http.Response.ContentType = contentType;
            var streamed = contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
            note.Streamed = streamed || stream;
            if (streamed)
            {
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";
            }
            else if (response.Content.Headers.ContentLength is { } length)
            {
                http.Response.ContentLength = length;
            }

            var sniffer = new UsageSniffer();
            var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using (source.ConfigureAwait(false))
                {
                    int n;
                    while ((n = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                    {
                        sniffer.Add(chunk.AsSpan(0, n));
                        // Written and flushed at once: the next read waits for the model, and the client should have this much already.
                        await http.Response.Body.WriteAsync(chunk.AsMemory(0, n), ct).ConfigureAwait(false);
                        await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
            {
                // The client went away, which ends the upstream request with it, or the engine did.
                note.Error = ct.IsCancellationRequested ? "client closed the connection" : "the reply was cut off";
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }

            (note.Prompt, note.Completion) = sniffer.Result();
            if (!response.IsSuccessStatusCode) note.Error ??= $"the engine answered {(int)response.StatusCode}";
        }
    }

    private sealed class BodyTooLarge : Exception;

    // ── Answers ──────────────────────────────────────────────────────────────────────────────────

    private static Note NoteOf(HttpContext http) => http.Items.TryGetValue(RequestKey, out var n) && n is Note note ? note : new Note();

    private static Task WriteJsonAsync(HttpContext http, int status, JsonNode body)
    {
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(body.ToJsonString());
    }

    internal static Task WriteErrorAsync(HttpContext http, Dialect dialect, int status, string message, string code, string? param = null)
    {
        if (http.Response.HasStarted) return Task.CompletedTask;
        JsonObject body = dialect == Dialect.Anthropic
            ? new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = AnthropicType(status), ["message"] = message } }
            : new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = OpenAiType(status), ["param"] = param, ["code"] = code } };
        return WriteJsonAsync(http, status, body);
    }

    private static string OpenAiType(int status) => status switch
    {
        401 => "authentication_error",
        429 => "rate_limit_error",
        >= 500 => "server_error",
        _ => "invalid_request_error",
    };

    private static string AnthropicType(int status) => status switch
    {
        400 => "invalid_request_error",
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        413 => "request_too_large",
        429 => "rate_limit_error",
        503 => "overloaded_error",
        _ => "api_error",
    };
}
