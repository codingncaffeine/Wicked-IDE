using System.Collections.Concurrent;
using System.Collections.Immutable;
using Wicked.Editor.Model;

namespace Wicked.Editor.Features;

public enum IndentStyle
{
    Space,
    Tab,
}

public enum EndOfLine
{
    Lf,
    CrLf,
    Cr,
}

/// <summary>
/// The EditorConfig properties that apply to one file, after the spec's rules are applied: <c>indent_style = tab</c>
/// without an <c>indent_size</c> makes the indent size follow <c>tab_width</c>; <c>indent_size = tab</c> takes the
/// <c>tab_width</c>; a missing <c>tab_width</c> takes a numeric <c>indent_size</c>; <c>unset</c> removes a property.
/// Unknown keys are kept in <see cref="Raw"/> (lowercase), and a known key with a value that is not valid is ignored.
/// </summary>
public sealed record EditorConfigProperties
{
    public static readonly EditorConfigProperties Empty = new();

    public IndentStyle? IndentStyle { get; init; }

    /// <summary>Columns per indentation level; null when unset, or <see cref="IndentSizeIsTab"/> with no tab_width to give it.</summary>
    public int? IndentSize { get; init; }

    /// <summary>The file says <c>indent_size = tab</c> (or tab indentation with no size) and gave no tab width.</summary>
    public bool IndentSizeIsTab { get; init; }

    public int? TabWidth { get; init; }

    public EndOfLine? EndOfLine { get; init; }

    /// <summary>latin1, utf-8, utf-8-bom, utf-16be or utf-16le.</summary>
    public string? Charset { get; init; }

    public bool? TrimTrailingWhitespace { get; init; }

    public bool? InsertFinalNewline { get; init; }

    /// <summary>Every property that applied, lowercase keys and values, <c>unset</c> ones removed.</summary>
    public ImmutableDictionary<string, string> Raw { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>"\n", "\r\n" or "\r" for <see cref="EndOfLine"/>, or null.</summary>
    public string? LineBreak => EndOfLine switch
    {
        Features.EndOfLine.Lf => "\n",
        Features.EndOfLine.CrLf => "\r\n",
        Features.EndOfLine.Cr => "\r",
        _ => null,
    };

    /// <summary>
    /// Applies the indentation properties to editing options. Wicked has one tab size: with spaces it is the indent
    /// size (what Tab inserts), with tabs it is the tab width (how wide a tab draws).
    /// </summary>
    public EditOptions Apply(EditOptions options)
    {
        var spaces = IndentStyle is { } style ? style == Features.IndentStyle.Space : options.InsertSpaces;
        var size = spaces ? IndentSize ?? TabWidth : TabWidth ?? IndentSize;
        return options with { InsertSpaces = spaces, TabSize = size is > 0 ? size.Value : options.TabSize };
    }
}

/// <summary>
/// Resolves <c>.editorconfig</c> files for a path: from the file's folder up through its parents until a file says
/// <c>root = true</c> (or the top is reached); nearer files and later sections win. Reading is the caller's: pass a
/// function from a directory to the text of the .editorconfig in it (null when there is none), so tests need no files;
/// <see cref="Disk"/> is the disk-backed default. Parsed files are cached per directory until <see cref="Invalidate"/>;
/// the type is safe to call from several threads.
/// </summary>
public sealed class EditorConfigResolver(Func<string, string?> read)
{
    private sealed record Section(EditorConfigGlob? Glob, List<(string Key, string Value)> Properties);

    private sealed record ParsedFile(bool IsRoot, List<Section> Sections);

    private readonly ConcurrentDictionary<string, ParsedFile?> _cache = new();

    /// <summary>The default: reads <c>.editorconfig</c> from a directory on disk, null when absent or unreadable.</summary>
    public static EditorConfigResolver Disk { get; } = new(ReadFromDisk);

