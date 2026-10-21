using System.Runtime.InteropServices;

namespace Wicked.Core.Terminal;

/// <summary>How to start a program on a pseudo-terminal.</summary>
/// <param name="FileName">The program; looked up on PATH when it has no directory part.</param>
/// <param name="Arguments">The arguments after the program's own name.</param>
/// <param name="WorkingDirectory">Where it starts; the current folder when empty.</param>
/// <param name="Environment">The complete environment of the program (not added to the parent's).</param>
/// <param name="Columns">The window width in cells.</param>
/// <param name="Rows">The window height in cells.</param>
public sealed record PtyOptions(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    int Columns = 80,
    int Rows = 24);

/// <summary>
/// Takes ownership of a buffer rented from <see cref="System.Buffers.ArrayPool{T}.Shared"/> holding what the program wrote.
/// It runs on the pty's reader thread and may block, which is how a program that writes faster than the terminal
/// reads is slowed down (the pty buffer fills and its write waits).
/// </summary>
public delegate void PtyDataHandler(byte[] rented, int length);

/// <summary>A program running on a pseudo-terminal: bytes in, bytes out, a window size, an exit code.</summary>
public interface IPty : IDisposable
{
    int ProcessId { get; }

    /// <summary>Queues bytes for the program's input. Never blocks the caller.</summary>
    void Write(ReadOnlySpan<byte> data);

    void Resize(int columns, int rows);

    /// <summary>Ends the program and everything it started in its process group.</summary>
    void Kill();

    /// <summary>Completes with the exit code once the program has ended and its last output was handed over.</summary>
    Task<int> Exited { get; }
}

/// <summary>Starts programs on a pty: <c>posix_spawn</c> with a new session on Linux and macOS, ConPTY on Windows.</summary>
public static class Pty
{
    public static IPty Start(PtyOptions options, PtyDataHandler onData)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return WindowsPty.Start(options, onData);
        return UnixPty.Start(options, onData);
    }

    /// <summary>
    /// The environment a terminal gives its programs: the parent's, plus what tells them what they are talking to.
    /// <paramref name="extra"/> wins over everything; a null value removes the variable.
    /// </summary>
    public static Dictionary<string, string> BuildEnvironment(IEnumerable<KeyValuePair<string, string?>>? extra = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value) env[key] = value;
        }

        // The program asks the terminal for its size; a stale value inherited from the parent would shadow the answer.
        env.Remove("LINES");
        env.Remove("COLUMNS");
        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "Wicked";
        env["TERM_PROGRAM_VERSION"] = BuildInfo.Version;
        if (extra is not null)
        {
            foreach (var (key, value) in extra)
            {
                if (value is null) env.Remove(key);
                else env[key] = value;
            }
        }

        return env;
    }
}
