using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wicked.Core.Chat;

/// <summary>
/// Streams chat replies from the router's OpenAI-compatible <c>/v1/chat/completions</c>. The reply is parsed event by event as it
/// arrives. Cancelling closes the connection, which is what makes the engine stop generating, and ends the stream with
/// <c>cancelled</c>. Every failure a person can act on becomes a <see cref="ChatException"/> with a sentence to show.
/// </summary>
public sealed class ChatEngine(ModelRuntime runtime, HttpClient http) : IChatEngine
{
    public async IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri? baseAddress = null;
        try
        {
            baseAddress = await runtime.EnsureLoadedAsync(request.Model, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelled while the model was loading: reported below, outside the catch.
        }

        if (baseAddress is null)
        {
            yield return new ChatFinished("cancelled");
            yield break;
        }

        var body = await Task.Run(() => BuildBody(request), ct).ConfigureAwait(false);
        var address = new Uri(baseAddress, "v1/chat/completions");
        HttpResponseMessage? response = null;
        try
        {
            try
            {
                response = await SendAsync(address, body, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelled before the first byte came back.
            }
            catch (HttpRequestException ex)
            {
                throw new ChatException($"The model engine did not answer ({ex.Message}). {runtime.RouterHint()}", ex);
            }

            if (response is null)
            {
                yield return new ChatFinished("cancelled");
                yield break;
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ChatException(await ErrorTextAsync(response, ct).ConfigureAwait(false));
            }

            var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var events = SseParser.ReadDataAsync(stream, ct).GetAsyncEnumerator(ct);
            var contextSize = runtime.ContextSizeOf(request.Model);
            string? reason = null;
            ChatUsage? usage = null;
            var cancelled = false;
            while (true)
            {
                string? data = null;
                try
                {
                    if (await events.MoveNextAsync().ConfigureAwait(false)) data = events.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw new ChatException($"The model engine stopped in the middle of the reply. {runtime.RouterHint()}", ex);
                }

                if (cancelled || data is null || data == "[DONE]") break;
                var parsed = Parse(data, contextSize);
                if (parsed.Delta is { } delta) yield return delta;
                if (parsed.Tools is { } calls)
                {
                    foreach (var call in calls) yield return call;
                }

                if (parsed.Reasoning is { } thinking) yield return thinking;
                reason = parsed.FinishReason ?? reason;
                usage = parsed.Usage ?? usage;
            }

            if (cancelled)
            {
                yield return new ChatFinished("cancelled");
                yield break;
            }

            // The finish reason comes before the usage event, but the contract puts usage first.
            if (reason is null) throw new ChatException($"The model engine closed the connection before the reply was complete. {runtime.RouterHint()}");
            if (usage is not null) yield return usage;
            yield return new ChatFinished(reason);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// Sends the request, and once more when it failed before any reply because the pooled connection had been closed by the server
    /// (nothing reached it, so nothing was generated).
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri address, string body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, address) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            try
            {
                return await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt == 0 && ex.InnerException is HttpIOException)
            {
                // Stale pooled connection.
            }
        }
    }

    internal readonly record struct Parsed(ContentDelta? Delta, ReasoningDelta? Reasoning, string? FinishReason, ChatUsage? Usage, IReadOnlyList<ToolCallDelta>? Tools = null);

    /// <summary>Reads one event of the stream. An <c>error</c> event throws.</summary>
    internal static Parsed Parse(string data, int? contextSize)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException ex)
        {
            throw new ChatException("The model engine sent a reply that could not be read.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return default;
            if (root.TryGetProperty("error", out var error))
            {
                throw new ChatException(error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : error.ToString());
            }

            ContentDelta? content = null;
            ReasoningDelta? reasoning = null;
            string? finish = null;
            List<ToolCallDelta>? tools = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 } text) content = new ContentDelta(text);
                    if (delta.TryGetProperty("reasoning_content", out var r) && r.ValueKind == JsonValueKind.String && r.GetString() is { Length: > 0 } thought) reasoning = new ReasoningDelta(thought);
                    if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var call in calls.EnumerateArray())
                        {
                            var index = call.TryGetProperty("index", out var i) && i.TryGetInt32(out var n) ? n : 0;
                            var id = call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
                            string? name = null;
                            var arguments = "";
                            if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                            {
                                if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) name = nm.GetString();
                                if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String) arguments = a.GetString() ?? "";
                            }

                            (tools ??= []).Add(new ToolCallDelta(index, id, name, arguments));
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var f) && f.ValueKind == JsonValueKind.String) finish = f.GetString();
            }

            ChatUsage? usage = null;
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                double? speed = root.TryGetProperty("timings", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("predicted_per_second", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : null;
                usage = new ChatUsage(Int(u, "prompt_tokens"), Int(u, "completion_tokens"), speed, contextSize);
            }

            return new Parsed(content, reasoning, finish, usage, tools);
        }
    }

    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    internal static string BuildBody(ChatRequest request)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages)
        {
            var role = m.Role switch { ChatRole.System => "system", ChatRole.Assistant => "assistant", ChatRole.Tool => "tool", _ => "user" };
            if (m.Role == ChatRole.Tool)
            {
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = m.ToolCallId ?? "", ["content"] = m.Content });
                continue;
            }

            if (m.ToolCalls is { Count: > 0 } toolCalls)
            {
                var array = new JsonArray();
                foreach (var call in toolCalls)
                {
                    array.Add(new JsonObject
                    {
                        ["id"] = call.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
                    });
                }

                messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = m.Content.Length > 0 ? m.Content : null, ["tool_calls"] = array });
                continue;
            }

            if (m.Images is { Count: > 0 })
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = m.Content } };
                foreach (var image in m.Images)
                {
                    parts.Add(new JsonObject
                    {
                        ["type"] = "image_url",
                        ["image_url"] = new JsonObject { ["url"] = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data)}" },
                    });
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = parts });
            }
            else
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = m.Content });
            }
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (request.Tools is { Count: > 0 } tools)
        {
            var list = new JsonArray();
            foreach (var tool in tools)
            {
                list.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["parameters"] = JsonNode.Parse(tool.Parameters.GetRawText()),
                    },
                });
            }

            body["tools"] = list;
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = true;
        }

        if (request.Sampling is { } s)
        {
            if (s.Temperature is { } t) body["temperature"] = t;
            if (s.TopP is { } p) body["top_p"] = p;
            if (s.MaxTokens is { } n) body["max_tokens"] = n;
        }

        return body.ToJsonString();
    }

    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string text;
        try
        {
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            text = "";
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } message) return message;
                if (error.ValueKind == JsonValueKind.String) return error.GetString()!;
            }
        }
        catch (JsonException)
        {
            // Not JSON: the status line will do.
        }

        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        return text.Length is > 0 and < 300 ? $"The model engine answered {code}: {text.Trim()}" : $"The model engine answered {code} {response.ReasonPhrase}";
    }
}
