using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wicked.Core.Settings;

/// <summary>
/// The user's settings, under VS Code's own names so a settings.json from VS Code mostly reads as-is. Everything here
/// has a control in the Settings page; the file is for those who prefer to type. Immutable: a change makes a new one.
/// </summary>
public sealed record WickedSettings
{
    [JsonPropertyName("editor.fontSize")]
    public double FontSize { get; init; } = 14;

    [JsonPropertyName("editor.lineHeight")]
    public double LineHeight { get; init; } = 1.5;

    [JsonPropertyName("editor.fontLigatures")]
    public bool FontLigatures { get; init; } = true;

    [JsonPropertyName("editor.tabSize")]
    public int TabSize { get; init; } = 4;

    [JsonPropertyName("editor.insertSpaces")]
    public bool InsertSpaces { get; init; } = true;

    [JsonPropertyName("editor.autoClosingBrackets")]
    public bool AutoClosingBrackets { get; init; } = true;

    [JsonPropertyName("files.autoSave")]
    public string AutoSave { get; init; } = "off";

    [JsonPropertyName("files.autoSaveDelay")]
    public int AutoSaveDelay { get; init; } = 1000;

    [JsonPropertyName("files.trimTrailingWhitespace")]
    public bool TrimTrailingWhitespace { get; init; }

    [JsonPropertyName("files.insertFinalNewline")]
    public bool InsertFinalNewline { get; init; }

    [JsonPropertyName("workbench.colorTheme")]
    public string ColorTheme { get; init; } = "Wicked Dark";

    /// <summary>The code's colours, chosen apart from the window's theme (D28); empty means the theme's own.</summary>
    [JsonPropertyName("workbench.codeColorTheme")]
    public string CodeColorTheme { get; init; } = "";

    [JsonPropertyName("window.restoreWindows")]
    public bool RestoreSession { get; init; } = true;

    /// <summary>Where new model installs go; empty means the default folder under the data directory. Models already installed stay where they are.</summary>
    [JsonPropertyName("models.storeLocation")]
    public string StoreLocation { get; init; } = "";

    /// <summary>The Hugging Face access token (for gated models and higher rate limits), kept here in plain text as the Hub's own tools keep theirs.</summary>
    [JsonPropertyName("huggingface.token")]
    public string HuggingFaceToken { get; init; } = "";

    /// <summary>Total download speed cap in megabytes per second; 0 is unlimited.</summary>
    [JsonPropertyName("models.downloadLimitMBps")]
    public double DownloadLimit { get; init; }

    /// <summary>The engine build to use: auto (what this machine calls for), cuda, vulkan, rocm or cpu.</summary>
    [JsonPropertyName("runtime.backend")]
    public string RuntimeBackend { get; init; } = "auto";

    /// <summary>Keys this build does not know, kept so a save never drops them.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Other { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // The same bytes on every platform (the catalog is signed over them): no CRLF on Windows.
        NewLine = "\n",
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>
/// Reads and writes settings.json. Reading tolerates comments and trailing commas (the file is JSONC, as VS Code's is);
/// a file that does not parse leaves the previous settings in force and says why. Disk work only: call off the UI thread.
/// </summary>
public static class SettingsFile
{
    public static async Task<(WickedSettings Settings, string? Problem)> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return (new WickedSettings(), null);
        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<WickedSettings>(stream, WickedSettings.Json, ct).ConfigureAwait(false);
            return (settings ?? new WickedSettings(), null);
        }
        catch (JsonException ex)
        {
            return (new WickedSettings(), $"settings.json could not be read: {ex.Message}");
        }
    }

    public static Task WriteAsync(string path, WickedSettings settings, CancellationToken ct) =>
        Files.TextFile.WriteAsync(path, JsonSerializer.Serialize(settings, WickedSettings.Json) + "\n", Files.FileEncoding.Utf8, ct);
}
