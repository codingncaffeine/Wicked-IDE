using System.Collections.Immutable;
using System.ComponentModel;

namespace Wicked.Core.Testing;

/// <summary>
/// The programs a test framework is run with, found on a PATH that a test can replace, and the variables added to their
/// environment (no colour, no telemetry, no build servers left behind).
/// </summary>
public sealed class ToolEnv
{
    public string? Path { get; init; } = Environment.GetEnvironmentVariable("PATH");

    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    /// <summary>A program as the shell would find it: in the project's own folders first (<paramref name="local"/>), then on the PATH.</summary>
    public string? Find(string program, string workingDirectory, params string[] local)
    {
        foreach (var dir in local)
        {
            var candidate = System.IO.Path.Combine(workingDirectory, dir, program);
            if (File.Exists(candidate)) return candidate;
        }

        return Tasks.TaskProcess.Find(program, Path, workingDirectory);
    }

    public Dictionary<string, string> ChildEnvironment()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NO_COLOR"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
            ["MSBUILDDISABLENODEREUSE"] = "1",
            ["UseSharedCompilation"] = "false",
            ["CI"] = "1",
            ["FORCE_COLOR"] = "0",
        };
        if (Path is not null) env["PATH"] = Path;
        foreach (var (k, v) in Variables) env[k] = v;
        return env;
    }
}

/// <summary>What a tool printed (standard output and error in order) and how it ended.</summary>
public sealed record ToolResult(int ExitCode, ImmutableArray<string> Lines, string? StartError)
{
    public bool Started => StartError is null;
}

/// <summary>Runs a tool to its end in its own process group, line by line, and stops it on cancellation.</summary>
public static class ToolRunner
{
    public static async Task<ToolResult> RunAsync(ToolEnv env, string program, IEnumerable<string> args, string workingDirectory, Action<string>? onLine, CancellationToken ct)
    {
        var path = env.Find(program, workingDirectory);
        if (path is null) return new ToolResult(-1, [], $"{program} was not found");
        var spec = new Tasks.ProcessSpec(path, [.. args], workingDirectory, env.ChildEnvironment());
        Tasks.TaskProcess process;
        try
        {
            process = Tasks.TaskProcess.Start(spec);
        }
        catch (Win32Exception ex)
        {
            return new ToolResult(-1, [], ex.Message);
        }

        using (process)
        using (ct.Register(process.Terminate))
        {
            var lines = ImmutableArray.CreateBuilder<string>();
            await foreach (var (text, _) in process.Lines.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                lines.Add(text);
                onLine?.Invoke(text);
            }

            var exit = await process.WaitForExitAsync().ConfigureAwait(false);
            return new ToolResult(exit, lines.ToImmutable(), null);
        }
    }
}
