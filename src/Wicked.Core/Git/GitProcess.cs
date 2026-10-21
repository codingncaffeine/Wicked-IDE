using System.Diagnostics;
using System.Text;

namespace Wicked.Core.Git;

/// <summary>What a git command printed and how it ended.</summary>
public sealed record GitResult(int ExitCode, string Output, string Error)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>A git command that failed; <see cref="Details"/> carries its output and stderr.</summary>
public sealed class GitException(string message, GitResult result) : Exception(message)
{
    public GitResult Details { get; } = result;
}

/// <summary>One line of the Git output log: the command, how it ended, and what it said on stderr.</summary>
public sealed record GitLogEntry(DateTimeOffset Time, string Repository, string Command, int ExitCode, TimeSpan Elapsed, string Error);

/// <summary>
/// Every git command Wicked ran and what it said, newest last, capped. Any thread may append; <see cref="Appended"/> is
/// raised on the thread that appended, so a view marshals it to its own. The bottom panel's Output tab shows this.
/// </summary>
public sealed class GitCommandLog
{
    private const int Capacity = 2000;
    private readonly Lock _gate = new();
    private readonly List<GitLogEntry> _entries = [];

    public event Action<GitLogEntry>? Appended;

    public IReadOnlyList<GitLogEntry> Snapshot()
    {
        lock (_gate) return [.. _entries];
    }

    public void Append(GitLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Capacity) _entries.RemoveRange(0, _entries.Count - Capacity);
        }

        Appended?.Invoke(entry);
    }
}

/// <summary>
/// How git is run: the program, the variables added to its environment, and the log it reports to. A test sets
/// <c>HOME</c>, <c>GIT_CONFIG_GLOBAL</c> and <c>GIT_CONFIG_NOSYSTEM</c> here so the user's own configuration is never read.
/// </summary>
public sealed class GitEnvironment
{
    public string Executable { get; init; } = "git";

    /// <summary>Variables set for every command; a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Variables { get; init; } = new Dictionary<string, string?>();

    public GitCommandLog Log { get; init; } = new();

    /// <summary>A command that runs longer than this is stopped (a hung credential helper, a dead remote).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// Starts git the way an IDE should: no pager, no optional locks (a status poll must not fight the user's own git),
/// paths unquoted, the C locale so output can be parsed, no terminal prompt (credentials come through the askpass relay),
/// and no editor. Process start, reads and waits all run on the thread pool.
/// </summary>
public static class GitProcess
{
    private static readonly string[] Prefix = ["--no-pager", "--no-optional-locks", "-c", "core.quotepath=off", "-c", "color.ui=false"];

    public static ProcessStartInfo StartInfo(GitEnvironment env, string workingDirectory, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(env.Executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in Prefix) info.ArgumentList.Add(a);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["LC_ALL"] = "C";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GIT_PAGER"] = "cat";
        info.Environment["GIT_EDITOR"] = "true";
        info.Environment["GIT_SEQUENCE_EDITOR"] = "true";
        foreach (var (key, value) in env.Variables)
        {
            if (value is null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }

        return info;
    }

    /// <summary>Runs git and collects its whole output. Never throws for a non-zero exit; throws for a git that cannot start.</summary>
    public static Task<GitResult> RunAsync(GitEnvironment env, string workingDirectory, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default) =>
        RunAsync(env, workingDirectory, args, stdin, onLine: null, ct);

    /// <summary>Like <see cref="RunAsync(GitEnvironment, string, IReadOnlyList{string}, string?, CancellationToken)"/>, handing each stdout line to <paramref name="onLine"/> as it arrives (for incremental parsers).</summary>
    public static async Task<GitResult> RunAsync(GitEnvironment env, string workingDirectory, IReadOnlyList<string> args, string? stdin, Action<string>? onLine, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var command = "git " + string.Join(' ', args.Select(Quote));
        int exit;
        var output = new StringBuilder();
        var error = new StringBuilder();
        try
        {
            exit = await Task.Run(async () =>
            {
                using var process = new Process { StartInfo = StartInfo(env, workingDirectory, args) };
                process.Start();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(env.Timeout);
                using var kill = timeout.Token.Register(() => Kill(process));
                var write = Task.Run(async () =>
                {
                    try
                    {
                        if (stdin is not null) await process.StandardInput.WriteAsync(stdin).ConfigureAwait(false);
                        process.StandardInput.Close();
                    }
                    catch (IOException)
                    {
                        // git stopped reading (it failed early); its exit code says why.
                    }
                });
                var readOut = Task.Run(async () =>
                {
                    if (onLine is null)
                    {
                        output.Append(await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false));
                        return;
                    }

                    string? line;
                    while ((line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false)) is not null)
                    {
                        onLine(line);
                    }
                });
                var readErr = process.StandardError.ReadToEndAsync();
                await Task.WhenAll(write, readOut).ConfigureAwait(false);
                error.Append(await readErr.ConfigureAwait(false));
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                return process.ExitCode;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            env.Log.Append(new GitLogEntry(DateTimeOffset.Now, workingDirectory, command, -1, started.Elapsed, "cancelled"));
            throw;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            env.Log.Append(new GitLogEntry(DateTimeOffset.Now, workingDirectory, command, 127, started.Elapsed, ex.Message));
            throw new GitException($"git could not be started: {ex.Message}", new GitResult(127, "", ex.Message));
        }

        var result = new GitResult(exit, output.ToString(), error.ToString());
        env.Log.Append(new GitLogEntry(DateTimeOffset.Now, workingDirectory, command, exit, started.Elapsed, result.Error.TrimEnd()));
        return result;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string Quote(string arg) => arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./:=@%+,~".Contains(c)) ? arg : "'" + arg.Replace("'", "'\\''") + "'";
}
