using System.Runtime.InteropServices;
using Wicked.Core;

namespace Wicked.App.Shell;

/// <summary>How this copy of Wicked was installed, which decides how it is updated.</summary>
public enum InstallKind
{
    /// <summary>Built from source (a bin/Release or bin/Debug folder).</summary>
    Development,

    /// <summary>The Linux tarball, unpacked anywhere.</summary>
    Tarball,

    /// <summary>The .deb, installed by apt or dpkg.</summary>
    Deb,

    /// <summary>The AUR package (wicked-ide-bin).</summary>
    Aur,

    /// <summary>The Windows zip, unzipped anywhere.</summary>
    WindowsZip,
}

/// <summary>
/// What a bug report and the About box say about this machine and this build. Read once on a worker: the GPU and the engine
/// come from the model state, which the caller passes in.
/// </summary>
public static class SystemInfo
{
    public const string Repository = "https://github.com/codingncaffeine/Wicked-IDE";

    /// <summary>The version without the commit the SDK appends ("0.0.3").</summary>
    public static string ShortVersion => BuildInfo.Version.Split('+')[0];

    /// <summary>The commit this build was made from, shortened, or "" when the build does not say.</summary>
    public static string Commit => BuildInfo.Version.Split('+') is [_, var sha, ..] ? sha[..Math.Min(12, sha.Length)] : "";

    public static InstallKind Install(string? baseDirectory = null, Func<string, bool>? fileExists = null, Func<string, bool>? dirExists = null)
    {
        var dir = (baseDirectory ?? AppContext.BaseDirectory).Replace('\\', '/');
        var exists = fileExists ?? File.Exists;
        var isDir = dirExists ?? Directory.Exists;
        if (dir.Contains("/bin/Release/", StringComparison.Ordinal) || dir.Contains("/bin/Debug/", StringComparison.Ordinal)) return InstallKind.Development;
        if (OperatingSystem.IsWindows()) return InstallKind.WindowsZip;
        if (!dir.StartsWith("/usr/lib/wicked-ide", StringComparison.Ordinal)) return InstallKind.Tarball; // not a path: Linux text, Windows returned above
        if (exists("/var/lib/dpkg/info/wicked-ide.list")) return InstallKind.Deb;
        return isDir("/var/lib/pacman/local") ? InstallKind.Aur : InstallKind.Deb;
    }

    /// <summary>How to get a newer version, for this kind of install.</summary>
    public static string HowToUpdate(InstallKind kind, string version) => kind switch
    {
        InstallKind.Aur => "Update it with your AUR helper, for example: yay -Syu wicked-ide-bin",
        InstallKind.Deb => $"Download wicked-ide_{version}_amd64.deb from the release page and install it with: sudo apt install ./wicked-ide_{version}_amd64.deb",
        InstallKind.WindowsZip => "Download the new zip from the release page and unzip it over this folder (settings and models are kept).",
        InstallKind.Development => "This is a build from source: pull and build again.",
        _ => "Download the new tarball from the release page and unpack it over this folder (settings and models are kept).",
    };

    public static string InstallName(InstallKind kind) => kind switch
    {
        InstallKind.Aur => "AUR package",
        InstallKind.Deb => ".deb package",
        InstallKind.WindowsZip => "Windows zip",
        InstallKind.Development => "built from source",
        _ => "tarball",
    };

    /// <summary>The lines a bug report needs: build, system, display and hardware.</summary>
    public static string Describe(string? gpu, string? engine)
    {
        var lines = new List<string>
        {
            $"Wicked: {ShortVersion}{(Commit.Length > 0 ? $" ({Commit})" : "")}, {InstallName(Install())}",
            $"OS: {RuntimeInformation.OSDescription} ({RuntimeInformation.RuntimeIdentifier})",
        };
        if (OperatingSystem.IsLinux())
        {
            var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
            var session = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            lines.Add($"Desktop: {(string.IsNullOrEmpty(desktop) ? "unknown" : desktop)}{(string.IsNullOrEmpty(session) ? "" : $" ({session})")}");
        }

        lines.Add($"Windowing: {WindowingBackend.Name ?? "not open yet"}");
        lines.Add($".NET: {RuntimeInformation.FrameworkDescription}");
        lines.Add($"GPU: {gpu ?? "none found"}");
        lines.Add($"Engine: {engine ?? "not installed"}");
        return string.Join('\n', lines);
    }
}