    private static string? ReadFromDisk(string dir)
    {
        try
        {
            var path = Path.Combine(dir, ".editorconfig");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Forgets the cached file of a directory (call when an .editorconfig is saved), or all of them.</summary>
    public void Invalidate(string? dir = null)
    {
        if (dir is null) _cache.Clear();
        else
        {
            // The cache is keyed by Path.GetDirectoryName, which writes the platform's separator; a caller may not.
            _cache.TryRemove(dir, out _);
            _cache.TryRemove(dir.Replace('/', Path.DirectorySeparatorChar), out _);
        }
    }

    /// <summary>The properties that apply to <paramref name="filePath"/> (an absolute path).</summary>
    public EditorConfigProperties Resolve(string filePath)
    {
        var path = filePath.Replace('\\', '/');
        var files = new List<(string Dir, ParsedFile File)>();
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrEmpty(dir))
        {
            var parsed = _cache.GetOrAdd(dir, d => Parse(read(d)));
            if (parsed is not null)
            {
                files.Add((dir, parsed));
                if (parsed.IsRoot) break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        // Farthest first, so nearer files and later sections overwrite.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = files.Count - 1; i >= 0; i--)
        {
            var (fileDir, file) = files[i];
            var prefix = fileDir.Replace('\\', '/').TrimEnd('/') + "/"; // not a path: EditorConfig globs are written with / on every platform
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = path[prefix.Length..];
            foreach (var section in file.Sections)
            {
                if (section.Glob is null || !section.Glob.IsMatch(relative)) continue;
                foreach (var (key, value) in section.Properties) values[key] = value;
            }
        }

        return Build(values);
    }

    // ── Parsing ──────────────────────────────────────────────────────────────────────────────────

    private static ParsedFile? Parse(string? content)
    {
        if (content is null) return null;
        var root = false;
        var sections = new List<Section>();
        Section? current = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var end = line.LastIndexOf(']');
                if (end <= 0) continue;
                var name = line[1..end];
                current = new Section(name.Length > 4096 ? null : EditorConfigGlob.Create(name), []);
                sections.Add(current);
                continue;
            }

            var eq = line.IndexOf('=');
            var colon = line.IndexOf(':');
            var split = eq >= 0 && (colon < 0 || eq < colon) ? eq : colon;
            if (split <= 0) continue;
            var key = line[..split].Trim().ToLowerInvariant();
            var value = line[(split + 1)..].Trim();
            if (current is null)
            {
                if (key == "root") root = value.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                current.Properties.Add((key, value));
            }
        }

        return new ParsedFile(root, sections);
    }

    private static EditorConfigProperties Build(Dictionary<string, string> merged)
    {
        // Values of the keys the spec defines are case-insensitive: compare lowercase; "unset" removes the key.
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in merged)
        {
            var v = value.ToLowerInvariant();
            if (v == "unset") continue;
            values[key] = SpecKeys.Contains(key) ? v : value;
        }

        values.TryGetValue("indent_style", out var style);
        values.TryGetValue("indent_size", out var size);
        values.TryGetValue("tab_width", out var width);

        IndentStyle? indentStyle = style switch { "tab" => Features.IndentStyle.Tab, "space" => Features.IndentStyle.Space, _ => null };
        var tabWidth = PositiveInt(width);
        int? indentSize = null;
        var sizeIsTab = false;
        if (size == "tab" || (size is null && indentStyle == Features.IndentStyle.Tab))
        {
            if (tabWidth is not null) indentSize = tabWidth;
            else sizeIsTab = true;
        }
        else
        {
            indentSize = PositiveInt(size);
        }

        tabWidth ??= indentSize;

        EndOfLine? eol = values.GetValueOrDefault("end_of_line") switch
        {
            "lf" => Features.EndOfLine.Lf,
            "crlf" => Features.EndOfLine.CrLf,
            "cr" => Features.EndOfLine.Cr,
            _ => null,
        };

        var charset = values.GetValueOrDefault("charset");
        if (charset is not ("latin1" or "utf-8" or "utf-8-bom" or "utf-16be" or "utf-16le")) charset = null;

        return new EditorConfigProperties
        {
            IndentStyle = indentStyle,
            IndentSize = indentSize,
            IndentSizeIsTab = sizeIsTab,
            TabWidth = tabWidth,
            EndOfLine = eol,
            Charset = charset,
            TrimTrailingWhitespace = Bool(values.GetValueOrDefault("trim_trailing_whitespace")),
            InsertFinalNewline = Bool(values.GetValueOrDefault("insert_final_newline")),
            Raw = values.ToImmutableDictionary(),
        };
    }

    private static readonly HashSet<string> SpecKeys =
    [
        "indent_style", "indent_size", "tab_width", "end_of_line", "charset", "trim_trailing_whitespace", "insert_final_newline", "root",
    ];

    private static int? PositiveInt(string? s) => int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    private static bool? Bool(string? s) => s switch { "true" => true, "false" => false, _ => null };
}
