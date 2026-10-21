using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wicked.Core.Debug;

/// <summary>
/// Starts what launch.json names: the variables filled in, the <c>preLaunchTask</c> run first, the configuration handed to its
/// adapter as a launch or an attach, and the <c>postDebugTask</c> run when the session ends. A compound starts its members one
/// after another, and with <c>stopAll</c> ends the rest when one of them ends.
/// </summary>
public sealed class DebugLauncher(DebugManager manager)
{
    /// <summary>Properties of a configuration that are the client's, not the adapter's.</summary>
    private static readonly string[] ClientOnly = ["preLaunchTask", "postDebugTask", "presentation", "debugServer"];

    /// <summary>
    /// Runs a task from tasks.json by its label and waits for it; null when it succeeded, otherwise why not. The shell sets it from
    /// the tasks lane; without one a configuration that names a task cannot start.
    /// </summary>
    public Func<string, CancellationToken, Task<string?>>? RunTask { get; set; }

    /// <summary>A name from the picker: a configuration or a compound. Returns every session it started.</summary>
    public async Task<IReadOnlyList<DebugSession>> StartAsync(LaunchFile file, string name, LaunchContext context, CancellationToken ct = default)
    {
        context = context with { Inputs = file.Inputs };
        if (file.Configuration(name) is { } configuration) return [await StartConfigurationAsync(configuration, context, ct).ConfigureAwait(false)];
        if (file.Compound(name) is not { } compound) throw new LaunchException($"launch.json has no configuration or compound called \"{name}\"");
        if (compound.PreLaunchTask is { } task) await RunTaskAsync(task, ct).ConfigureAwait(false);
        var members = compound.Configurations.Select(m => file.Configuration(m) ?? throw new LaunchException($"The compound \"{name}\" names \"{m}\", which launch.json does not have")).ToList();
        var started = new List<DebugSession>();
        foreach (var member in members) started.Add(await StartConfigurationAsync(member, context, ct).ConfigureAwait(false));
        if (compound.StopAll)
        {
            foreach (var session in started)
            {
                session.Ended += () =>
                {
                    foreach (var other in started.Where(o => o != session && o.State != DebugSessionState.Ended)) _ = other.StopAsync();
                };
            }
        }

        return started;
    }

    public async Task<DebugSession> StartConfigurationAsync(LaunchConfiguration configuration, LaunchContext context, CancellationToken ct = default)
    {
        var resolved = await LaunchVariables.ResolveAsync(configuration.Properties, context, ct).ConfigureAwait(false);
        var request = BuildRequest(configuration, resolved);
        if (configuration.PreLaunchTask is { } task)
        {
            var preLaunch = await LaunchVariables.ResolveAsync(task, context, ct).ConfigureAwait(false);
            await RunTaskAsync(preLaunch, ct).ConfigureAwait(false);
        }

        var session = await manager.StartAsync(request, null, ct).ConfigureAwait(false);
        if (configuration.PostDebugTask is { } post)
        {
            session.Ended += () => _ = Task.Run(async () =>
            {
                try
                {
                    await RunTaskAsync(await LaunchVariables.ResolveAsync(post, context, CancellationToken.None).ConfigureAwait(false), CancellationToken.None).ConfigureAwait(false);
                }
                catch (LaunchException ex)
                {
                    session.Say("stderr", ex.Message + "\n");
                }
            });
        }

        return session;
    }

    /// <summary>What the adapter is sent: the resolved configuration without the properties that are the client's.</summary>
    public static DebugStartRequest BuildRequest(LaunchConfiguration configuration, JsonObject resolved)
    {
        var arguments = new JsonObject();
        foreach (var (key, value) in resolved)
        {
            if (!ClientOnly.Contains(key)) arguments[key] = value?.DeepClone();
        }

        return new DebugStartRequest(configuration.Name, configuration.Type, configuration.Request, JsonSerializer.SerializeToElement(arguments));
    }

    private async Task RunTaskAsync(string label, CancellationToken ct)
    {
        if (RunTask is not { } run) throw new LaunchException($"The task \"{label}\" cannot run: no task runner is available");
        if (await run(label, ct).ConfigureAwait(false) is { } failure) throw new LaunchException(failure);
    }
}

/// <summary>A running program a debugger could attach to.</summary>
public sealed record ProcessEntry(int Id, string Name, string CommandLine);

/// <summary>The programs running now, for "Attach to Process" and <c>${command:pickProcess}</c>.</summary>
public static class ProcessLister
{
    /// <summary>The user's processes, the ones with a command line first. Reads /proc where there is one; call from a worker.</summary>
    public static IReadOnlyList<ProcessEntry> List()
    {
        var found = new List<ProcessEntry>();
        if (Directory.Exists("/proc/self"))
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(dir), out var id) || id == Environment.ProcessId) continue;
                try
                {
                    var name = File.ReadAllText(Path.Combine(dir, "comm")).Trim();
                    var command = File.ReadAllText(Path.Combine(dir, "cmdline")).Replace('\0', ' ').Trim();
                    if (command.Length == 0) continue; // kernel threads have none
                    found.Add(new ProcessEntry(id, name, command));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The process ended while it was read, or is not ours to look at.
                }
            }
        }
        else
        {
            foreach (var p in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (p.Id != Environment.ProcessId) found.Add(new ProcessEntry(p.Id, p.ProcessName, p.ProcessName));
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }

        return [.. found.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Id)];
    }
}
