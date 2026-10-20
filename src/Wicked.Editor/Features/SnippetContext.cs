using System.Globalization;

namespace Wicked.Editor.Features;

/// <summary>
/// What a snippet's variables resolve against: the file, the selection, the clock. Everything is a plain property so a
/// test can fix it; the clock is a <see cref="TimeProvider"/> and the random numbers come from a seedable generator.
/// </summary>
public sealed class SnippetContext
{
    /// <summary>Every variable name VS Code defines; any other name in a snippet becomes a placeholder showing its own name.</summary>
    public static readonly IReadOnlySet<string> KnownVariables = new HashSet<string>(StringComparer.Ordinal)
    {
        "TM_SELECTED_TEXT", "TM_CURRENT_LINE", "TM_CURRENT_WORD", "TM_LINE_INDEX", "TM_LINE_NUMBER",
        "TM_FILENAME", "TM_FILENAME_BASE", "TM_DIRECTORY", "TM_FILEPATH", "RELATIVE_FILEPATH", "CLIPBOARD",
        "WORKSPACE_NAME", "WORKSPACE_FOLDER", "CURSOR_INDEX", "CURSOR_NUMBER",
        "CURRENT_YEAR", "CURRENT_YEAR_SHORT", "CURRENT_MONTH", "CURRENT_MONTH_NAME", "CURRENT_MONTH_NAME_SHORT",
        "CURRENT_DATE", "CURRENT_DAY_NAME", "CURRENT_DAY_NAME_SHORT", "CURRENT_HOUR", "CURRENT_MINUTE",
        "CURRENT_SECOND", "CURRENT_SECONDS_UNIX", "CURRENT_TIMEZONE_OFFSET", "RANDOM", "RANDOM_HEX", "UUID",
        "BLOCK_COMMENT_START", "BLOCK_COMMENT_END", "LINE_COMMENT",
    };

    private Random? _random;

    /// <summary>The full path of the file being edited, if it has one.</summary>
    public string? FilePath { get; init; }

    public string? WorkspaceFolder { get; init; }

    public string? WorkspaceName { get; init; }

    public string? SelectedText { get; init; }

    public string? CurrentLine { get; init; }

    public string? CurrentWord { get; init; }

    /// <summary>0-based line of the cursor.</summary>
    public int LineIndex { get; init; }

    /// <summary>0-based index of this cursor among the cursors the snippet is inserted at.</summary>
    public int CursorIndex { get; init; }

    public string? Clipboard { get; init; }

    public string? LineComment { get; init; }

    public string? BlockCommentStart { get; init; }

    public string? BlockCommentEnd { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Seed for RANDOM and RANDOM_HEX; null for a fresh seed.</summary>
    public int? RandomSeed { get; init; }

    /// <summary>
    /// The value of a variable, or null when it has none here (the snippet's default text is used then, or nothing).
    /// Only names in <see cref="KnownVariables"/> are meaningful.
    /// </summary>
    public string? Resolve(string name)
    {
        var now = Time.GetLocalNow();
        var inv = CultureInfo.InvariantCulture;
        switch (name)
        {
            case "TM_SELECTED_TEXT": return string.IsNullOrEmpty(SelectedText) ? null : SelectedText;
            case "TM_CURRENT_LINE": return CurrentLine;
            case "TM_CURRENT_WORD": return string.IsNullOrEmpty(CurrentWord) ? null : CurrentWord;
            case "TM_LINE_INDEX": return LineIndex.ToString(inv);
            case "TM_LINE_NUMBER": return (LineIndex + 1).ToString(inv);
            case "TM_FILENAME": return FilePath is null ? null : Path.GetFileName(FilePath);
            case "TM_FILENAME_BASE": return FilePath is null ? null : Path.GetFileNameWithoutExtension(FilePath);
            case "TM_DIRECTORY": return FilePath is null ? null : Path.GetDirectoryName(FilePath) is { Length: > 0 } d ? d : ".";
            case "TM_FILEPATH": return FilePath;
            case "RELATIVE_FILEPATH":
                if (FilePath is null) return null;
                return WorkspaceFolder is { Length: > 0 } ? Path.GetRelativePath(WorkspaceFolder, FilePath) : FilePath;
            case "CLIPBOARD": return string.IsNullOrEmpty(Clipboard) ? null : Clipboard;
            case "WORKSPACE_NAME": return WorkspaceName ?? (WorkspaceFolder is null ? null : Path.GetFileName(WorkspaceFolder.TrimEnd('/', '\\')));
            case "WORKSPACE_FOLDER": return WorkspaceFolder;
            case "CURSOR_INDEX": return CursorIndex.ToString(inv);
            case "CURSOR_NUMBER": return (CursorIndex + 1).ToString(inv);
            case "CURRENT_YEAR": return now.Year.ToString("D4", inv);
            case "CURRENT_YEAR_SHORT": return (now.Year % 100).ToString("D2", inv);
            case "CURRENT_MONTH": return now.Month.ToString("D2", inv);
            case "CURRENT_MONTH_NAME": return now.ToString("MMMM", inv);
            case "CURRENT_MONTH_NAME_SHORT": return now.ToString("MMM", inv);
            case "CURRENT_DATE": return now.Day.ToString("D2", inv);
            case "CURRENT_DAY_NAME": return now.ToString("dddd", inv);
            case "CURRENT_DAY_NAME_SHORT": return now.ToString("ddd", inv);
            case "CURRENT_HOUR": return now.Hour.ToString("D2", inv);
            case "CURRENT_MINUTE": return now.Minute.ToString("D2", inv);
            case "CURRENT_SECOND": return now.Second.ToString("D2", inv);
            case "CURRENT_SECONDS_UNIX": return now.ToUnixTimeSeconds().ToString(inv);
            case "CURRENT_TIMEZONE_OFFSET":
                var offset = now.Offset;
                return (offset < TimeSpan.Zero ? "-" : "+") + Math.Abs(offset.Hours).ToString("D2", inv) + ":" + Math.Abs(offset.Minutes).ToString("D2", inv);
            case "RANDOM": return Rng.Next(0, 1_000_000).ToString("D6", inv);
            case "RANDOM_HEX": return Rng.Next(0, 0x1000000).ToString("x6", inv);
            case "UUID": return Guid.NewGuid().ToString();
            case "BLOCK_COMMENT_START": return BlockCommentStart;
            case "BLOCK_COMMENT_END": return BlockCommentEnd;
            case "LINE_COMMENT": return LineComment;
            default: return null;
        }
    }

    private Random Rng => _random ??= RandomSeed is { } seed ? new Random(seed) : Random.Shared;
}
