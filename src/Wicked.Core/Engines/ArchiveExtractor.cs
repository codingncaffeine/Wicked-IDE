using System.Formats.Tar;
using System.IO.Compression;

namespace Wicked.Core.Engines;

/// <summary>
/// Unpacks engine archives (<c>.tar.gz</c> on Linux and macOS, <c>.zip</c> on Windows) the way the files were packed:
/// symbolic links stay links and executable bits stay set (the shared libraries are a chain of symlinks). Archives come
/// from the network, so every entry is checked: no path may leave the destination, and no link may point outside it.
/// </summary>
internal static class ArchiveExtractor
{
    public static Task ExtractAsync(string archive, string destination, CancellationToken ct) =>
        archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? Task.Run(() => ExtractZip(archive, destination, ct), ct)
            : ExtractTarGzAsync(archive, destination, ct);

    /// <summary>
    /// Archives wrap their files in one folder (<c>llama-b11759/</c>); returns that folder when the destination holds
    /// nothing else, so the caller can use it as the engine root.
    /// </summary>
    public static string FlattenRoot(string destination)
    {
        var entries = Directory.GetFileSystemEntries(destination);
        return entries.Length == 1 && Directory.Exists(entries[0]) && new FileInfo(entries[0]).LinkTarget is null ? entries[0] : destination;
    }

    /// <summary>Moves everything in <paramref name="source"/> into <paramref name="target"/>, merging folders (used to lay the CUDA runtime over the engine).</summary>
    public static void MergeInto(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            var to = Path.Combine(target, Path.GetFileName(entry));
            var isRealDirectory = Directory.Exists(entry) && new FileInfo(entry).LinkTarget is null;
            if (isRealDirectory && Directory.Exists(to))
            {
                MergeInto(entry, to);
            }
            else if (isRealDirectory)
            {
                Directory.Move(entry, to);
            }
            else
            {
                File.Move(entry, to, overwrite: true);
            }
        }
    }

    private static async Task ExtractTarGzAsync(string archive, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var links = new List<(string Path, string Target)>();
        var hardLinks = new List<(string Path, string Source)>();

        await using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await using var tar = new TarReader(gzip);
        while (await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false) is { } entry)
        {
            var path = Resolve(root, entry.Name);
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    if (entry.DataStream is { } data)
                    {
                        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                        await data.CopyToAsync(output, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await File.WriteAllBytesAsync(path, [], ct).ConfigureAwait(false);
                    }

                    SetMode(path, entry.Mode);
                    break;
                case TarEntryType.SymbolicLink:
                    CheckLink(root, path, entry.LinkName);
                    links.Add((path, entry.LinkName));
                    break;
                case TarEntryType.HardLink:
                    hardLinks.Add((path, Resolve(root, entry.LinkName)));
                    break;
            }
        }

        // Links last: a link may come before the file it names, and a hard link needs the file to exist.
        foreach (var (path, source) in hardLinks)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.Copy(source, path, overwrite: true);
        }

        foreach (var (path, target) in links)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) || Directory.Exists(path)) File.Delete(path);
            File.CreateSymbolicLink(path, target);
        }
    }

    internal static void ExtractZip(string archive, string destination, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var path = Resolve(root, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.Name.Length == 0) // archive path
            {
                Directory.CreateDirectory(path);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static string Resolve(string root, string entryName)
    {
        var full = Path.GetFullPath(Path.Combine(root, entryName));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry escapes the destination: {entryName}");
        }

        return full;
    }

    private static void CheckLink(string root, string linkPath, string target)
    {
        if (Path.IsPathRooted(target)) throw new InvalidDataException($"Archive link points outside the destination: {target}");
        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath)!, target));
        if (resolved != root && !resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive link points outside the destination: {target}");
        }
    }

    private static void SetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        // Keep the permission bits only (not setuid/sticky), and always leave the owner able to read and write.
        File.SetUnixFileMode(path, (mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute))
                                   | UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
