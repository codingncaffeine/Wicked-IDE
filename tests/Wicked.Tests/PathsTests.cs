using Wicked.Core;

namespace Wicked.Tests;

public sealed class PathsTests
{
    [Fact]
    public void WickedHomePutsEverythingUnderOneFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "wicked-home");
        var paths = WickedPaths.Resolve(v => v == WickedPaths.HomeVariable ? root : null);

        Assert.True(paths.Isolated);
        foreach (var dir in new[] { paths.Config, paths.Data, paths.State, paths.Cache, paths.Runtime, paths.Logs, paths.DaemonSocket })
        {
            Assert.StartsWith(root + Path.DirectorySeparatorChar, dir, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LinuxFollowsTheXdgVariables()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "XDG folders are Linux's");
        var env = new Dictionary<string, string>
        {
            ["XDG_CONFIG_HOME"] = "/x/config",
            ["XDG_DATA_HOME"] = "/x/data",
            ["XDG_STATE_HOME"] = "/x/state",
            ["XDG_CACHE_HOME"] = "/x/cache",
            ["XDG_RUNTIME_DIR"] = "/run/user/1000",
        };
        var paths = WickedPaths.Resolve(v => env.GetValueOrDefault(v));

        Assert.False(paths.Isolated);
        Assert.Equal("/x/config/wicked", paths.Config);
        Assert.Equal("/x/data/wicked", paths.Data);
        Assert.Equal("/x/state/wicked", paths.State);
        Assert.Equal("/x/cache/wicked", paths.Cache);
        Assert.Equal("/run/user/1000/wicked/wickedd.sock", paths.DaemonSocket);
    }

    [Fact]
    public void LinuxIgnoresRelativeXdgValuesAndAMissingRuntimeFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "XDG folders are Linux's");
        // The XDG specification says a relative value is invalid and must be ignored.
        var paths = WickedPaths.Resolve(v => v == "XDG_CONFIG_HOME" ? "relative/config" : null);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

        Assert.Equal(Path.Combine(home, ".config", "wicked"), paths.Config);
        Assert.Equal(Path.Combine(home, ".local", "state", "wicked", "run"), paths.Runtime);
    }
}
