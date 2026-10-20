using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Wicked.Core.Engines;

/// <summary>What <c>llama-server --version</c> prints; <c>Build</c> is the llama.cpp build number (11759 for tag b11759).</summary>
public sealed record EngineVersion(string Text, int? Build, string? Commit);

/// <summary>A compute device as the engine itself sees it (<c>--list-devices</c>); these numbers are the truth for what fits (D9).</summary>
public sealed record EngineDevice(string Id, string Name, string Backend, long? TotalBytes, long? FreeBytes);

public sealed record CliResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public string Combined => StdOut + StdErr;
}

/// <summary>Runs engine executables for a quick answer (<c>--version</c>, <c>--list-devices</c>) with a hard timeout, and parses what they print.</summary>
public static partial class EngineCli
{
    [GeneratedRegex(@"version:\s*(?<text>\S+)(?:\s*\(build\s+(?<build>\d+)(?:,\s*commit\s+(?<commit>\w+))?\))?", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^\s*(?<id>(?<backend>[A-Za-z]+)[A-Za-z0-9_]*):\s*(?<name>.+?)\s*\((?<total>\d+)\s*MiB(?:,\s*(?<free>\d+)\s*MiB free)?\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex DevicePattern();

    public static async Task<CliResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? ".",
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        AddLibraryPath(info, Path.GetDirectoryName(executable));

        using var process = Process.Start(info) ?? throw new IOException($"Could not start {executable}");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            return new CliResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            return new CliResult(-1, "", "", true);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
    }

    /// <summary>Puts the engine's own folder first on the shared-library search path, so its bundled libraries win over any system copies.</summary>
    internal static void AddLibraryPath(ProcessStartInfo info, string? folder)
    {
        if (folder is null) return;
        var variable = OperatingSystem.IsWindows() ? "PATH" : OperatingSystem.IsMacOS() ? "DYLD_LIBRARY_PATH" : "LD_LIBRARY_PATH";
        var existing = info.Environment.TryGetValue(variable, out var v) ? v : Environment.GetEnvironmentVariable(variable);
        info.Environment[variable] = string.IsNullOrEmpty(existing) ? folder : folder + Path.PathSeparator + existing;
    }

    public static EngineVersion? ParseVersion(string text)
    {
        var m = VersionPattern().Match(text);
        if (!m.Success) return null;
        return new EngineVersion(
            m.Groups["text"].Value,
            m.Groups["build"].Success ? int.Parse(m.Groups["build"].Value, CultureInfo.InvariantCulture) : null,
            m.Groups["commit"].Success ? m.Groups["commit"].Value : null);
    }

    public static IReadOnlyList<EngineDevice> ParseDevices(string text)
    {
        var start = text.IndexOf("Available devices:", StringComparison.Ordinal);
        if (start < 0) return [];
        var devices = new List<EngineDevice>();
        foreach (Match m in DevicePattern().Matches(text[start..]))
        {
            long? Mib(string group) => m.Groups[group].Success ? long.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture) * 1024 * 1024 : null;
            devices.Add(new EngineDevice(m.Groups["id"].Value, m.Groups["name"].Value, m.Groups["backend"].Value, Mib("total"), Mib("free")));
        }

        return devices;
    }
}
