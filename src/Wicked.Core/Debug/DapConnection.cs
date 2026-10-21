using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Wicked.Core.Lsp;

namespace Wicked.Core.Debug;

/// <summary>A request the debug adapter answered with an error, or that could not be answered because the adapter went away.</summary>
public sealed class DapException(string message, string? command = null, bool cancelled = false, int? errorId = null) : IOException(message)
{
    /// <summary>The request that failed.</summary>
    public string? Command { get; } = command;

    /// <summary>The adapter said the request was cancelled (an answer with the message <c>cancelled</c>).</summary>
    public bool Cancelled { get; } = cancelled;

    /// <summary>The adapter's own number for the error, when it gave one.</summary>
    public int? ErrorId { get; } = errorId;
}

/// <summary>
/// The Debug Adapter Protocol's wire: the same <c>Content-Length</c> framing the language servers use, carrying messages with
/// a <c>seq</c> number and a <c>type</c> of request, response or event. One reader task parses frames and dispatches them; one
/// writer task sends queued frames in order, so callers never block on the pipe. Requests are matched to answers by
/// <c>request_seq</c>; cancelling a request's token sends a <c>cancel</c> request when the adapter supports it and ends the
/// wait at once either way.
/// </summary>
/// <remarks>
/// Thread-safe. Event handlers run on the reader task in arrival order (a <c>stopped</c> must be seen after the
/// <c>continued</c> before it) and must not block; requests from the adapter (<c>runInTerminal</c>, <c>startDebugging</c>) run on
/// the thread pool.
/// </remarks>
public sealed class DapConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>();
    private readonly ConcurrentDictionary<long, (string Command, TaskCompletionSource<JsonElement> Answer)> _pending = new();
    private readonly ConcurrentDictionary<string, List<Action<JsonElement>>> _events = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _requests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private long _seq;
    private Task? _reader;
    private Task? _writer;
    private int _closed;

    public DapConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Raised once when the connection ends: the adapter closed it (null) or reading failed (the exception).</summary>
    public event EventHandler<Exception?>? Closed;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    /// <summary>Whether the adapter has said it understands <c>cancel</c> (the session sets it from the capabilities).</summary>
    public bool SupportsCancel { get; set; }

    /// <summary>Handles an event by name. Several handlers may listen to one event; they run in the order added.</summary>
    public void OnEvent(string name, Action<JsonElement> handler)
    {
        var list = _events.GetOrAdd(name, _ => []);
        lock (list) list.Add(handler);
    }

    /// <summary>Answers a request the adapter sends; a thrown <see cref="DapException"/> is its error answer.</summary>
    public void OnRequest(string command, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) => _requests[command] = handler;

    public void Start()
    {
        if (_reader is not null) throw new InvalidOperationException("The connection is already started");
        _reader = Task.Run(ReadLoopAsync);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Sends a request and waits for its answer's <c>body</c> (<see cref="JsonValueKind.Undefined"/> when it has none).</summary>
    public async Task<JsonElement> RequestAsync(string command, object? arguments = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var seq = Interlocked.Increment(ref _seq);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[seq] = (command, answer);
        if (IsClosed)
        {
            _pending.TryRemove(seq, out _);
            throw Gone(command);
        }

        Send(Frame(seq, "request", w =>
        {
            w.WriteString("command", command);
            if (arguments is null) return;
            w.WritePropertyName("arguments");
            JsonSerializer.Serialize(w, arguments, arguments.GetType(), DapJson.Options);
        }));

        await using var registration = ct.Register(() =>
        {
            if (!_pending.TryRemove(seq, out var waiting)) return;
            waiting.Answer.TrySetCanceled(ct);
            if (!SupportsCancel || command == "cancel" || IsClosed) return;
            var cancel = Interlocked.Increment(ref _seq);
            _outgoing.Writer.TryWrite(Frame(cancel, "request", w =>
            {
                w.WriteString("command", "cancel");
                w.WritePropertyName("arguments");
                w.WriteStartObject();
                w.WriteNumber("requestId", seq);
                w.WriteEndObject();
            }));
        }).ConfigureAwait(false);
        return await answer.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Close(null);
        _outgoing.Writer.TryComplete();
        await _lifetime.CancelAsync().ConfigureAwait(false);
        foreach (var task in new[] { _reader, _writer })
        {
            if (task is null) continue;
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Ending is the point.
            }
        }

        _lifetime.Dispose();
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────

    private void Send(byte[] frame)
    {
        if (!_outgoing.Writer.TryWrite(frame) && !IsClosed) throw new DapException("The connection is closed");
    }

    /// <summary>A whole frame: the header, then <c>{"seq":…, "type":…}</c> with the members <paramref name="body"/> writes.</summary>
    internal static byte[] Frame(long seq, string type, Action<Utf8JsonWriter> body)
    {
        var json = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", seq);
            w.WriteString("type", type);
            body(w);
            w.WriteEndObject();
        }

        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.WrittenCount.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
        var frame = new byte[header.Length + json.WrittenCount];
        header.CopyTo(frame, 0);
        json.WrittenSpan.CopyTo(frame.AsSpan(header.Length));
        return frame;
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var frame in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _output.WriteAsync(frame, _lifetime.Token).ConfigureAwait(false);
                if (_outgoing.Reader.Count == 0) await _output.FlushAsync(_lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Whatever stops the writer ends the connection: a writer that died quietly would leave every request waiting.
            Close(ex);
        }
    }

    // ── Reading ──────────────────────────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync()
    {
        var reader = new JsonRpcConnection.FrameReader(_input);
        Exception? failure = null;
        try
        {
            while (await reader.ReadAsync(_lifetime.Token).ConfigureAwait(false) is { } body)
            {
                JsonElement message;
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    message = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue; // A frame that is not JSON is dropped; the next one may be fine.
                }

                Dispatch(message);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        Close(failure);
    }

    private void Dispatch(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var typeElement)) return;
        switch (typeElement.GetString())
        {
            case "response":
                if (message.TryGetProperty("request_seq", out var rs) && rs.TryGetInt64(out var requestSeq) && _pending.TryRemove(requestSeq, out var waiting)) Answer(waiting.Command, waiting.Answer, message);
                break;
            case "event":
                if (message.TryGetProperty("event", out var name) && name.GetString() is { } eventName && _events.TryGetValue(eventName, out var handlers))
                {
                    var body = message.TryGetProperty("body", out var b) ? b : default;
                    Action<JsonElement>[] snapshot;
                    lock (handlers) snapshot = [.. handlers];
                    foreach (var handler in snapshot) handler(body);
                }

                break;
            case "request":
                if (message.TryGetProperty("command", out var command) && command.GetString() is { } commandName && message.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var theirSeq))
                {
                    var arguments = message.TryGetProperty("arguments", out var a) ? a : default;
                    _ = Task.Run(() => ServeAsync(commandName, theirSeq, arguments));
                }

                break;
        }
    }

    private static void Answer(string command, TaskCompletionSource<JsonElement> waiting, JsonElement message)
    {
        var success = !message.TryGetProperty("success", out var s) || s.ValueKind != JsonValueKind.False;
        if (success)
        {
            waiting.TrySetResult(message.TryGetProperty("body", out var body) ? body : default);
            return;
        }

        var text = message.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "The request failed";
        int? id = null;
        if (message.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object && b.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            if (error.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var number)) id = number;
            if (error.TryGetProperty("format", out var format) && format.ValueKind == JsonValueKind.String) text = Format(format.GetString()!, error.TryGetProperty("variables", out var vars) ? vars : default);
        }

        waiting.TrySetException(new DapException(text, command, cancelled: text == "cancelled", errorId: id));
    }

    /// <summary>An error's <c>format</c> with its <c>{name}</c> placeholders filled from <c>variables</c>.</summary>
    internal static string Format(string format, JsonElement variables)
    {
        if (variables.ValueKind != JsonValueKind.Object) return format;
        var text = format;
        foreach (var property in variables.EnumerateObject())
        {
            text = text.Replace("{" + property.Name + "}", property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString(), StringComparison.Ordinal);
        }

        return text;
    }

    private async Task ServeAsync(string command, long requestSeq, JsonElement arguments)
    {
        byte[] reply;
        var seq = Interlocked.Increment(ref _seq);
        try
        {
            if (!_requests.TryGetValue(command, out var handler)) throw new DapException($"Unhandled request {command}", command);
            var result = await handler(arguments, _lifetime.Token).ConfigureAwait(false);
            reply = Frame(seq, "response", w =>
            {
                w.WriteNumber("request_seq", requestSeq);
                w.WriteBoolean("success", true);
                w.WriteString("command", command);
                if (result.ValueKind == JsonValueKind.Undefined) return;
                w.WritePropertyName("body");
                result.WriteTo(w);
            });
        }
        catch (Exception ex)
        {
            reply = Frame(seq, "response", w =>
            {
                w.WriteNumber("request_seq", requestSeq);
                w.WriteBoolean("success", false);
                w.WriteString("command", command);
                w.WriteString("message", ex.Message);
            });
        }

        if (!IsClosed) _outgoing.Writer.TryWrite(reply);
    }

    private void Close(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        foreach (var seq in _pending.Keys)
        {
            if (_pending.TryRemove(seq, out var waiting)) waiting.Answer.TrySetException(Gone(waiting.Command));
        }

        _outgoing.Writer.TryComplete();
        Closed?.Invoke(this, failure);
    }

    private static DapException Gone(string command) => new("The debug adapter closed the connection", command);
}

/// <summary>The JSON settings every protocol record is written and read with: camelCase names, nulls left out.</summary>
public static class DapJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Reads <paramref name="element"/> as <typeparamref name="T"/>; <paramref name="fallback"/> when it is missing or null.</summary>
    public static T Read<T>(JsonElement element, T fallback) =>
        element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? fallback : element.Deserialize<T>(Options) ?? fallback;
}
