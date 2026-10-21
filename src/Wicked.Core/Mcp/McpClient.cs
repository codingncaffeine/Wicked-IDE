using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wicked.Core.Mcp;

/// <summary>
/// An MCP server as VS Code's mcp.json describes one: a program to start (stdio) or an address (streamable HTTP), with
/// its arguments, environment and headers.
/// </summary>
public sealed record McpServerConfig(string Name, string? Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env, string? Url, IReadOnlyDictionary<string, string> Headers, string? Cwd = null);

/// <summary>A tool a server offers: its name, what it does, its input schema.</summary>
public sealed record McpTool(string Server, string Name, string Description, JsonElement InputSchema);

/// <summary>A server refused or failed a request, in words fit for the model and the Output panel.</summary>
public sealed class McpException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// The client side of the Model Context Protocol (2025-06-18): initialize, the tool list (paged), and tool calls, over
/// newline-delimited JSON-RPC on a child's stdin/stdout, or over HTTP POSTs answered with JSON or an event stream. The
/// server's stderr goes to <see cref="Log"/>. Everything is async; nothing waits on the caller's thread.
/// </summary>
public sealed class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly McpServerConfig _config;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly HttpClient? _http;
    private Process? _process;
    private Task? _reader;
    private long _nextId;
    private string? _session;

    private McpClient(McpServerConfig config, HttpClient? http)
    {
        _config = config;
        _http = http;
    }

    public string Name => _config.Name;

    public IReadOnlyList<McpTool> Tools { get; private set; } = [];

    /// <summary>Lines the server wrote to stderr, and what the client noticed (for the Output panel).</summary>
    public event Action<string>? Log;

    public static async Task<McpClient> StartAsync(McpServerConfig config, HttpClient http, CancellationToken ct = default)
    {
        var client = new McpClient(config, config.Url is null ? null : http);
        try
        {
            if (config.Url is null) client.StartProcess();
            var init = new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "Wicked IDE", ["version"] = BuildInfo.Version },
            };
            await client.RequestAsync("initialize", init, ct).ConfigureAwait(false);
            await client.NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
            await client.RefreshToolsAsync(ct).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private void StartProcess()
    {
        var info = new ProcessStartInfo(_config.Command!)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        if (_config.Cwd is { } cwd) info.WorkingDirectory = cwd;
        foreach (var a in _config.Args) info.ArgumentList.Add(a);
        foreach (var (k, v) in _config.Env) info.Environment[k] = v;
        _process = Process.Start(info) ?? throw new McpException($"{_config.Command} did not start");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) Log?.Invoke(e.Data);
        };
        _process.BeginErrorReadLine();
        _reader = Task.Run(ReadLoopAsync);
    }

    /// <summary>Each line of the child's stdout is one message: answers complete their request, the rest is noted.</summary>
    private async Task ReadLoopAsync()
    {
        var output = _process!.StandardOutput;
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                Dispatch(line);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        foreach (var waiting in _pending.Values) waiting.TrySetException(new McpException($"{Name} stopped"));
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray()) Answer(item);
            }
            else
            {
                Answer(root);
            }
        }
        catch (JsonException)
        {
            Log?.Invoke($"[wicked] {Name} wrote a line that is not JSON: {json}");
        }
    }

    private void Answer(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var idElement) || !idElement.TryGetInt64(out var id)) return; // a notification or a request of the server's
        if (message.TryGetProperty("method", out _)) return;
        if (!_pending.TryRemove(id, out var waiting)) return;
        if (message.TryGetProperty("error", out var error))
        {
            var text = error.TryGetProperty("message", out var m) ? m.GetString() : error.ToString();
            waiting.TrySetException(new McpException($"{Name}: {text}"));
        }
        else
        {
            waiting.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
        }
    }

    private async Task NotifyAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        if (_http is not null)
        {
            using var response = await PostAsync(message, ct).ConfigureAwait(false);
            return;
        }

        await WriteAsync(message, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> RequestAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        if (_http is not null) return await HttpRequestAsync(message, id, ct).ConfigureAwait(false);

        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        await WriteAsync(message, ct).ConfigureAwait(false);
        using var reg = ct.Register(() =>
        {
            if (_pending.TryRemove(id, out var w)) w.TrySetCanceled(ct);
            _ = NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id }, CancellationToken.None);
        });
        return await waiting.Task.WaitAsync(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        var line = message.ToJsonString() + "\n";
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _process!.StandardInput.WriteAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new McpException($"{Name} is not running", ex);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task<HttpResponseMessage> PostAsync(JsonObject message, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _config.Url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        if (_session is not null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
        foreach (var (k, v) in _config.Headers) request.Headers.TryAddWithoutValidation(k, v);
        var response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _session = ids.FirstOrDefault() ?? _session;
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Accepted)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new McpException($"{Name} answered HTTP {status}");
        }

        return response;
    }

    /// <summary>An HTTP request: the answer is the JSON body, or the event in the stream that carries this id.</summary>
    private async Task<JsonElement> HttpRequestAsync(JsonObject message, long id, CancellationToken ct)
    {
        using var response = await PostAsync(message, ct).ConfigureAwait(false);
        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await foreach (var data in Wicked.Core.Chat.SseParser.ReadDataAsync(stream, ct).ConfigureAwait(false))
            {
                Dispatch(data);
                if (waiting.Task.IsCompleted) break;
            }
        }
        else
        {
            Dispatch(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }

        _pending.TryRemove(id, out _);
        if (!waiting.Task.IsCompleted) throw new McpException($"{Name} sent no answer");
        return await waiting.Task.ConfigureAwait(false);
    }

    /// <summary>The server's tools, every page of them.</summary>
    public async Task RefreshToolsAsync(CancellationToken ct = default)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        do
        {
            var result = await RequestAsync("tools/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }, ct).ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("tools", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in list.EnumerateArray())
                {
                    var schema = t.TryGetProperty("inputSchema", out var s) ? s.Clone() : JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
                    tools.Add(new McpTool(Name, t.GetProperty("name").GetString()!, t.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "", schema));
                }
            }

            cursor = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (cursor is not null);
        Tools = tools;
    }

    /// <summary>Calls a tool; its text content comes back joined, an error result as an <see cref="McpException"/>.</summary>
    public async Task<string> CallAsync(string tool, JsonElement arguments, CancellationToken ct = default)
    {
        var parameters = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText()) };
        var result = await RequestAsync("tools/call", parameters, ct).ConfigureAwait(false);
        var text = new StringBuilder();
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                var type = part.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                if (type == "text" && part.TryGetProperty("text", out var tx)) text.Append(tx.GetString()).Append('\n');
                else if (type is "image" or "audio") text.Append('[').Append(type).Append(" content]\n");
                else if (type == "resource" && part.TryGetProperty("resource", out var r) && r.TryGetProperty("text", out var rt)) text.Append(rt.GetString()).Append('\n');
            }
        }

        if (result.TryGetProperty("structuredContent", out var structured) && text.Length == 0) text.Append(structured.GetRawText());
        var isError = result.TryGetProperty("isError", out var e) && e.ValueKind == JsonValueKind.True;
        if (isError) throw new McpException(text.ToString().Trim());
        return text.ToString().TrimEnd();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var waiting in _pending.Values) waiting.TrySetCanceled();
        if (_process is { HasExited: false } p)
        {
            try
            {
                p.StandardInput.Close();
                if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
            }
        }

        if (_reader is not null) await _reader.ConfigureAwait(false);
        _process?.Dispose();
        _write.Dispose();
    }
}

/// <summary>Reads mcp.json (VS Code's shape: <c>{"servers": {"name": {"command", "args", "env"} | {"url", "headers"}}}</c>).</summary>
public static class McpConfig
{
    public static IReadOnlyList<McpServerConfig> Parse(string json, string? cwd = null)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using var doc = JsonDocument.Parse(json, options);
        var root = doc.RootElement;
        var servers = root.TryGetProperty("servers", out var s) ? s : root.TryGetProperty("mcpServers", out var m) ? m : root;
        var result = new List<McpServerConfig>();
        if (servers.ValueKind != JsonValueKind.Object) return result;
        foreach (var server in servers.EnumerateObject())
        {
            var v = server.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            if (v.TryGetProperty("disabled", out var off) && off.ValueKind == JsonValueKind.True) continue;
            string? Str(string name) => v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            Dictionary<string, string> Map(string name) => v.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Object
                ? x.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!)
                : [];
            var args = v.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            var command = Str("command");
            var url = Str("url");
            if (command is null && url is null) continue;
            result.Add(new McpServerConfig(server.Name, command, args, Map("env"), url, Map("headers"), Str("cwd") ?? cwd));
        }

        return result;
    }
}
