using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wicked.Core.Extensions;

/// <summary>A theme file an extension ships.</summary>
public sealed record ThemeContribution
{
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";
}

/// <summary>A TextMate grammar an extension ships, with the files it claims.</summary>
public sealed record GrammarContribution
{
    [JsonPropertyName("language")]
    public string Language { get; init; } = "";

    [JsonPropertyName("scopeName")]
    public string ScopeName { get; init; } = "";

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";

    [JsonPropertyName("extensions")]
    public List<string>? Extensions { get; init; }

    [JsonPropertyName("fileNames")]
    public List<string>? FileNames { get; init; }

    [JsonPropertyName("configuration")]
    public string? Configuration { get; init; }
}

/// <summary>A snippet file an extension ships; <c>Language</c> narrows a file that has no scopes of its own.</summary>
public sealed record SnippetContribution
{
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("path")]
    public string Path { get; init; } = "";
}

/// <summary>A release asset of a server an extension names (see <see cref="Lsp.ReleaseAsset"/>).</summary>
public sealed record ServerAssetContribution
{
    [JsonPropertyName("platform")]
    public string Platform { get; init; } = "";

    [JsonPropertyName("pattern")]
    public string Pattern { get; init; } = "";

    [JsonPropertyName("packing")]
    public string Packing { get; init; } = "raw";

    [JsonPropertyName("program")]
    public string Program { get; init; } = "";
}

/// <summary>A language server an extension adds to the catalog.</summary>
public sealed record LanguageServerContribution
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("languages")]
    public List<string> Languages { get; init; } = [];

    /// <summary>Program names to look for; one that starts with <c>./</c> is a file inside the extension.</summary>
    [JsonPropertyName("programs")]
    public List<string> Programs { get; init; } = [];

    [JsonPropertyName("arguments")]
    public List<string> Arguments { get; init; } = [];

    /// <summary>system, github, npm, dotnet or go.</summary>
    [JsonPropertyName("install")]
    public string Install { get; init; } = "system";

    [JsonPropertyName("package")]
    public string? Package { get; init; }

    [JsonPropertyName("repository")]
    public string? Repository { get; init; }

    [JsonPropertyName("assets")]
    public List<ServerAssetContribution>? Assets { get; init; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    [JsonPropertyName("rootMarkers")]
    public List<string>? RootMarkers { get; init; }
}

/// <summary>A key binding an extension adds for a command.</summary>
public sealed record KeybindingContribution
{
    [JsonPropertyName("command")]
    public string Command { get; init; } = "";

    [JsonPropertyName("key")]
    public string Key { get; init; } = "";
}

/// <summary>What an extension adds without running any code.</summary>
public sealed record ExtensionContributions
{
    [JsonPropertyName("themes")]
    public List<ThemeContribution> Themes { get; init; } = [];

    [JsonPropertyName("grammars")]
    public List<GrammarContribution> Grammars { get; init; } = [];

    [JsonPropertyName("snippets")]
    public List<SnippetContribution> Snippets { get; init; } = [];

    [JsonPropertyName("languageServers")]
    public List<LanguageServerContribution> LanguageServers { get; init; } = [];

    [JsonPropertyName("keybindings")]
    public List<KeybindingContribution> Keybindings { get; init; } = [];

    /// <summary>Setting keys and their defaults (read through <see cref="ISettingsApi"/> when the user has not set them).</summary>
    [JsonPropertyName("settings")]
    public Dictionary<string, JsonElement> Settings { get; init; } = [];

    public bool IsEmpty => Themes.Count + Grammars.Count + Snippets.Count + LanguageServers.Count + Keybindings.Count + Settings.Count == 0;
}

/// <summary>
/// <c>wicked-extension.json</c>: what an extension is, which API it needs, which assembly runs, when, and what it
/// contributes without code. Parsed with <see cref="Parse"/>, which says in words what is wrong.
/// </summary>
public sealed record ExtensionManifest
{
    public const string FileName = "wicked-extension.json";

    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "";

