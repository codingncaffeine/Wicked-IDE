namespace Wicked.Core.Server;

/// <summary>Finds the <c>wickedd</c> program that goes with this build, so the window and the command line can start it.</summary>
public static class DaemonLocator
{
    public const string PathVariable = "WICKED_DAEMON";

    /// <summary>The daemon beside the running program (the packages put the three programs together), else <c>WICKED_DAEMON</c>, else the PATH; null when none is found.</summary>
    public static string? Find(Func<string, string?>? env = null, string? baseDirectory = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var name = OperatingSystem.IsWindows() ? "wickedd.exe" : "wickedd";
        if (env(PathVariable) is { Length: > 0 } chosen && File.Exists(chosen)) return Path.GetFullPath(chosen);
        var beside = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, name);
        if (File.Exists(beside)) return beside;
        foreach (var folder in (env("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A PATH entry that is not a path.
            }
        }

        return null;
    }
}
