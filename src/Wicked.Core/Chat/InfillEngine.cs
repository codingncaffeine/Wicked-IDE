using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Wicked.Core.Chat;

/// <summary>Another file's text given to the model as context for a completion (llama.cpp's <c>input_extra</c>).</summary>
public sealed record InfillContext(string FileName, string Text);

/// <summary>
/// A fill-in-the-middle request: the text before and after the caret, nearby files, and limits that keep a suggestion
/// quick (a few lines, a time budget). <c>Model</c> is the runtime id of a model trained for FIM (a coder).
/// </summary>
public sealed record InfillRequest(
    string Model,
    string Prefix,
    string Suffix,
    IReadOnlyList<InfillContext> Extra,
    int MaxTokens = 96,
    int TimeBudgetMs = 1500,
    double Temperature = 0.1,
    IReadOnlyList<string>? Stop = null);

/// <summary>What came back: the text to insert, and how fast (for the status bar).</summary>
public sealed record InfillResult(string Text, int Tokens, double? TokensPerSecond, bool Truncated);

/// <summary>Ghost-text completions from a local model (D18): llama-server's <c>/infill</c>, loading the model first when needed.</summary>
public interface IInfillEngine
{
    Task<InfillResult> CompleteAsync(InfillRequest request, CancellationToken ct = default);
}

/// <summary>
/// Completions through the router's <c>/infill</c> endpoint (llama.cpp's FIM, with the model's own FIM tokens). The
/// request is cancelled as soon as the user types again; <c>cache_prompt</c> keeps the shared prefix in the model's
/// cache, so the next keystroke's request reuses nearly all of the work.
/// </summary>
public sealed class InfillEngine(ModelRuntime runtime, HttpClient http) : IInfillEngine
{
    public async Task<InfillResult> CompleteAsync(InfillRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var baseAddress = await runtime.EnsureLoadedAsync(request.Model, ct).ConfigureAwait(false);
        var body = await Task.Run(() => Body(request), ct).ConfigureAwait(false);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, "infill"))
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ChatException($"The model engine did not answer ({ex.Message}). {runtime.RouterHint()}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ChatException(ErrorOf(text, (int)response.StatusCode));
            return await Task.Run(() => Parse(text), ct).ConfigureAwait(false);
        }
    }

    internal static string Body(InfillRequest r)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("model", r.Model);
            w.WriteString("input_prefix", r.Prefix);
            w.WriteString("input_suffix", r.Suffix);
            w.WriteString("prompt", "");
            w.WriteStartArray("input_extra");
            foreach (var e in r.Extra)
            {
                w.WriteStartObject();
                w.WriteString("filename", e.FileName);
                w.WriteString("text", e.Text);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteNumber("n_predict", r.MaxTokens);
            w.WriteNumber("t_max_predict_ms", r.TimeBudgetMs);
            w.WriteNumber("temperature", r.Temperature);
            w.WriteNumber("top_k", 40);
            w.WriteNumber("top_p", 0.95);
            w.WriteBoolean("cache_prompt", true);
            w.WriteBoolean("stream", false);
            w.WriteStartArray("stop");
            foreach (var s in r.Stop ?? []) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static InfillResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var content = root.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
        var tokens = root.TryGetProperty("tokens_predicted", out var t) && t.TryGetInt32(out var n) ? n : 0;
        double? speed = root.TryGetProperty("timings", out var timings) && timings.TryGetProperty("predicted_per_second", out var p) && p.TryGetDouble(out var v) ? v : null;
        var truncated = root.TryGetProperty("stop_type", out var st) && st.GetString() == "limit";
        return new InfillResult(content.Replace("\r\n", "\n", StringComparison.Ordinal), tokens, speed, truncated);
    }

    private static string ErrorOf(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)) return m.GetString() ?? $"The engine answered {status}";
        }
        catch (JsonException)
        {
        }

        return $"The engine answered {status}";
    }
}
