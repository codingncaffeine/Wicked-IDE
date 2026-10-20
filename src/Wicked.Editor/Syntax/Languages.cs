using System.Collections.Immutable;
using TextMateSharp.Grammars;
using Wicked.Editor.Model;

namespace Wicked.Editor.Syntax;

/// <summary>A file's language: its grammar's scope and the editing rules its configuration gives.</summary>
/// <param name="Id">The language id ("csharp", "python").</param>
/// <param name="Scope">The TextMate scope of its grammar ("source.cs").</param>
/// <param name="Options">Comment marks and bracket pairs from the language configuration, over the defaults.</param>
public sealed record LanguageInfo(string Id, string Scope, EditOptions Options);

/// <summary>
/// The languages the bundled grammars cover (63 of them, from VS Code's own extensions), looked up by file name. The
/// grammar index is read once, on first use, and is safe to read from any thread after that.
/// </summary>
public static class Languages
{
    private static readonly Lazy<RegistryOptions> Index = new(() => new RegistryOptions(ThemeName.DarkPlus), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lock Gate = new();

    /// <summary>Files known by their whole name, as VS Code's language extensions list them (the bundled index has extensions only).</summary>
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Makefile"] = "makefile",
        ["GNUmakefile"] = "makefile",
        ["Dockerfile"] = "dockerfile",
        ["Containerfile"] = "dockerfile",
        [".gitignore"] = "ignore",
        [".dockerignore"] = "ignore",
        [".npmignore"] = "ignore",
        [".gitattributes"] = "properties",
        [".editorconfig"] = "ini",
        [".bashrc"] = "shellscript",
        [".bash_profile"] = "shellscript",
        [".profile"] = "shellscript",
        [".zshrc"] = "shellscript",
        ["PKGBUILD"] = "shellscript",
        ["Gemfile"] = "ruby",
        ["Rakefile"] = "ruby",
        ["CMakeLists.txt"] = "plaintext",
        ["compose.yaml"] = "dockercompose",
        ["docker-compose.yml"] = "dockercompose",
        ["docker-compose.yaml"] = "dockercompose",
    };

    /// <summary>The language for <paramref name="path"/> by its extension (or whole name, for files like Makefile), or null for plain text.</summary>
    public static LanguageInfo? ForFile(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        lock (Gate)
        {
            var options = Index.Value;
            var language = ByName.TryGetValue(name, out var id)
                ? options.GetAvailableLanguages().FirstOrDefault(l => l.Id == id)
                : extension.Length > 0 ? options.GetLanguageByExtension(extension) : null;
            if (language is null) return null;
            var scope = options.GetScopeByLanguageId(language.Id);
            return scope is null ? null : new LanguageInfo(language.Id, scope, OptionsFrom(language.Configuration));
        }
    }

    /// <summary>Every language id, for a language picker.</summary>
    public static IReadOnlyList<string> All()
    {
        lock (Gate) return [.. Index.Value.GetAvailableLanguages().Select(l => l.Id).Order(StringComparer.Ordinal)];
    }

    internal static RegistryOptions Registry => Index.Value;

    private static EditOptions OptionsFrom(LanguageConfiguration? config)
    {
        if (config is null) return EditOptions.Default with { LineComment = null, BlockComment = null };
        var options = EditOptions.Default with
        {
            LineComment = string.IsNullOrEmpty(config.Comments?.LineComment) ? null : config.Comments.LineComment,
            BlockComment = config.Comments?.BlockComment is { Count: 2 } block ? (block[0], block[1]) : null,
        };

        var pairs = config.AutoClosingPairs?.AutoPairs?.Where(p => !string.IsNullOrEmpty(p.Open) && !string.IsNullOrEmpty(p.Close))
            .Select(p => new BracketPair(p.Open, p.Close)).ToImmutableArray();
        if (pairs is { Length: > 0 } p) options = options with { Pairs = p };
        return options;
    }
}
