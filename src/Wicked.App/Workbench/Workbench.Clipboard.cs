using Wicked.Core;

namespace Wicked.App.Workbench;

/// <summary>
/// The Explorer's Cut, Copy and Paste of files and folders, as VS Code's: what was cut or copied is kept here (not on the
/// desktop clipboard), a paste makes "name copy.ext" (then "name copy 2.ext") where the name is taken, a cut moves once
/// and open tabs follow the move. The work happens on a worker.
/// </summary>
public sealed partial class Workbench
{
    /// <summary>The files and folders cut or copied in the Explorer, and whether they were cut.</summary>
    public (IReadOnlyList<string> Paths, bool Cut)? FileClipboard { get; private set; }

    public void CopyFiles(IReadOnlyList<string> paths, bool cut) => FileClipboard = paths.Count == 0 ? null : (paths, cut);

    /// <summary>Pastes what was cut or copied into a folder.</summary>
    public async Task PasteFilesAsync(string folder)
    {
        if (FileClipboard is not { } clip) return;
        var moves = new List<(string From, string To)>();
        try
        {
            await Task.Run(() =>
            {
                foreach (var source in clip.Paths)
                {
                    var isFolder = Directory.Exists(source);
                    if (!isFolder && !File.Exists(source)) continue;
                    // A folder into itself or below itself would never end.
                    if (isFolder && (folder + Path.DirectorySeparatorChar).StartsWith(source + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        throw new IOException($"{Path.GetFileName(source)} cannot go inside itself");
                    }

                    if (clip.Cut && Path.GetDirectoryName(source) == folder) continue; // cut and pasted where it is: nothing to do
                    var target = FreeName(folder, Path.GetFileName(source), isFolder);
                    if (clip.Cut)
                    {
                        if (isFolder) Directory.Move(source, target);
                        else File.Move(source, target);
                        moves.Add((source, target));
                    }
                    else if (isFolder)
                    {
                        CopyFolder(source, target);
                    }
                    else
                    {
                        File.Copy(source, target);
                    }
                }
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notices.Post(new Notice("files", ex.Message, NoticeKind.Error));
        }

        // Open tabs of what moved follow it; a cut pastes once.
        foreach (var (from, to) in moves)
        {
            foreach (var tab in AllTabs.Where(t => t.Path is not null).ToList())
            {
                if (tab.Path == from) tab.Renamed(to);
                else if (tab.Path!.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.Ordinal)) tab.Renamed(to + tab.Path[from.Length..]);
            }
        }

        if (clip.Cut) FileClipboard = null;
    }

    /// <summary>"name.ext", or "name copy.ext", "name copy 2.ext"... the first not taken in the folder.</summary>
    internal static string FreeName(string folder, string name, bool isFolder)
    {
        var target = Path.Combine(folder, name);
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        var stem = isFolder ? name : Path.GetFileNameWithoutExtension(name);
        var extension = isFolder ? "" : Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            target = Path.Combine(folder, $"{stem} copy{(n == 1 ? "" : $" {n}")}{extension}");
            if (!File.Exists(target) && !Directory.Exists(target)) return target;
        }
    }

    private static void CopyFolder(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var sub in Directory.EnumerateDirectories(source))
        {
            var to = Path.Combine(target, Path.GetFileName(sub));
            // A linked folder is copied as the link: following it could loop, or copy half the disk.
            if (new DirectoryInfo(sub).LinkTarget is { } link) Directory.CreateSymbolicLink(to, link);
            else CopyFolder(sub, to);
        }
    }
}
