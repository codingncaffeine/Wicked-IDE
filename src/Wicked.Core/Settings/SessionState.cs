using System.Text.Json;

namespace Wicked.Core.Settings;

/// <summary>What Wicked remembers between runs that is not a setting: the folders opened and what was open in them.</summary>
public sealed record SessionState
{
    /// <summary>Recently opened folders, newest first.</summary>
    public List<string> RecentFolders { get; init; } = [];

    /// <summary>The folder open when Wicked last closed.</summary>
    public string? LastFolder { get; init; }

    /// <summary>Per folder: the files open in it, the active one last.</summary>
    public Dictionary<string, List<string>> OpenFiles { get; init; } = [];

    /// <summary>Per folder: its breakpoints, exception filters, watch expressions and the launch configuration last chosen.</summary>
    public Dictionary<string, Wicked.Core.Debug.DebugWorkspaceState> Debug { get; init; } = [];

    public double WindowWidth { get; init; } = 1440;

    public double WindowHeight { get; init; } = 900;

    public bool WindowMaximized { get; init; }

    public SessionState WithRecent(string folder) => this with
    {
        RecentFolders = [folder, .. RecentFolders.Where(f => f != folder).Take(19)],
        LastFolder = folder,
    };

    public static async Task<SessionState> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return new SessionState();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<SessionState>(stream, WickedSettings.Json, ct).ConfigureAwait(false) ?? new SessionState();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn($"The session could not be read ({ex.Message}); starting fresh");
            return new SessionState();
        }
    }

    public Task WriteAsync(string path, CancellationToken ct) =>
        Files.TextFile.WriteAsync(path, JsonSerializer.Serialize(this, WickedSettings.Json) + "\n", Files.FileEncoding.Utf8, ct);
}
