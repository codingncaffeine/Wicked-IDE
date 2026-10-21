using System.Runtime.InteropServices;
using System.Text;

namespace Wicked.Core.Git;

/// <summary>A question git (or ssh) asks for a credential or a confirmation.</summary>
/// <param name="Prompt">The text git gave: "Username for 'https://host': ", "Enter passphrase for key '...': ".</param>
/// <param name="IsSecret">The answer must not be shown as it is typed (a password, a passphrase, a token).</param>
public sealed record AskpassRequest(string Prompt, bool IsSecret)
{
    public static AskpassRequest From(string prompt)
    {
        var lower = prompt.ToLowerInvariant();
        return new AskpassRequest(prompt, lower.Contains("password", StringComparison.Ordinal) || lower.Contains("passphrase", StringComparison.Ordinal)
            || lower.Contains("token", StringComparison.Ordinal) || lower.Contains("pin", StringComparison.Ordinal) && lower.Contains("enter", StringComparison.Ordinal));
    }
}

/// <summary>
/// Lets git ask the app for credentials without a terminal and without Wicked ever storing one. git runs the helper script
/// named in <c>GIT_ASKPASS</c> (ssh the one in <c>SSH_ASKPASS</c>) with the prompt as its argument and reads the answer from
/// its output. The script hands the prompt to this relay through a named pipe in a private folder and waits on a second pipe
/// for the reply; the relay raises <see cref="Handler"/>, which shows the app's dialog, and writes the reply back. The answer
/// lives only in those pipes' buffers for the moment it takes to read. No daemon, socket server or extra executable is needed;
/// the script needs only a POSIX shell and <c>mkfifo</c>, which every Linux and macOS has.
/// </summary>
public sealed class AskpassRelay : IDisposable
{
    private const string Script = """
        #!/bin/sh
        # Relays git's credential prompt to the Wicked window. See AskpassRelay.
        dir="$WICKED_ASKPASS_DIR"
        [ -p "$dir/req" ] || exit 1
        resp="$dir/r.$$"
        mkfifo -m 600 "$resp" || exit 1
        prompt=$(printf '%s' "$1" | tr '\n\t' '  ' | cut -c1-1500)
        printf '%s\t%s\n' "$resp" "$prompt" > "$dir/req" || { rm -f "$resp"; exit 1; }
        { IFS= read -r status; IFS= read -r value; } < "$resp"
        rm -f "$resp"
        [ "$status" = "OK" ] || exit 1
        printf '%s\n' "$value"
        """;

    private readonly string _folder;
    private readonly Thread _reader;
    private readonly FileStream _requests;
    private bool _disposed;

    private AskpassRelay(string folder, FileStream requests)
    {
        _folder = folder;
        _requests = requests;
        ScriptPath = Path.Combine(folder, "askpass.sh");
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "Wicked askpass" };
    }

    /// <summary>Whether this platform can relay (a Unix with named pipes); on Windows git uses the credential manager it ships with.</summary>
    public static bool IsSupported => !OperatingSystem.IsWindows();

    /// <summary>Answers a question: the reply, or null when the user cancelled. Called on a pool thread.</summary>
    public Func<AskpassRequest, Task<string?>>? Handler { get; set; }

    public string ScriptPath { get; }

    /// <summary>The variables that point git and ssh at the relay.</summary>
    public IReadOnlyDictionary<string, string?> Variables => new Dictionary<string, string?>
    {
        ["GIT_ASKPASS"] = ScriptPath,
        ["SSH_ASKPASS"] = ScriptPath,
        ["SSH_ASKPASS_REQUIRE"] = "force",
        ["WICKED_ASKPASS_DIR"] = _folder,
        ["GIT_TERMINAL_PROMPT"] = "0",
    };

    /// <summary>Creates the relay's private folder under <paramref name="parent"/>, or returns null where it cannot work.</summary>
    public static AskpassRelay? Create(string parent)
    {
        if (OperatingSystem.IsWindows()) return null;
        try
        {
            var folder = Path.Combine(parent, "askpass-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var requests = Path.Combine(folder, "req");
            if (MakeFifo(requests, Convert.ToUInt32("600", 8)) != 0) return null;
            File.WriteAllText(Path.Combine(folder, "askpass.sh"), Script + "\n");
            File.SetUnixFileMode(Path.Combine(folder, "askpass.sh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            // Opened for reading and writing, so the open never waits for a writer and the pipe never reports end of file.
            var stream = new FileStream(requests, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1, FileOptions.None);
            var relay = new AskpassRelay(folder, stream);
            relay._reader.Start();
            return relay;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Warn($"The credential relay could not start: {ex.Message}");
            return null;
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private void ReadLoop()
    {
        try
        {
            using var reader = new StreamReader(_requests, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            while (!_disposed)
            {
                var line = reader.ReadLine();
                if (line is null) break;
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue; // the shutdown nudge
                _ = Task.Run(() => AnswerAsync(line[..tab], line[(tab + 1)..]));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task AnswerAsync(string responsePipe, string prompt)
    {
        string? answer = null;
        try
        {
            // Only pipes made inside this relay's own folder are written to.
            if (!string.Equals(Path.GetDirectoryName(responsePipe), _folder, StringComparison.Ordinal)) return;
            if (Handler is { } handler) answer = await handler(AskpassRequest.From(prompt)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"The credential prompt failed: {ex.Message}");
        }

        await Task.Run(() => Reply(responsePipe, answer)).ConfigureAwait(false);
    }

    private static void Reply(string responsePipe, string? answer)
    {
        if (!File.Exists(responsePipe)) return; // git gave up while the user was thinking
        // The write end opens only once the script has the read end open. If the script died, a read-write open of our own lets this one finish.
        using var release = new Timer(_ =>
        {
            try
            {
                using var unblock = new FileStream(responsePipe, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite, 1);
            }
            catch (IOException)
            {
            }
        }, null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
        try
        {
            using var stream = new FileStream(responsePipe, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1);
            var body = answer is null ? "CANCEL\n\n" : "OK\n" + answer.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal) + "\n";
            stream.Write(new UTF8Encoding(false).GetBytes(body));
            stream.Flush();
        }
        catch (IOException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _requests.Write("\n"u8);
            _requests.Flush();
        }
        catch (IOException)
        {
        }

        _requests.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
