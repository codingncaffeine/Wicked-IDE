using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace Wicked.Core.Files;

/// <summary>
/// Deleting the way a desktop does: into the Trash, so it can be restored. On Linux this follows the freedesktop.org
/// Trash specification: files on the home drive go to <c>$XDG_DATA_HOME/Trash</c>; files on another drive go to that
/// drive's own <c>.Trash-$UID</c>, as file managers do (never a slow copy across drives). On Windows the Recycle Bin.
/// Disk work: call off the UI thread.
/// </summary>
public static class Trash
{
    public static async Task MoveToTrashAsync(string path, Func<string, string?>? env = null, CancellationToken ct = default)
    {
        env ??= Environment.GetEnvironmentVariable;
        if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException("Nothing to delete", path);
        if (OperatingSystem.IsWindows())
        {
            RecycleOnWindows(path);
            return;
        }

        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        var homeTrash = Path.Combine(env("XDG_DATA_HOME") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".local", "share"), "Trash");
        var trash = SameDrive(full, homeTrash) ? homeTrash : DriveTrash(full) ?? homeTrash;
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        // A free name in the trash: "a.txt", then "a.2.txt", "a.3.txt" ...
        var name = Path.GetFileName(full);
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = name;
        for (var n = 2; File.Exists(Path.Combine(files, candidate)) || Directory.Exists(Path.Combine(files, candidate)) || File.Exists(Path.Combine(info, candidate + ".trashinfo")); n++)
        {
            candidate = string.Create(CultureInfo.InvariantCulture, $"{stem}.{n}{extension}");
        }

        // The info file first, as the specification asks, so a crash never leaves a file in the trash with no way back.
        var original = trash == homeTrash ? full : Path.GetRelativePath(Path.GetDirectoryName(trash)!, full);
        var record = new StringBuilder("[Trash Info]\n")
            .Append("Path=").Append(Escape(original)).Append('\n')
            .Append("DeletionDate=").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)).Append('\n');
        await File.WriteAllTextAsync(Path.Combine(info, candidate + ".trashinfo"), record.ToString(), ct).ConfigureAwait(false);
        var target = Path.Combine(files, candidate);
        if (Directory.Exists(full)) Directory.Move(full, target);
        else File.Move(full, target);
    }

    private static bool SameDrive(string a, string b)
    {
        var ma = MountOf(a);
        var mb = MountOf(Directory.Exists(b) ? b : Path.GetDirectoryName(b)!);
        return ma == mb;
    }

    /// <summary>The drive's own trash, <c>$topdir/.Trash-$uid</c>, or null when it cannot be made.</summary>
    private static string? DriveTrash(string path)
    {
        var top = MountOf(path);
        if (top is null) return null;
        var uid = UserId();
        var trash = Path.Combine(top, $".Trash-{uid}");
        try
        {
            Directory.CreateDirectory(trash);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(trash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return trash;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The mount point that holds <paramref name="path"/>: the longest mount point that is a prefix of it.</summary>
    internal static string? MountOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            string? best = null;
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var fields = line.Split(' ');
                if (fields.Length < 5) continue;
                var mount = fields[4].Replace("\\040", " ", StringComparison.Ordinal);
                if ((full == mount || full.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal)) && (best is null || mount.Length > best.Length)) best = mount; // not a path: mountinfo's own '/'
            }

            return best;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string UserId()
    {
        try
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal)) return line.Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries)[1];
            }
        }
        catch (IOException)
        {
        }

        return "1000";
    }

    /// <summary>The specification's percent-encoding of a path (RFC 2396 escaping, '/' kept).</summary>
    private static string Escape(string path)
    {
        var builder = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~') builder.Append(c);
            else builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static void RecycleOnWindows(string path)
    {
        if (Directory.Exists(path))
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
        else
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }
    }
}
