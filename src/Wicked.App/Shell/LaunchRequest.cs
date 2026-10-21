using System.Globalization;

namespace Wicked.App.Shell;

/// <summary>A file to open, with the place in it to go to (1-based, as the command line writes it).</summary>
public sealed record OpenedFile(string Path, int? Line = null, int? Column = null);

/// <summary>
/// What a launch asks for, as VS Code's command line has it: folders and files (<c>wicked .</c>, <c>wicked notes.md</c>,
/// <c>wicked -g main.cs:12:5</c>), a window of its own (<c>-n</c>), and waiting until the files are closed (<c>-w</c>, for
/// <c>git commit</c> and other tools that wait for their editor). Paths are made absolute against the launch's folder.
/// </summary>
public sealed record LaunchRequest(IReadOnlyList<string> Folders, IReadOnlyList<OpenedFile> Files, bool NewWindow, bool Wait)
{
    public static readonly LaunchRequest Empty = new([], [], false, false);

    public bool IsEmpty => Folders.Count == 0 && Files.Count == 0;

    /// <summary>
    /// Reads the arguments. Unknown options are an error (null and a message); a path that does not exist is still taken as
    /// a file to create when it is saved, as VS Code does.
    /// </summary>
    public static LaunchRequest? Parse(IReadOnlyList<string> args, string workingDirectory, Func<string, bool> isFolder, out string? error)
    {
        error = null;
        var folders = new List<string>();
        var files = new List<OpenedFile>();
        bool newWindow = false, wait = false, gotoNext = false;
        foreach (var arg in args)
        {
            switch (arg)
            {
                case "-n" or "--new-window":
                    newWindow = true;
                    continue;
                case "-w" or "--wait":
                    wait = true;
                    continue;
                case "-g" or "--goto":
                    gotoNext = true;
                    continue;
                case "-r" or "--reuse-window":
                    continue; // the default here
            }

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                error = $"Unknown option {arg} (see wicked --help)";
                return null;
            }

            var (path, line, column) = gotoNext ? Location(arg) : (arg, (int?)null, (int?)null);
            gotoNext = false;
            var full = Path.GetFullPath(path, workingDirectory);
            if (isFolder(full)) folders.Add(full);
            else files.Add(new OpenedFile(full, line, column));
        }

        if (wait && files.Count == 0)
        {
            error = "--wait needs a file to wait for";
            return null;
        }

        return new LaunchRequest(folders, files, newWindow, wait);
    }

    /// <summary>"file:12:5" → the file, line 12, column 5; a suffix that is not a number stays part of the name.</summary>
    public static (string Path, int? Line, int? Column) Location(string arg)
    {
        var parts = arg.Split(':');
        int? Number(string s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
        if (parts.Length >= 3 && Number(parts[^1]) is { } c && Number(parts[^2]) is { } l) return (string.Join(':', parts[..^2]), l, c);
        if (parts.Length >= 2 && Number(parts[^1]) is { } only) return (string.Join(':', parts[..^1]), only, null);
        return (arg, null, null);
    }
}
