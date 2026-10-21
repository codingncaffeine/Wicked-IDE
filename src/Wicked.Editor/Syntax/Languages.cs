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
        if (extension.Equals(".csx", StringComparison.OrdinalIgnoreCase)) extension = ".cs";
        if (AddedGrammars.For(name, extension) is { } added) return added.Info;
        lock (Gate)
        {
            var options = Index.Value;
            var language = ByName.TryGetValue(name, out var id)
                ? options.GetAvailableLanguages().FirstOrDefault(l => l.Id == id)
                : extension.Length > 0 ? options.GetLanguageByExtension(extension) : null;
            if (language is null) return null;
            var scope = options.GetScopeByLanguageId(language.Id);
            return scope is null ? null : new LanguageInfo(language.Id, scope, OptionsFrom(language.Configuration) with { CloseTags = TagClosingFor(language.Id) });
        }
    }

    /// <summary>Names a code fence uses for languages whose id or extension is spelled differently.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["js"] = "javascript",
        ["jsx"] = "javascriptreact",
        ["ts"] = "typescript",
        ["tsx"] = "typescriptreact",
        ["py"] = "python",
        ["sh"] = "shellscript",
        ["bash"] = "shellscript",
        ["zsh"] = "shellscript",
        ["shell"] = "shellscript",
        ["console"] = "shellscript",
        ["cs"] = "csharp",
        ["c#"] = "csharp",
        ["c++"] = "cpp",
        ["cc"] = "cpp",
        ["rs"] = "rust",
        ["rb"] = "ruby",
        ["yml"] = "yaml",
        ["md"] = "markdown",
        ["ps1"] = "powershell",
        ["golang"] = "go",
        ["kt"] = "kotlin",
        ["docker"] = "dockerfile",
        ["objc"] = "objective-c",
        ["tex"] = "latex",
        ["jsonc"] = "jsonc",
    };

    /// <summary>The language a Markdown code fence names ("py", "C#", "bash", "json"): by id, alias or extension. Null for plain text or a name no grammar covers.</summary>
    public static LanguageInfo? ForName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = name.Trim();
        if (AddedGrammars.ForLanguage(key) is { } named) return named.Info;
        lock (Gate)
        {
            var options = Index.Value;
            var wanted = Aliases.TryGetValue(key, out var alias) ? alias : key;
            var language = options.GetAvailableLanguages().FirstOrDefault(l => string.Equals(l.Id, wanted, StringComparison.OrdinalIgnoreCase))
                ?? options.GetLanguageByExtension("." + key.TrimStart('.'));
            if (language is null) return null;
            var scope = options.GetScopeByLanguageId(language.Id);
            return scope is null ? null : new LanguageInfo(language.Id, scope, OptionsFrom(language.Configuration) with { CloseTags = TagClosingFor(language.Id) });
        }
    }

    /// <summary>Every language id, for a language picker.</summary>
    public static IReadOnlyList<string> All()
    {
        lock (Gate) return [.. Index.Value.GetAvailableLanguages().Select(l => l.Id).Concat(AddedGrammars.LanguageIds).Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The name a language goes by in lists and the status bar ("C#", "Python", "Shell Script"): its grammar's first alias,
    /// as VS Code shows it; the id for a language with none.
    /// </summary>
    public static string DisplayName(string id)
    {
        if (id == "plaintext") return "Plain Text";
        lock (Gate)
        {
            var language = Index.Value.GetAvailableLanguages().FirstOrDefault(l => l.Id == id);
            return language?.Aliases is { Count: > 0 } aliases && !string.IsNullOrWhiteSpace(aliases[0]) ? aliases[0] : id;
        }
    }

    internal static RegistryOptions Registry => Index.Value;

    /// <summary>What a TextMate registry reads grammars through: the bundled ones, then those extensions added.</summary>
    internal static TextMateSharp.Registry.IRegistryOptions Options { get; } = new ExtensibleOptions();

    /// <summary>The languages whose tags close themselves, as VS Code's HTML and TypeScript support close them (html.autoClosingTags).</summary>
    private static TagClosing TagClosingFor(string id) => id switch
    {
        "html" or "vue" or "svelte" or "handlebars" or "razor" => TagClosing.Html,
        "xml" or "xsl" or "svg" => TagClosing.Xml, // not JSX: a generic (useState<string>) looks like a tag without a language server
        _ => TagClosing.None,
    };

    private static EditOptions OptionsFrom(LanguageConfiguration? config)
    {
        if (config is null) return EditOptions.Default with { LineComment = null, BlockComment = null };
        var options = EditOptions.Default with
        {
            LineComment = string.IsNullOrEmpty(config.Comments?.LineComment) ? null : config.Comments.LineComment,
            BlockComment = config.Comments?.BlockComment is { Count: 2 } block ? (block[0], block[1]) : null,
        };

        // A language configuration lists its pairs in two shapes: ["{", "}"] (which TextMateSharp reads as CharPairs) and
        // { "open": "'", "close": "'", "notIn": [...] } (AutoPairs). Both count.
        var pairs = new List<BracketPair>();
        foreach (var chars in config.AutoClosingPairs?.CharPairs ?? [])
        {
            if (chars is { Count: 2 }) pairs.Add(new BracketPair(chars[0].ToString(), chars[1].ToString()));
        }

        foreach (var pair in config.AutoClosingPairs?.AutoPairs ?? [])
        {
            if (!string.IsNullOrEmpty(pair.Open) && !string.IsNullOrEmpty(pair.Close)) pairs.Add(new BracketPair(pair.Open, pair.Close));
        }

        if (pairs.Count > 0) options = options with { Pairs = [.. pairs.Distinct()] };
        return options;
    }
}
