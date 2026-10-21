using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Wicked.Core;

namespace Wicked.App.Shell;

/// <summary>How the running window answered a launch handed to it.</summary>
public enum LaunchAnswer
{
    /// <summary>It opened what was asked (and, with --wait, says "closed" when the files are closed).</summary>
    Opened,

    /// <summary>The launch is to open a window of its own (a different folder than the running window has).</summary>
    OpenHere,
}

/// <summary>
/// One Wicked per profile, as VS Code has one: the first window listens on a socket in the profile's runtime folder; a later
/// launch hands its request to it and exits, unless it is told to open a window of its own. A socket left by a Wicked that
/// ended without cleaning up is taken over. Every read and write is off the UI thread; the request itself is handled on it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string Opened = "opened";
    private const string OpenHere = "here";
    private const string Closed = "closed";

    private readonly Socket _listener;
    private readonly string _path;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Socket listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    /// <summary>
    /// The profile's socket. A socket's path is limited to about a hundred bytes, so a profile deep in the folders (a long
    /// WICKED_HOME) has its socket in the user's private runtime folder under a name made from the profile's.
    /// </summary>
    public static string SocketPath(WickedPaths paths)
    {
        var path = Path.Combine(paths.Runtime, "window.sock");
        if (Encoding.UTF8.GetByteCount(path) <= 100) return path;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(paths.Runtime)))[..16].ToLowerInvariant();
        var folder = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime ? runtime : Path.GetTempPath();
        return Path.Combine(folder, $"wicked-{hash}.sock");
    }

    /// <summary>
    /// Hands <paramref name="request"/> to the running Wicked. Null when there is none (this launch becomes the window) or it
    /// answered OpenHere; otherwise the exit code for this launch (0, after the files were closed when it waits).
    /// </summary>
    public static async Task<(int? ExitCode, bool OpenHere)> HandOffAsync(WickedPaths paths, LaunchRequest request, TimeSpan timeout)
    {
        var path = SocketPath(paths);
        if (!File.Exists(path)) return (null, false);
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var connect = new CancellationTokenSource(timeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), connect.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return (null, false); // nobody listening: a socket left behind
        }

        await using var stream = new NetworkStream(socket, ownsSocket: false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
        var answer = await reader.ReadLineAsync();
        if (answer == OpenHere) return (null, true);
        if (answer != Opened) return (null, false);
        if (!request.Wait) return (0, false);
        var closed = await reader.ReadLineAsync(); // held until the files are closed, or the window ends
        return (closed == Closed ? 0 : 1, false);
    }

    /// <summary>
    /// Listens for later launches; <paramref name="handle"/> runs for each (it marshals to its own thread)
    /// and returns its answer and, for --wait, a task that ends when the files are closed.
    /// Null when another Wicked is already listening.
    /// </summary>
    public static SingleInstance? Listen(WickedPaths paths, Func<LaunchRequest, Task<(LaunchAnswer Answer, Task? Closed)>> handle)
    {
        var path = SocketPath(paths);
        Directory.CreateDirectory(paths.Runtime);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            if (File.Exists(path)) File.Delete(path); // a live one was asked first (HandOffAsync); this one is left behind
            listener.Bind(new UnixDomainSocketEndPoint(path));
            listener.Listen(8);
        }
        catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Single instance: could not listen on {path}: {ex.Message}");
            listener.Dispose();
            return null;
        }

        var instance = new SingleInstance(listener, path);
        _ = Task.Run(() => instance.AcceptAsync(handle));
        return instance;
    }

    private async Task AcceptAsync(Func<LaunchRequest, Task<(LaunchAnswer, Task?)>> handle)
    {
        while (!_stop.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client, handle));
        }
    }

    private async Task ServeAsync(Socket client, Func<LaunchRequest, Task<(LaunchAnswer, Task?)>> handle)
    {
        using var _ = client;
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            if (await reader.ReadLineAsync(_stop.Token) is not { } line || JsonSerializer.Deserialize<LaunchRequest>(line) is not { } request) return;
            var (answer, closed) = await handle(request);
            await writer.WriteLineAsync(answer == LaunchAnswer.OpenHere ? OpenHere : Opened);
            if (closed is not null)
            {
                await closed.WaitAsync(_stop.Token);
                await writer.WriteLineAsync(Closed);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or OperationCanceledException or ObjectDisposedException)
        {
            Log.Warn($"Single instance: a launch could not be handled: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Dispose();
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
