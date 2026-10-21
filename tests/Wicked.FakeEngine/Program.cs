using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

// A stand-in for llama-server in router mode: just enough of its model-management and chat API for the tests.
// Behaviour is scripted by fake.json in the engine folder (re-read on every request), and everything it is asked is
// appended to fake-log.jsonl beside it, so a test can assert on what the runtime sent.

var arguments = args.ToList();
string Take(string name)
{
    var i = arguments.IndexOf(name);
    if (i < 0 || i + 1 >= arguments.Count) throw new ArgumentException($"missing {name}");
    return arguments[i + 1];
}

var folder = Take("--fake-dir");
var port = int.Parse(Take("--port"), CultureInfo.InvariantCulture);
var presetPath = Take("--models-preset");
var logPath = Path.Combine(folder, "fake-log.jsonl");
var logLock = new object();
var models = new Dictionary<string, Model>(StringComparer.Ordinal);
var subscribers = new ConcurrentDictionary<Channel<string>, byte>();
var preset = "";

void Log(string kind, Action<JsonObject>? more = null)
{
    var entry = new JsonObject { ["t"] = kind };
    more?.Invoke(entry);
    lock (logLock) File.AppendAllText(logPath, entry.ToJsonString() + "\n");
}

JsonNode Script()
{
    try
    {
        return JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "fake.json"))) ?? new JsonObject();
    }
    catch (Exception ex) when (ex is IOException or JsonException)
    {
        return new JsonObject();
    }
}

Dictionary<string, Dictionary<string, string>> ReadPreset()
{
    preset = File.ReadAllText(presetPath);
    var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
    Dictionary<string, string>? current = null;
    foreach (var raw in preset.Split('\n'))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line[0] == ';' || line.StartsWith("version", StringComparison.Ordinal)) continue;
        if (line[0] == '[' && line[^1] == ']') sections[line[1..^1]] = current = [];
        else if (current is not null && line.IndexOf('=', StringComparison.Ordinal) is var eq and > 0) current[line[..eq].Trim()] = line[(eq + 1)..].Trim();
    }

    sections.Remove("*", out var shared);
    return sections.ToDictionary(s => s.Key, s => new Dictionary<string, string>(shared ?? [], StringComparer.Ordinal).Concat(s.Value).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Last().Value), StringComparer.Ordinal);
}

void Sync()
{
    var sections = ReadPreset();
    lock (models)
    {
        foreach (var (name, values) in sections)
        {
            if (models.TryGetValue(name, out var m)) m.Preset = values;
            else models[name] = new Model { Preset = values };
        }

        foreach (var gone in models.Keys.Where(k => !sections.ContainsKey(k)).ToList()) models.Remove(gone);
    }
}

void Publish(string model, string kind, JsonNode? data = null)
{
    var ev = new JsonObject { ["model"] = model, ["event"] = kind };
    if (data is not null) ev["data"] = data;
    var text = ev.ToJsonString();
    foreach (var channel in subscribers.Keys) channel.Writer.TryWrite(text);
}

JsonObject ModelJson(string name, Model m)
{
    var status = new JsonObject { ["value"] = m.Status };
    var argList = new JsonArray("llama-server");
    if (m.Preset.TryGetValue("c", out var c))
    {
        argList.Add("--ctx-size");
        argList.Add(c);
    }
    status["args"] = argList;
    if (m.Failed)
    {
        status["failed"] = true;
        status["exit_code"] = m.Exit;
    }

    return new JsonObject
    {
        ["id"] = name,
        ["path"] = m.Preset.GetValueOrDefault("model"),
        ["status"] = status,
        ["architecture"] = new JsonObject
        {
            ["input_modalities"] = m.Preset.ContainsKey("mmproj") ? new JsonArray("text", "image") : new JsonArray("text"),
            ["output_modalities"] = new JsonArray("text"),
        },
    };
}

async Task DoLoadAsync(string name)
{
    var script = Script();
    Model m;
    lock (models)
    {
        m = models[name];
        m.Status = "loading";
        m.Failed = false;
    }

    Publish(name, "model_status", new JsonObject { ["status"] = "loading" });
    var half = TimeSpan.FromMilliseconds((script["loadMs"]?.GetValue<double>() ?? 20) / 2);
    await Task.Delay(half);
    Publish(name, "model_status", new JsonObject
    {
        ["status"] = "loading",
        ["progress"] = new JsonObject { ["stages"] = new JsonArray("text_model"), ["current"] = "text_model", ["value"] = 0.5 },
    });
    await Task.Delay(half);
    if (script["failLoad"] is JsonArray fails && fails.Any(f => f?.GetValue<string>() == name))
    {
        Console.Error.WriteLine($"[{Environment.ProcessId + 1}] 0.00.000.100 E llama_model_load: error loading model: fake failure for {name}");
        Console.Error.WriteLine($"0.00.000.200 I srv           run: instance name={name} exited with status 1");
        lock (models)
        {
            m.Status = "unloaded";
            m.Failed = true;
            m.Exit = 1;
        }

        Publish(name, "model_status", new JsonObject { ["status"] = "unloaded" });
        return;
    }

    lock (models) m.Status = "loaded";
    Publish(name, "model_status", new JsonObject { ["status"] = "loaded", ["info"] = new JsonObject() });
}

PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => Environment.Exit(0));
Sync();
Log("start", e =>
{
    var shown = new JsonArray();
    for (var i = 0; i < arguments.Count; i++)
    {
        if (arguments[i] == "--fake-dir") i++;
        else shown.Add(arguments[i]);
    }

    e["args"] = shown;
    e["preset"] = preset;
    e["pid"] = Environment.ProcessId;
});

var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
var app = builder.Build();

IResult Json(int status, JsonNode body) => Results.Text(body.ToJsonString(), "application/json", Encoding.UTF8, status);

app.MapGet("/health", () => Json(200, new JsonObject { ["status"] = "ok" }));

app.MapGet("/models", (HttpContext http) =>
{
    if (http.Request.Query.ContainsKey("reload"))
    {
        Log("reload");
        Sync();
    }

    lock (models) return Json(200, new JsonObject { ["data"] = new JsonArray(models.Select(m => (JsonNode?)ModelJson(m.Key, m.Value)).ToArray()) });
});

app.MapGet("/props", (HttpContext http) =>
{
    Model? m;
    lock (models) models.TryGetValue(http.Request.Query["model"].ToString(), out m);
    var n = m is not null && m.Preset.TryGetValue("c", out var c) ? int.Parse(c, CultureInfo.InvariantCulture) : 4096;
    return Json(200, new JsonObject { ["default_generation_settings"] = new JsonObject { ["n_ctx"] = n } });
});

app.MapGet("/models/sse", async (HttpContext http) =>
{
    var channel = Channel.CreateUnbounded<string>();
    subscribers[channel] = 0;
    http.Response.ContentType = "text/event-stream";
    try
    {
        await http.Response.WriteAsync(": connected\n\n");
        await http.Response.Body.FlushAsync();
        while (!http.RequestAborted.IsCancellationRequested)
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
            wait.CancelAfter(500);
            try
            {
                await http.Response.WriteAsync($"data: {await channel.Reader.ReadAsync(wait.Token)}\n\n");
            }
            catch (OperationCanceledException) when (!http.RequestAborted.IsCancellationRequested)
            {
                await http.Response.WriteAsync(": keep-alive\n\n");
            }

            await http.Response.Body.FlushAsync();
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
    {
        // The client went away.
    }
    finally
    {
        subscribers.TryRemove(channel, out _);
    }
});

async Task LoadOrUnload(HttpContext http, bool load)
{
    var name = (await JsonNode.ParseAsync(http.Request.Body))?["model"]?.GetValue<string>() ?? "";
    bool known;
    lock (models) known = models.ContainsKey(name);
    if (!known)
    {
        await Json(400, new JsonObject { ["error"] = new JsonObject { ["code"] = 400, ["message"] = "model not found", ["type"] = "invalid_request_error" } }).ExecuteAsync(http);
        return;
    }

    if (load)
    {
        Log("load", e => e["model"] = name);
        _ = Task.Run(() => DoLoadAsync(name));
    }
    else
    {
        Log("unload", e => e["model"] = name);
        lock (models) models[name].Status = "unloaded";
        Publish(name, "model_status", new JsonObject { ["status"] = "unloaded" });
    }

    await Json(200, new JsonObject { ["success"] = true }).ExecuteAsync(http);
}

app.MapPost("/models/load", (HttpContext http) => LoadOrUnload(http, load: true));
app.MapPost("/models/unload", (HttpContext http) => LoadOrUnload(http, load: false));

app.MapPost("/v1/chat/completions", async (HttpContext http) =>
{
    var body = await JsonNode.ParseAsync(http.Request.Body) ?? new JsonObject();
    Log("chat", e => e["body"] = body.DeepClone());
    var script = Script();
    var name = body["model"]?.GetValue<string>() ?? "";
    bool loaded;
    lock (models) loaded = models.TryGetValue(name, out var m) && m.Status == "loaded";
    if (!loaded)
    {
        http.Response.StatusCode = 400;
        await http.Response.WriteAsync(new JsonObject { ["error"] = new JsonObject { ["code"] = 400, ["message"] = $"model '{name}' is not loaded", ["type"] = "invalid_request_error" } }.ToJsonString());
        return;
    }

    if (script["chatStatus"] is { } status)
    {
        http.Response.StatusCode = status.GetValue<int>();
        http.Response.ContentType = "application/json";
        await http.Response.WriteAsync((script["chatBody"] ?? new JsonObject()).ToJsonString());
        return;
    }

    http.Response.ContentType = "text/event-stream";
    var delay = TimeSpan.FromMilliseconds(script["chatDelayMs"]?.GetValue<double>() ?? 0);
    var items = script["chat"] as JsonArray ?? new JsonArray("[DONE]");
    try
    {
        foreach (var item in items)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var text))
            {
                if (text == "@crash") Environment.Exit(7);
                if (text == "@hangup")
                {
                    http.Abort();
                    return;
                }

                await http.Response.WriteAsync($"data: {text}\n\n", http.RequestAborted);
            }
            else
            {
                await http.Response.WriteAsync($"data: {item?.ToJsonString()}\n\n", http.RequestAborted);
            }

            await http.Response.Body.FlushAsync(http.RequestAborted);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, http.RequestAborted);
        }
    }
    catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
    {
        Log("chat-aborted");
    }
});

Console.Error.WriteLine($"fake llama-server listening on {port.ToString(CultureInfo.InvariantCulture)}");
await app.RunAsync();

internal sealed class Model
{
    public string Status { get; set; } = "unloaded";

    public bool Failed { get; set; }

    public int? Exit { get; set; }

    public Dictionary<string, string> Preset { get; set; } = [];
}
