using System.Diagnostics;

namespace Wicked.Core.Server;

/// <summary>
/// "Start at login" for the daemon: a <c>systemd --user</c> unit named <c>wickedd.service</c>, written to the user's systemd folder
/// only when asked and removed when asked. The folder is a parameter so tests write into a profile of their own, and the commands
/// that tell systemd about it go through a runner a test replaces.
/// </summary>
public sealed class SystemdUnit(string configHome, Func<IReadOnlyList<string>, CancellationToken, Task<int>>? runner = null)
{
    public const string UnitName = "wickedd.service";

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<int>> _run = runner ?? RunSystemctlAsync;

    /// <summary>The user's systemd folder for a given XDG config home: <c>$XDG_CONFIG_HOME/systemd/user</c>.</summary>
    public string UnitFolder => Path.Combine(configHome, "systemd", "user");

    public string UnitPath => Path.Combine(UnitFolder, UnitName);

    /// <summary>The user's real config home (<c>XDG_CONFIG_HOME</c>, else <c>~/.config</c>), for the Settings button.</summary>
    public static string DefaultConfigHome(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (env("XDG_CONFIG_HOME") is { Length: > 0 } x && Path.IsPathRooted(x)) return x;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".config");
    }

    public bool IsInstalled => File.Exists(UnitPath);

    /// <summary>The unit's text for a daemon at <paramref name="executable"/>. A profile chosen with <c>WICKED_HOME</c> is carried into the service.</summary>
    public static string Render(string executable, string? wickedHome = null)
    {
        if (executable.Any(char.IsControl) || executable.Contains('"', StringComparison.Ordinal)) throw new ArgumentException("The daemon's path cannot be written into a unit", nameof(executable));
        var environment = wickedHome is { Length: > 0 } ? $"Environment=\"{WickedPaths.HomeVariable}={wickedHome}\"\n" : "";
        return $"""
            [Unit]
            Description=Wicked IDE model server
            Documentation=https://github.com/codingncaffeine/Wicked-IDE

            [Service]
            Type=simple
            ExecStart="{executable}"
            {environment}Restart=on-failure
            RestartSec=3

            [Install]
            WantedBy=default.target
            """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>Writes the unit and tells systemd about it: reloads and enables it, so the daemon starts at the next login.</summary>
    public async Task InstallAsync(string executable, string? wickedHome, CancellationToken ct = default)
    {
        Directory.CreateDirectory(UnitFolder);
        var temp = UnitPath + ".tmp";
        await File.WriteAllTextAsync(temp, Render(executable, wickedHome), ct).ConfigureAwait(false);
        File.Move(temp, UnitPath, overwrite: true);
        await RunAsync(["daemon-reload"], ct).ConfigureAwait(false);
        await RunAsync(["enable", UnitName], ct).ConfigureAwait(false);
    }

    /// <summary>Disables the unit and removes the file (the daemon running now keeps running).</summary>
    public async Task RemoveAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return;
        await RunAsync(["disable", UnitName], ct).ConfigureAwait(false);
        File.Delete(UnitPath);
        await RunAsync(["daemon-reload"], ct).ConfigureAwait(false);
    }

    private async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var code = await _run(arguments, ct).ConfigureAwait(false);
        if (code != 0) Log.Warn($"systemctl --user {string.Join(' ', arguments)} exited with {code}");
    }

    private static async Task<int> RunSystemctlAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("systemctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add("--user");
        foreach (var a in arguments) info.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(info) ?? throw new IOException("systemctl did not start");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Log.Warn($"systemctl is not available: {ex.Message}");
            return -1;
        }
    }
}
