using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wicked.Core.Models;

/// <summary>What the update checks remember between runs (the profile's state folder): what the Hub last said, what was dismissed, what failed.</summary>
public sealed class UpdateStateFile
{
    /// <summary>The ETag of the Hub's last answer for a repo, and the commit that answer described.</summary>
    public Dictionary<string, string> ETags { get; set; } = [];

    public Dictionary<string, string> Shas { get; set; } = [];

    /// <summary>Models whose repo moved to a commit with the very same weights: model id to that commit (so its file tree is not asked again).</summary>
    public Dictionary<string, string> Unchanged { get; set; } = [];

    /// <summary>The update notice the user dismissed: model id to the commit it was about. A newer commit is news again.</summary>
    public Dictionary<string, string> Dismissed { get; set; } = [];

    /// <summary>An automatic update that was rolled back: model id to the commit that failed, so it is not tried again every day.</summary>
    public Dictionary<string, string> AutoFailed { get; set; } = [];

    public DateTimeOffset? LastSweep { get; set; }

    /// <summary>The catalog generation the picks were last compared at, and the picks themselves.</summary>
    public string? CatalogGenerated { get; set; }

    public string? PickSignature { get; set; }

    /// <summary>The better-pick suggestions the user dismissed (model ids).</summary>
    public List<string> DismissedPicks { get; set; } = [];

    /// <summary>The app release the user dismissed.</summary>
    public string? DismissedApp { get; set; }
}

/// <summary>An update being applied, written before the files are swapped so a crash in the middle is finished or undone at the next start.</summary>
public sealed class UpdateJournalEntry
{
    public string ModelId { get; set; } = "";

    public string StoreRoot { get; set; } = "";

    public string? FromRevision { get; set; }

    public string? ToRevision { get; set; }

    /// <summary>Paths relative to the model's folder.</summary>
    public List<string> Files { get; set; } = [];

    /// <summary>The manifest as it was before the swap, as JSON.</summary>
    public string? OldManifest { get; set; }

    public bool WasLoaded { get; set; }
}

/// <summary>Reads and writes the update state and the update journal: lazily loaded, changed under a lock, written through a temporary file and a rename.</summary>
internal sealed class UpdateStateStore(string stateFile, string journalFile)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NewLine = "\n",
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<UpdateStateFile> SnapshotAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadLockedAsync(ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<UpdateStateFile>(JsonSerializer.Serialize(state, Json), Json)!;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ChangeAsync(Action<UpdateStateFile> change, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadLockedAsync(ct).ConfigureAwait(false);
            change(state);
            await WriteAsync(stateFile, state, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<UpdateJournalEntry>> ReadJournalAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ReadJournalLockedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ChangeJournalAsync(Action<List<UpdateJournalEntry>> change, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await ReadJournalLockedAsync(ct).ConfigureAwait(false);
            change(entries);
            await WriteAsync(journalFile, entries, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Read from the file every time: the window and the daemon share it, and each must see what the other wrote.</summary>
    private async Task<UpdateStateFile> LoadLockedAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(stateFile))
            {
                return JsonSerializer.Deserialize<UpdateStateFile>(await File.ReadAllTextAsync(stateFile, ct).ConfigureAwait(false), Json) ?? new UpdateStateFile();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {stateFile}: {ex.Message}");
        }

        return new UpdateStateFile();
    }

    private async Task<List<UpdateJournalEntry>> ReadJournalLockedAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(journalFile))
            {
                return JsonSerializer.Deserialize<List<UpdateJournalEntry>>(await File.ReadAllTextAsync(journalFile, ct).ConfigureAwait(false), Json) ?? [];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {journalFile}: {ex.Message}");
        }

        return [];
    }

    private static async Task WriteAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, Json), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }
}
