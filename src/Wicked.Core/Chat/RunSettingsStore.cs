using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wicked.Core.Chat;

/// <summary>What is kept in the run-settings file: the user's choice for each model and for how many may be loaded at once.</summary>
internal sealed record RunSettingsFile(int? ModelsMax, Dictionary<string, ModelRunSettings> Models);

/// <summary>
/// The user's per-model run choices (<see cref="ModelRunSettings"/>) and the models-loaded-at-once limit, in one JSON file in the
/// profile's config folder. Reads and writes are on the caller's worker; a write goes through a temporary file and a rename, so a
/// crash leaves the old file or the new one, never half of one.
/// </summary>
internal sealed class RunSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _write = new(1, 1);

    public string Path { get; } = path;

    public async Task<RunSettingsFile> LoadAsync(CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(Path)) return new RunSettingsFile(null, []);
            await using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var file = await JsonSerializer.DeserializeAsync<RunSettingsFile>(stream, Json, ct).ConfigureAwait(false);
            return file is null ? new RunSettingsFile(null, []) : file with { Models = new Dictionary<string, ModelRunSettings>(file.Models ?? [], StringComparer.Ordinal) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {Path}: {ex.Message}");
            return new RunSettingsFile(null, []);
        }
    }

    public async Task SaveAsync(RunSettingsFile file, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
            var temp = Path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(file, Json), ct).ConfigureAwait(false);
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            _write.Release();
        }
    }
}
