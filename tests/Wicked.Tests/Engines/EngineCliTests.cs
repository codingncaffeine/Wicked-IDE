using Wicked.Core.Engines;

namespace Wicked.Tests;

public sealed class EngineCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ParsesTheRealVersionLine()
    {
        var v = EngineCli.ParseVersion("version: 0.7.0-dev (build 11759, commit 522f2b6ee)\nbuilt with GNU 11.4.0 for Linux x86_64\n")!;

        Assert.Equal("0.7.0-dev", v.Text);
        Assert.Equal(11759, v.Build);
        Assert.Equal("522f2b6ee", v.Commit);
        Assert.Null(EngineCli.ParseVersion("error while loading shared libraries: libcuda.so.1"));
    }

    [Fact]
    public void ParsesDevicesFromEveryBackend()
    {
        const string output = """
            ggml_cuda_init: found 1 CUDA devices
            Available devices:
              CUDA0: NVIDIA GeForce RTX 5080 (16303 MiB, 14979 MiB free)
              Vulkan1: AMD Radeon 610M (RADV RAPHAEL_MENDOCINO) (2048 MiB, 2023 MiB free)
              Metal: Apple M3 Max (49152 MiB)
            """;

        var devices = EngineCli.ParseDevices(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(("CUDA0", "NVIDIA GeForce RTX 5080", "CUDA"), (devices[0].Id, devices[0].Name, devices[0].Backend));
        Assert.Equal(16303L * 1048576, devices[0].TotalBytes);
        Assert.Equal(14979L * 1048576, devices[0].FreeBytes);
        Assert.Equal("AMD Radeon 610M (RADV RAPHAEL_MENDOCINO)", devices[1].Name);
        Assert.Equal("Metal", devices[2].Backend);
        Assert.Null(devices[2].FreeBytes);
        Assert.Empty(EngineCli.ParseDevices("Available devices:\n  (none)\n"));
        Assert.Empty(EngineCli.ParseDevices("nothing"));
    }

    [Fact]
    public async Task ATimedOutCommandIsKilled()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        using var dir = new TempProfile();
        var script = Shell.Script(dir.Root, "slow", "sleep 30");

        var result = await EngineCli.RunAsync(script, [], TimeSpan.FromMilliseconds(300), Ct);

        Assert.True(result.TimedOut);
    }

    [Fact]
    public async Task CapturesBothStreamsAndTheExitCode()
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "uses /bin/sh");
        using var dir = new TempProfile();
        var script = Shell.Script(dir.Root, "both", "echo out; echo err >&2; exit 7");

        var result = await EngineCli.RunAsync(script, [], TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("out", result.StdOut.Trim());
        Assert.Equal("err", result.StdErr.Trim());
    }
}

/// <summary>Writes small executable shell scripts that stand in for llama-server.</summary>
internal static class Shell
{
    public static string Script(string directory, string name, string body)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
