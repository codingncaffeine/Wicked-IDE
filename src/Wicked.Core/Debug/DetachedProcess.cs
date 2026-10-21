using System.Diagnostics;
using Wicked.Core.Lsp;

namespace Wicked.Core.Debug;

/// <summary>
/// Answers an adapter's <c>runInTerminal</c> without a terminal: the program is started by the client in a process group of its
/// own (<c>setsid</c> where the system has it, so a Ctrl+C aimed at the IDE does not reach it), its output is handed to the
/// Debug Console, and it is ended when the session ends. The terminal lane replaces this with a real terminal later.
/// </summary>
public static class DetachedProcess
{
    public static Task<RunInTerminalResult> RunAsync(RunInTerminalArguments request, Action<string, string>? output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Args.Count == 0) throw new DapException("There is no program to run", "runInTerminal");
        var program = request.Args[0];
        var arguments = request.Args.Skip(1).ToList();
        var setsid = OperatingSystem.IsWindows() ? null : new ServerLocator("", null).Find(new LanguageServerSpec("setsid", "setsid", [], ["setsid"], [], InstallKind.System))?.Executable;
        var info = new ProcessStartInfo(setsid ?? program)
        {
            WorkingDirectory = Directory.Exists(request.Cwd) ? request.Cwd : Environment.CurrentDirectory,
            RedirectStandardOutput = output is not null,
            RedirectStandardError = output is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (setsid is not null)
        {
            info.ArgumentList.Add("--");
            info.ArgumentList.Add(program);
        }

        foreach (var a in arguments) info.ArgumentList.Add(a);
        if (request.Env is not null)
        {
            foreach (var (key, value) in request.Env)
            {
                if (value is null) info.Environment.Remove(key);
                else info.Environment[key] = value;
            }
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new DapException($"{program} did not start", "runInTerminal");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DapException($"{program} could not be started: {ex.Message}", "runInTerminal");
        }

        if (output is not null)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null) output("stdout", e.Data + "\n");
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null) output("stderr", e.Data + "\n");
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return Task.FromResult(new RunInTerminalResult(process.Id, new Ender(process)));
    }

    private sealed class Ender(Process process) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already gone.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}
