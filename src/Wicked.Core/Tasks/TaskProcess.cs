using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Wicked.Core.Tasks;

/// <summary>A program to start: what to run, its arguments, where, and the variables added to its environment.</summary>
public sealed record ProcessSpec(string FileName, ImmutableArray<string> Args, string WorkingDirectory, IReadOnlyDictionary<string, string> Environment);

/// <summary>Colour and cursor codes in a line of output, which the Output panel does not show.</summary>
public static partial class AnsiText
{
    [GeneratedRegex(@"\u001b\[[0-9;?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)|\u001b[@-Z\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex Codes();

    public static string Strip(string text) => text.Contains('\u001b', StringComparison.Ordinal) ? Codes().Replace(text, "") : text;
}

/// <summary>How a command line is put together for a shell, argument by argument.</summary>
public static partial class TaskCommandLine
{
    [GeneratedRegex(@"[\s""'`\\&|;<>(){}!#]", RegexOptions.CultureInvariant)]
    private static partial Regex NeedsQuoting();

    public static string Join(string command, IEnumerable<TaskArgument> args, bool windows)
    {
        var line = new StringBuilder(command);
        foreach (var arg in args) line.Append(' ').Append(Quote(arg, windows));
        return line.ToString();
    }

    public static string Quote(TaskArgument arg, bool windows)
    {
        var v = arg.Value;
        var already = v.Length >= 2 && (v[0] == '"' && v[^1] == '"' || v[0] == '\'' && v[^1] == '\'');
        switch (arg.Quoting)
        {
            case QuoteMode.Strong:
                return windows ? "\"" + v.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : "'" + v.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            case QuoteMode.Weak:
                return "\"" + v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            default:
                if (already) return v;
                if (v.Length == 0) return windows ? "\"\"" : "''";
                if (!NeedsQuoting().IsMatch(v)) return v;
                return windows ? "\"" + v.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : "'" + v.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        }
    }

    /// <summary>The program and arguments that run <paramref name="line"/> in a shell.</summary>
    public static (string FileName, ImmutableArray<string> Args) Shell(string line, ShellOptions? custom, Func<string, string?> env, bool windows)
    {
        if (custom is not null) return (custom.Executable, [.. custom.Args.IsDefaultOrEmpty ? [windows ? "/c" : "-c"] : custom.Args, line]);
        if (windows) return ("cmd.exe", ["/d", "/c", line]);
        var user = env("SHELL");
        var known = user is { Length: > 0 } && File.Exists(user) && Path.GetFileName(user) is "bash" or "zsh" or "fish" or "sh" or "dash" or "ksh";
        return (known ? user! : "/bin/sh", ["-c", line]);
    }
}

/// <summary>
/// A child process of a task, started in a process group of its own so that terminating it takes everything it started
/// along (a build tool's compilers, a dev server's workers). Its output arrives line by line, standard output and
/// standard error in the order they were read.
/// </summary>
public sealed class TaskProcess : IDisposable
{
    private const int MaxLine = 64 * 1024;
    private readonly Process _process;
    private readonly bool _ownGroup;
    private readonly Channel<(string Text, bool IsError)> _lines = Channel.CreateUnbounded<(string, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private int _terminated;

    private TaskProcess(Process process, bool ownGroup)
    {
        _process = process;
        _ownGroup = ownGroup;
    }

    public int Id { get; private set; }

    public ChannelReader<(string Text, bool IsError)> Lines => _lines.Reader;

    /// <summary>Whether <see cref="Terminate"/> was called.</summary>
    public bool WasTerminated => Volatile.Read(ref _terminated) == 1;

    /// <summary>The process started, or a <see cref="Win32Exception"/> saying why not.</summary>
    public static TaskProcess Start(ProcessSpec spec)
    {
        var useSetsid = !OperatingSystem.IsWindows() && SetsidPath() is not null;
        var info = new ProcessStartInfo(useSetsid ? SetsidPath()! : spec.FileName)
        {
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (useSetsid) info.ArgumentList.Add(spec.FileName);
        foreach (var a in spec.Args) info.ArgumentList.Add(a);
        foreach (var (k, v) in spec.Environment) info.Environment[k] = v;
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        if (!process.Start()) throw new Win32Exception("The process did not start");
        var task = new TaskProcess(process, useSetsid) { Id = process.Id };
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The program is already gone; its exit is read below.
        }

        _ = task.PumpAsync();
        return task;
    }

    private static string? SetsidPath() => new[] { "/usr/bin/setsid", "/bin/setsid", "/usr/local/bin/setsid" }.FirstOrDefault(File.Exists);

    private async Task PumpAsync()
    {
        var readers = Task.WhenAll(Read(_process.StandardOutput, false), Read(_process.StandardError, true));
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            // A grandchild left running can hold the pipes open for good; the output that is going to come has come.
            await Task.WhenAny(readers, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        }
        finally
        {
            _lines.Writer.TryComplete();
        }
    }

    private async Task Read(StreamReader reader, bool isError)
    {
        var buffer = new char[4096];
        var line = new StringBuilder();
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    var c = buffer[i];
                    if (c == '\n')
                    {
                        Emit(line, isError);
                    }
                    else if (c == '\r' && i + 1 < n && buffer[i + 1] == '\n')
                    {
                        // CRLF: the LF ends the line.
                    }
                    else if (c == '\r')
                    {
                        // A bare CR returns the cursor: what follows overwrites the line (progress bars).
                        line.Clear();
                    }
                    else
                    {
                        line.Append(c);
                        if (line.Length >= MaxLine) Emit(line, isError);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The pipe closed under the reader.
        }

        if (line.Length > 0) Emit(line, isError);
    }

    private void Emit(StringBuilder line, bool isError)
    {
        _lines.Writer.TryWrite((line.ToString(), isError));
        line.Clear();
    }

    /// <summary>Completes with the exit code once the process has ended.</summary>
    public async Task<int> WaitForExitAsync()
    {
        await _process.WaitForExitAsync().ConfigureAwait(false);
        return _process.ExitCode;
    }

    /// <summary>Asks the whole process group to stop; whatever is still running after a few seconds is killed.</summary>
    public void Terminate()
    {
        if (Interlocked.Exchange(ref _terminated, 1) == 1) return;
        Signal(15);
        _ = Task.Run(async () =>
        {
            try
            {
                await _process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(3)).Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            // The leader may be gone while its children are not: the group is signalled either way.
            Signal(9);
        });
    }

    private void Signal(int signal)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (signal == 9 || !_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            else if (_ownGroup)
            {
                _ = kill(-Id, signal);
            }
            else if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
    }

    public void Dispose() => _process.Dispose();

    /// <summary>The path of a program as the shell would find it, or null.</summary>
    public static string? Find(string program, string? path, string workingDirectory)
    {
        if (program.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal) || program.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            var full = Path.GetFullPath(program, workingDirectory);
            return File.Exists(full) ? full : null;
        }

        var extensions = OperatingSystem.IsWindows() ? (System.Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        foreach (var dir in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, program + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);
}
