namespace Wicked.Core.Terminal;

public enum ShellKind
{
    Other,
    Bash,
    Zsh,
    Fish,
    PowerShell,
    Cmd,
}

/// <summary>A shell the terminal can start.</summary>
/// <param name="Name">What the user calls it: <c>bash</c>, <c>zsh</c>, <c>PowerShell</c>.</param>
/// <param name="Path">The program.</param>
/// <param name="Arguments">Arguments it is always started with.</param>
/// <param name="Kind">Which shell it is, for the integration scripts.</param>
/// <param name="Login">Start it the way a login shell starts (macOS convention).</param>
public sealed record ShellProfile(string Name, string Path, IReadOnlyList<string> Arguments, ShellKind Kind, bool Login = false);

/// <summary>Finds the shells on this machine and picks the one to start.</summary>
public static class ShellProfiles
{
    /// <summary>The shells found: those <c>/etc/shells</c> lists, the user's own, and the ones a Windows machine has.</summary>
    public static List<ShellProfile> Detect(Func<string, string?>? env = null, Func<string, bool>? exists = null, Func<string, string[]>? readLines = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        exists ??= File.Exists;
        readLines ??= static path => File.Exists(path) ? File.ReadAllLines(path) : [];
        var profiles = new List<ShellProfile>();
        if (OperatingSystem.IsWindows())
        {
            DetectWindows(profiles, env, exists);
            return profiles;
        }

        var paths = new List<string>();
        if (env("SHELL") is { Length: > 0 } own) paths.Add(own);
        foreach (var line in readLines("/etc/shells"))
        {
            var text = line.Trim();
            if (text.StartsWith('/')) paths.Add(text); // not a path
        }

        foreach (var path in paths)
        {
            var name = System.IO.Path.GetFileName(path);
            if (name is "nologin" or "false" or "true" or "git-shell" or "") continue;
            if (!exists(path) || profiles.Any(p => p.Path == path)) continue;
            // /usr/bin/bash and /bin/bash are one shell on most systems: list it once.
            if (profiles.Any(p => p.Name == name)) continue;
            profiles.Add(new ShellProfile(name, path, [], KindOf(name), Login: OperatingSystem.IsMacOS()));
        }

        if (profiles.Count == 0 && exists("/bin/sh")) profiles.Add(new ShellProfile("sh", "/bin/sh", [], ShellKind.Other));
        return profiles;
    }

    private static void DetectWindows(List<ShellProfile> profiles, Func<string, string?> env, Func<string, bool> exists)
    {
        var path = env("PATH") ?? "";
        string? Find(string file) => path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => System.IO.Path.Combine(d, file)).FirstOrDefault(exists);

        if (Find("pwsh.exe") is { } pwsh) profiles.Add(new ShellProfile("PowerShell", pwsh, ["-NoLogo"], ShellKind.PowerShell));
        var windowsPowerShell = System.IO.Path.Combine(env("SystemRoot") ?? @"C:\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        if (exists(windowsPowerShell)) profiles.Add(new ShellProfile("Windows PowerShell", windowsPowerShell, ["-NoLogo"], ShellKind.PowerShell));
        var cmd = env("ComSpec") ?? System.IO.Path.Combine(env("SystemRoot") ?? @"C:\Windows", "System32", "cmd.exe");
        if (exists(cmd)) profiles.Add(new ShellProfile("Command Prompt", cmd, [], ShellKind.Cmd));
        var gitBash = System.IO.Path.Combine(env("ProgramFiles") ?? @"C:\Program Files", "Git", "bin", "bash.exe");
        if (exists(gitBash)) profiles.Add(new ShellProfile("Git Bash", gitBash, ["--login", "-i"], ShellKind.Bash));
        if (Find("wsl.exe") is { } wsl) profiles.Add(new ShellProfile("WSL", wsl, [], ShellKind.Other));
    }

    public static ShellKind KindOf(string name)
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        return n switch
        {
            "bash" => ShellKind.Bash,
            "zsh" => ShellKind.Zsh,
            "fish" => ShellKind.Fish,
            "pwsh" or "powershell" => ShellKind.PowerShell,
            "cmd" => ShellKind.Cmd,
            _ => ShellKind.Other,
        };
    }

    /// <summary>
    /// The profile to start: the one the setting names (by name or by path), else the user's own shell, else the first found.
    /// </summary>
    public static ShellProfile? Choose(IReadOnlyList<ShellProfile> profiles, string? setting, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (profiles.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(setting))
        {
            var wanted = setting.Trim();
            var named = profiles.FirstOrDefault(p => string.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase) || p.Path == wanted);
            if (named is not null) return named;
        }

        if (env("SHELL") is { Length: > 0 } own)
        {
            var mine = profiles.FirstOrDefault(p => p.Path == own || p.Name == System.IO.Path.GetFileName(own));
            if (mine is not null) return mine;
        }

        return profiles[0];
    }
}