    [JsonPropertyName("publisher")]
    public string Publisher { get; init; } = "";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("homepage")]
    public string? Homepage { get; init; }

    [JsonPropertyName("engines")]
    public Dictionary<string, string> Engines { get; init; } = [];

    /// <summary>The assembly to load, relative to the extension's folder; absent for an extension that only contributes files.</summary>
    [JsonPropertyName("main")]
    public string? Main { get; init; }

    /// <summary><c>onStartup</c> (the default) or <c>onLanguage:&lt;id&gt;</c>: when the assembly is loaded and activated.</summary>
    [JsonPropertyName("activationEvents")]
    public List<string> ActivationEvents { get; init; } = [];

    [JsonPropertyName("contributes")]
    public ExtensionContributions Contributes { get; init; } = new();

    /// <summary><c>publisher.id</c>, the extension's identity.</summary>
    [JsonIgnore]
    public string FullId => $"{Publisher}.{Id}";

    /// <summary>The folder name an installed copy has: <c>publisher.id-version</c>.</summary>
    [JsonIgnore]
    public string FolderName => $"{FullId}-{Version}";

    private static readonly Regex NamePattern = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionPattern = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static readonly JsonSerializerOptions Json = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = false,
    };

    /// <summary>The manifest in <paramref name="json"/>, or why it cannot be used (unreadable, a missing field, an unsafe name, or an API version this build does not speak).</summary>
    public static (ExtensionManifest? Manifest, string? Problem) Parse(string json)
    {
        ExtensionManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ExtensionManifest>(json, Json);
        }
        catch (JsonException ex)
        {
            return (null, $"{FileName} could not be read: {ex.Message}");
        }

        if (manifest is null) return (null, $"{FileName} is empty");
        if (!NamePattern.IsMatch(manifest.Publisher)) return (null, "\"publisher\" must be lower-case letters, digits, '-' or '_'");
        if (!NamePattern.IsMatch(manifest.Id)) return (null, "\"id\" must be lower-case letters, digits, '-' or '_'");
        if (manifest.Name.Length == 0) return (null, "\"name\" is missing");
        if (!VersionPattern.IsMatch(manifest.Version)) return (null, "\"version\" must look like 1.2.3");
        if (!manifest.Engines.TryGetValue("wicked", out var wanted)) return (null, "\"engines\" must say which Wicked API it needs: { \"wicked\": \"^1.0\" }");
        if (!VersionRange.TryParse(wanted, out var range)) return (null, $"\"engines.wicked\" ({wanted}) is not a version range such as ^1.0");
        if (!range.Contains(WickedApiVersion.Current))
        {
            return (null, $"It needs Wicked API {wanted}; this Wicked speaks {WickedApiVersion.Text}");
        }

        if (manifest.Main is { } main && (Path.IsPathRooted(main) || main.Contains("..", StringComparison.Ordinal))) return (null, "\"main\" must be a path inside the extension");
        foreach (var path in manifest.Contributes.Themes.Select(t => t.Path)
                     .Concat(manifest.Contributes.Grammars.SelectMany(g => new[] { g.Path, g.Configuration ?? "x" }))
                     .Concat(manifest.Contributes.Snippets.Select(s => s.Path)))
        {
            if (path.Length == 0 || Path.IsPathRooted(path) || path.Contains("..", StringComparison.Ordinal)) return (null, $"The contributed path \"{path}\" must be a path inside the extension");
        }

        if (manifest.Contributes.Grammars.Any(g => g.Language.Length == 0 || g.ScopeName.Length == 0)) return (null, "A grammar needs a \"language\" and a \"scopeName\"");
        if (manifest.Contributes.LanguageServers.Any(s => s.Id.Length == 0 || s.Languages.Count == 0 || s.Programs.Count == 0)) return (null, "A language server needs an \"id\", \"languages\" and \"programs\"");
        foreach (var activation in manifest.ActivationEvents)
        {
            if (activation != "onStartup" && !activation.StartsWith("onLanguage:", StringComparison.Ordinal)) return (null, $"Unknown activation event \"{activation}\" (onStartup, onLanguage:<id>)");
        }

        return (manifest, null);
    }

    /// <summary>The language ids whose first document activates the extension; empty when it starts with the IDE.</summary>
    public IReadOnlyList<string> ActivationLanguages =>
        ActivationEvents.Count == 0 || ActivationEvents.Contains("onStartup") ? [] : [.. ActivationEvents.Select(a => a["onLanguage:".Length..])];
}
