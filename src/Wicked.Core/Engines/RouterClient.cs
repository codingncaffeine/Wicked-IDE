using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Wicked.Core.Engines;

public enum RouterModelStatus
{
    Unknown,
    Unloaded,
    Loading,
    Loaded,
    Sleeping,
    Downloading,
    Incomplete,
}

/// <summary>A model as <c>GET /models</c> lists it. <c>Failed</c> means its last load exited with <c>ExitCode</c>.</summary>
public sealed record RouterModel(
    string Id,
    string? Path,
    RouterModelStatus Status,
    IReadOnlyList<string> Args,
    bool Failed,
    int? ExitCode,
    IReadOnlyList<string> InputModalities);

/// <summary>
/// One server-sent event from <c>/models/sse</c>: <c>model_status</c> (with <see cref="Status"/> and, while loading,
/// <see cref="LoadProgress"/> 0..1), <c>download_progress</c>, <c>model_remove</c>, or <c>models_reload</c> (model <c>*</c>).
/// </summary>
public sealed record RouterEvent(string Model, string Kind, RouterModelStatus? Status, double? LoadProgress, string RawData);

public sealed class RouterException(string message) : IOException(message);

/// <summary>The router's model-management API (PLAN §6), spoken over loopback HTTP.</summary>
public sealed class RouterClient(HttpClient http, Func<Uri> baseAddress)
{
    public RouterClient(HttpClient http, Uri baseAddress)
        : this(http, () => baseAddress)
    {
    }

    public async Task<IReadOnlyList<RouterModel>> GetModelsAsync(bool reload = false, CancellationToken ct = default)
    {
        using var response = await http.GetAsync(new Uri(baseAddress(), reload ? "models?reload=1" : "models"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return [.. data.EnumerateArray().Select(ParseModel)];
    }

    public Task LoadAsync(string model, CancellationToken ct = default) => PostAsync("models/load", model, ct);

    public Task UnloadAsync(string model, CancellationToken ct = default) => PostAsync("models/unload", model, ct);

    /// <summary>Follows <c>/models/sse</c> until the token is cancelled or the connection ends. Events are yielded as they arrive.</summary>
    public async IAsyncEnumerable<RouterEvent> EventsAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress(), "models/sse"));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), Encoding.UTF8);
        var data = new StringBuilder();
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0 && ParseEvent(data.ToString()) is { } ev) yield return ev;
                data.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line.AsSpan(5).TrimStart());
            }
        }

        if (data.Length > 0 && ParseEvent(data.ToString()) is { } last) yield return last;
    }

    public static RouterModelStatus ParseStatus(string? value) => value switch
    {
        "unloaded" => RouterModelStatus.Unloaded,
        "loading" => RouterModelStatus.Loading,
        "loaded" => RouterModelStatus.Loaded,
        "sleeping" => RouterModelStatus.Sleeping,
        "downloading" => RouterModelStatus.Downloading,
        "incomplete" => RouterModelStatus.Incomplete,
        _ => RouterModelStatus.Unknown,
    };

    internal static RouterEvent? ParseEvent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
            var kind = root.TryGetProperty("event", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "";
            RouterModelStatus? status = null;
            double? progress = null;
            if (kind == "model_status" && root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                if (d.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String) status = ParseStatus(s.GetString());
                if (d.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number) progress = v.GetDouble();
            }

            return new RouterEvent(model, kind, status, progress, json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static RouterModel ParseModel(JsonElement e)
    {
        var status = RouterModelStatus.Unknown;
        var args = new List<string>();
        var failed = false;
        int? exit = null;
        if (e.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            if (s.TryGetProperty("value", out var v)) status = ParseStatus(v.GetString());
            if (s.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array) args.AddRange(a.EnumerateArray().Select(x => x.GetString() ?? ""));
            failed = s.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.True;
            if (s.TryGetProperty("exit_code", out var c) && c.ValueKind == JsonValueKind.Number) exit = c.GetInt32();
        }

        var modalities = e.TryGetProperty("architecture", out var arch) && arch.ValueKind == JsonValueKind.Object
                         && arch.TryGetProperty("input_modalities", out var im) && im.ValueKind == JsonValueKind.Array
            ? im.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
            : [];
        return new RouterModel(
            e.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            e.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String ? path.GetString() : null,
            status, args, failed, exit, modalities);
    }

    private async Task PostAsync(string path, string model, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { model }), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(new Uri(baseAddress(), path), content, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new RouterException($"{path} answered {(int)response.StatusCode}: {body}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
            {
                throw new RouterException($"{path} for {model} was refused: {body}");
            }
        }
        catch (JsonException)
        {
            // A body that is not JSON with a 2xx status is treated as success.
        }
    }
}
