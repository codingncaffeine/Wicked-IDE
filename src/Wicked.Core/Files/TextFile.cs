using System.Text;

namespace Wicked.Core.Files;

/// <summary>How a file was encoded on disk, so a save writes it back the same way.</summary>
/// <param name="Name">"utf-8", "utf-16le", "utf-16be" or "latin1".</param>
/// <param name="Bom">Whether the file began with a byte-order mark.</param>
public sealed record FileEncoding(string Name, bool Bom)
{
    public static readonly FileEncoding Utf8 = new("utf-8", false);

    public Encoding Encoding => Name switch
    {
        "utf-16le" => new UnicodeEncoding(bigEndian: false, byteOrderMark: Bom),
        "utf-16be" => new UnicodeEncoding(bigEndian: true, byteOrderMark: Bom),
        "latin1" => Encoding.Latin1,
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: Bom),
    };

    /// <summary>The label the status bar shows, as VS Code labels encodings.</summary>
    public string Label => Name switch
    {
        "utf-8" => Bom ? "UTF-8 with BOM" : "UTF-8",
        "utf-16le" => "UTF-16 LE",
        "utf-16be" => "UTF-16 BE",
        _ => "Latin-1",
    };
}

/// <summary>What reading a file found.</summary>
public enum FileKind
{
    Text,

    /// <summary>NUL bytes near the start: an image, an archive, a build output. Not opened as text.</summary>
    Binary,

    /// <summary>Larger than the editor opens without asking.</summary>
    TooLarge,
}

/// <summary>A file's text as read, its encoding, and its last write time (to notice changes made elsewhere).</summary>
public sealed record LoadedText(string Text, FileEncoding Encoding, FileKind Kind, DateTime LastWriteUtc, long Length);

/// <summary>
/// Reads and writes text files the way an editor must: the encoding detected (BOM, then strict UTF-8, then Latin-1),
/// binary files recognised and refused, saves written to a temporary file and moved over the original so a crash never
/// leaves half a file. Every method touches the disk: call them off the UI thread.
/// </summary>
public static class TextFile
{
    /// <summary>VS Code asks before opening files over 50 MB; so does Wicked.</summary>
    public const long LargeFile = 50L * 1024 * 1024;

    public static async Task<LoadedText> ReadAsync(string path, bool allowLarge, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!allowLarge && info.Length > LargeFile) return new LoadedText("", FileEncoding.Utf8, FileKind.TooLarge, info.LastWriteTimeUtc, info.Length);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var (text, encoding, kind) = Decode(bytes);
        return new LoadedText(text, encoding, kind, info.LastWriteTimeUtc, bytes.Length);
    }

    public static (string Text, FileEncoding Encoding, FileKind Kind) Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) return (Encoding.UTF8.GetString(bytes[3..]), new FileEncoding("utf-8", true), FileKind.Text);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) return (Encoding.Unicode.GetString(bytes[2..]), new FileEncoding("utf-16le", true), FileKind.Text);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) return (Encoding.BigEndianUnicode.GetString(bytes[2..]), new FileEncoding("utf-16be", true), FileKind.Text);

        // A NUL byte in the first 8 KB marks a binary file, as git and VS Code decide.
        if (bytes[..Math.Min(bytes.Length, 8192)].Contains((byte)0)) return ("", FileEncoding.Utf8, FileKind.Binary);
        try
        {
            return (new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes), FileEncoding.Utf8, FileKind.Text);
        }
        catch (DecoderFallbackException)
        {
            return (Encoding.Latin1.GetString(bytes), new FileEncoding("latin1", false), FileKind.Text);
        }
    }

    /// <summary>Writes <paramref name="text"/> atomically; returns the new last write time.</summary>
    public static async Task<DateTime> WriteAsync(string path, string text, FileEncoding encoding, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.wicked-{Environment.ProcessId}.tmp");
        try
        {
            var bytes = encoding.Encoding.GetPreamble().Concat(encoding.Encoding.GetBytes(text)).ToArray();
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            // Keep the original's permissions (an executable script stays executable).
            if (!OperatingSystem.IsWindows() && File.Exists(path)) File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }

        return File.GetLastWriteTimeUtc(path);
    }
}
