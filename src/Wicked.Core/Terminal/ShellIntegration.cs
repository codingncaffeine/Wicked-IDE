
namespace Wicked.Core.Terminal;

/// <summary>
/// Makes a shell tell the terminal where each prompt, command and result is (OSC 133 and 633) and where it is working
/// (OSC 7), the way VS Code does: bash starts from an rc file of ours that runs the user's own first, zsh through a
/// ZDOTDIR of ours that does the same, fish and PowerShell through a startup command. Programs that are not one of
/// these start unchanged.
/// </summary>
public static class ShellIntegration
{
    private static readonly string[] Files =
    [
        "bash/wicked.bash",
        "zsh/.zshenv",
        "zsh/.zprofile",
        "zsh/.zshrc",
        "zsh/.zlogin",
        "fish/wicked.fish",
        "pwsh/wicked.ps1",
    ];

    /// <summary>Writes the scripts under <paramref name="directory"/> (replacing any that differ). Disk work: call it off the UI thread.</summary>
    public static void Install(string directory)
    {
        var assembly = typeof(ShellIntegration).Assembly;
        foreach (var file in Files)
        {
            // The resource name carries MSBuild's RecursiveDir, which a Windows build writes with a backslash.
            using var stream = assembly.GetManifestResourceStream("shell-integration/" + file)
                ?? assembly.GetManifestResourceStream("shell-integration/" + file.Replace('/', '\\'))
                ?? throw new InvalidOperationException($"the shell integration script {file} is missing from the build");
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            var path = Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && File.ReadAllText(path) == text) continue;
            File.WriteAllText(path, text);
        }
    }

    /// <summary>
    /// The program, arguments and extra environment that start <paramref name="profile"/> with its integration, scripts
    /// being in <paramref name="directory"/> (after <see cref="Install"/>). Without integration, the shell as it is.
    /// </summary>
    public static (string File, List<string> Arguments, Dictionary<string, string?> Environment) Prepare(ShellProfile profile, string directory, bool enabled, Func<string, string?>? env = null)
    {
        env ??= System.Environment.GetEnvironmentVariable;
        var args = profile.Arguments.ToList();
        var extra = new Dictionary<string, string?>();
        if (profile.Login && profile.Kind != ShellKind.Bash) args.Add("-l");
        if (!enabled) return (profile.Path, args, extra);

        switch (profile.Kind)
        {
            case ShellKind.Bash:
                args.Insert(0, Path.Combine(directory, "bash", "wicked.bash"));
                args.Insert(0, "--rcfile");
                // --rcfile applies to shells that are not login shells; the script reads the login files itself when asked to.
                if (profile.Login || args.Contains("--login") || args.Contains("-l"))
                {
                    args.RemoveAll(a => a is "--login" or "-l");
                    extra["WICKED_SHELL_LOGIN"] = "1";
                }

                break;
            case ShellKind.Zsh:
                extra["WICKED_USER_ZDOTDIR"] = env("ZDOTDIR") is { Length: > 0 } z ? z : env("HOME") ?? "";
                extra["ZDOTDIR"] = Path.Combine(directory, "zsh");
                break;
            case ShellKind.Fish:
                args.Add("--init-command");
                args.Add($"source '{Path.Combine(directory, "fish", "wicked.fish").Replace("'", "\\'", StringComparison.Ordinal)}'");
                break;
            case ShellKind.PowerShell:
                if (!args.Contains("-NoExit")) args.Add("-NoExit");
                args.Add("-Command");
                args.Add($". '{Path.Combine(directory, "pwsh", "wicked.ps1").Replace("'", "''", StringComparison.Ordinal)}'");
                break;
            default:
                return (profile.Path, args, extra);
        }

        extra["WICKED_SHELL_INTEGRATION"] = "1";
        return (profile.Path, args, extra);
    }

    /// <summary>The pty options to start a shell: its integration, the working directory, the size and the terminal's environment.</summary>
    public static PtyOptions Launch(ShellProfile profile, string integrationDirectory, bool integration, string? workingDirectory, int columns, int rows, IEnumerable<KeyValuePair<string, string?>>? extraEnvironment = null)
    {
        var (file, args, extra) = Prepare(profile, integrationDirectory, integration);
        var merged = extra.Concat(extraEnvironment ?? []);
        var directory = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory)
            ? workingDirectory
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return new PtyOptions(file, args, directory, Pty.BuildEnvironment(merged), columns, rows);
    }

}
