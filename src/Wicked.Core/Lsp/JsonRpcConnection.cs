using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Wicked.Core.Lsp;

/// <summary>A JSON-RPC error: from the other side (its code and message), or ours when the connection ended.</summary>
public sealed class JsonRpcException(int code, string message, JsonElement? data = null) : IOException(message)
{
    public int Code { get; } = code;

    public JsonElement? ErrorData { get; } = data;
}

/// <summary>The error codes JSON-RPC and LSP define.</summary>
public static class JsonRpcErrors
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    public const int ServerNotInitialized = -32002;
    public const int RequestFailed = -32803;
    public const int ServerCancelled = -32802;
    public const int ContentModified = -32801;
    public const int RequestCancelled = -32800;

    /// <summary>Ours: the connection closed before an answer came.</summary>
    public const int ConnectionClosed = -32099;
}

/// <summary>
/// JSON-RPC 2.0 with the Language Server Protocol's framing (a <c>Content-Length</c> header, a blank line, the JSON) over a
/// pair of streams — a language server's stdout and stdin. One reader task parses frames and dispatches them; one writer task
/// sends queued frames in order, so callers never block on the pipe. Requests are matched to answers by id; cancelling a
/// request's token sends <c>$/cancelRequest</c> and ends the wait at once.
/// </summary>
/// <remarks>
/// Thread-safe. Notification handlers run on the reader task in arrival order (diagnostics depend on it) and must not block;
/// request handlers from the other side run on the thread pool.
/// </remarks>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private const int MaxFrame = 256 * 1024 * 1024;

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly Channel<byte[]> _outgoing = Channel.CreateUnbounded<byte[]>();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _incoming = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Action<JsonElement>> _notifications = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _requests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private long _nextId;
    private Task? _reader;
    private Task? _writer;
    private int _closed;

    public JsonRpcConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    /// <summary>Raised once when the connection ends: the other side closed it (null) or reading failed (the exception).</summary>
    public event EventHandler<Exception?>? Closed;

    /// <summary>Notifications whose method has no handler (for a log).</summary>
    public event EventHandler<string>? Unhandled;

    public bool IsClosed => Volatile.Read(ref _closed) != 0;

    public void OnNotification(string method, Action<JsonElement> handler) => _notifications[method] = handler;

    /// <summary>Answers requests the other side sends; the handler's result is the reply, a <see cref="JsonRpcException"/> its error.</summary>
    public void OnRequest(string method, Func<JsonElement, CancellationToken, Task<JsonElement>> handler) => _requests[method] = handler;

    public void Start()
    {
        if (_reader is not null) throw new InvalidOperationException("The connection is already started");
        _reader = Task.Run(ReadLoopAsync);
        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Sends a request and waits for its answer (<see cref="JsonValueKind.Null"/> for a null result).</summary>
    public async Task<JsonElement> RequestAsync(string method, JsonElement? parameters, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        if (IsClosed) Fail(answer);
        Send(Frame(w =>
        {
            w.WriteNumber("id", id);
            w.WriteString("method", method);
            WriteParams(w, parameters);
        }));

        await using var registration = ct.Register(() =>
        {
            if (!_pending.TryRemove(id, out var waiting)) return;
            waiting.TrySetCanceled(ct);
            Send(Frame(w =>
            {
                w.WriteString("method", "$/cancelRequest");
                w.WritePropertyName("params");
                w.WriteStartObject();
                w.WriteNumber("id", id);
                w.WriteEndObject();
            }));
        }).ConfigureAwait(false);
        return await answer.Task.ConfigureAwait(false);
    }

    public void Notify(string method, JsonElement? parameters) => Send(Frame(w =>
    {
        w.WriteString("method", method);
        WriteParams(w, parameters);
    }));

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
        if (!_outgoing.Writer.TryWrite(frame) && !IsClosed) throw new JsonRpcException(JsonRpcErrors.ConnectionClosed, "The connection is closed");
    }

    private static void WriteParams(Utf8JsonWriter w, JsonElement? parameters)
    {
        if (parameters is not { ValueKind: not JsonValueKind.Undefined } p) return;
        w.WritePropertyName("params");
        p.WriteTo(w);
    }

    /// <summary>A whole frame: the header, then <c>{"jsonrpc":"2.0", …}</c> with the members <paramref name="body"/> writes.</summary>
    internal static byte[] Frame(Action<Utf8JsonWriter> body)
    {
        var json = new ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(json))
        {
            w.WriteStartObject();
            w.WriteString("jsonrpc", "2.0");
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
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            Close(ex);
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
        var reader = new FrameReader(_input);
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
                    continue; // A frame that is not JSON is dropped, as other clients do; the next one may be fine.
                }

                Dispatch(message);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            failure = ex; // the stream failed, a frame was malformed, or a handler threw: the connection ends either way
        }

        Close(failure);
    }

    private void Dispatch(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return;
        var hasMethod = message.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
        var hasId = message.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        var parameters = message.TryGetProperty("params", out var p) ? p : default;

        if (!hasMethod)
        {
            if (hasId && id.ValueKind == JsonValueKind.Number && id.TryGetInt64(out var number) && _pending.TryRemove(number, out var waiting)) Answer(waiting, message);
            return;
        }

        var method = methodElement.GetString()!;
        if (!hasId)
        {
            if (method == "$/cancelRequest" && parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("id", out var cancelled))
            {
                if (_incoming.TryRemove(cancelled.ToString(), out var cts)) cts.Cancel();
                return;
            }

            if (_notifications.TryGetValue(method, out var handler)) handler(parameters);
            else Unhandled?.Invoke(this, method);
            return;
        }

        _ = Task.Run(() => ServeAsync(method, id.Clone(), parameters));
    }

    private static void Answer(TaskCompletionSource<JsonElement> waiting, JsonElement message)
    {
        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : JsonRpcErrors.InternalError;
            var text = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "Unknown error";
            waiting.TrySetException(new JsonRpcException(code, text, error.TryGetProperty("data", out var d) ? d : null));
            return;
        }

        waiting.TrySetResult(message.TryGetProperty("result", out var result) ? result : default);
    }

    private async Task ServeAsync(string method, JsonElement id, JsonElement parameters)
    {
        var key = id.ToString();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _incoming[key] = cts;
        byte[] reply;
        try
        {
            if (!_requests.TryGetValue(method, out var handler)) throw new JsonRpcException(JsonRpcErrors.MethodNotFound, $"Unhandled method {method}");
            var result = await handler(parameters, cts.Token).ConfigureAwait(false);
            reply = Frame(w =>
            {
                WriteId(w, id);
                w.WritePropertyName("result");
                if (result.ValueKind == JsonValueKind.Undefined) w.WriteNullValue();
                else result.WriteTo(w);
            });
        }
        catch (Exception ex)
        {
            var (code, text) = ex switch
            {
                JsonRpcException rpc => (rpc.Code, rpc.Message),
                OperationCanceledException => (JsonRpcErrors.RequestCancelled, "Cancelled"),
                _ => (JsonRpcErrors.InternalError, ex.Message),
            };
            reply = Frame(w =>
            {
                WriteId(w, id);
                w.WritePropertyName("error");
                w.WriteStartObject();
                w.WriteNumber("code", code);
                w.WriteString("message", text);
                w.WriteEndObject();
            });
        }
        finally
        {
            _incoming.TryRemove(key, out _);
        }

        if (!IsClosed) _outgoing.Writer.TryWrite(reply);
    }

    private static void WriteId(Utf8JsonWriter w, JsonElement id)
    {
        w.WritePropertyName("id");
        id.WriteTo(w);
    }

    private void Close(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiting)) Fail(waiting);
        }

        foreach (var cts in _incoming.Values) cts.Cancel();
        _outgoing.Writer.TryComplete();
        Closed?.Invoke(this, failure);
    }

    private static void Fail(TaskCompletionSource<JsonElement> waiting) =>
        waiting.TrySetException(new JsonRpcException(JsonRpcErrors.ConnectionClosed, "The language server closed the connection"));

    /// <summary>Reads LSP frames: header lines up to a blank line (only <c>Content-Length</c> matters), then that many bytes.</summary>
    internal sealed class FrameReader(Stream input)
    {
        private byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        /// <summary>The next frame's body, or null at the end of the stream.</summary>
        public async ValueTask<byte[]?> ReadAsync(CancellationToken ct)
        {
            var length = -1;
            while (true)
            {
                var line = await ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) return null;
                if (line.Length == 0)
                {
                    if (length >= 0) break;
                    continue; // Stray blank lines between frames.
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line.AsSpan(0, colon).Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length > MaxFrame)
                    {
                        throw new InvalidDataException($"Bad Content-Length header: {line}");
                    }
                }
            }

            var body = new byte[length];
            var copied = 0;
            while (copied < length)
            {
                if (_start == _end && !await FillAsync(ct).ConfigureAwait(false)) return null;
                var n = Math.Min(length - copied, _end - _start);
                _buffer.AsSpan(_start, n).CopyTo(body.AsSpan(copied));
                _start += n;
                copied += n;
            }

            return body;
        }

        private async ValueTask<string?> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                var newline = _buffer.AsSpan(_start, _end - _start).IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    var line = Encoding.ASCII.GetString(_buffer, _start, newline).TrimEnd('\r');
                    _start += newline + 1;
                    return line;
                }

                if (_end - _start > 8192) throw new InvalidDataException("A header line is too long");
                if (!await FillAsync(ct).ConfigureAwait(false)) return null;
            }
        }

        private async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0)
            {
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
            var n = await input.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
            _end += n;
            return n > 0;
        }
    }
}
