using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wicked.Core.Downloads;
using Wicked.Core.Hub;

namespace Wicked.Core.Store;

/// <summary>A file of a stored model. <c>Path</c> is relative to the model's folder, with forward slashes.</summary>
public sealed record StoredFile(string Path, long Size, string? Sha256);

/// <summary>
/// The <c>wicked.json</c> beside a model's files (D6): where it came from and what the user agreed to. It is the
/// provenance record that makes updates (D7) possible; a model without one was copied in by hand and still works.
/// </summary>
public sealed record ModelManifest(
    int Version,
    string? SourceRepo,
    string? Revision,
    IReadOnlyList<StoredFile> Files,
    string? LicenseId,
    DateTimeOffset? AcceptedAt,
    string? CatalogId,
    DateTimeOffset InstalledAt,
    IReadOnlyDictionary<string, string> Settings)
{
    public const int CurrentVersion = 1;

    public static ModelManifest New(string? sourceRepo, string? revision, IEnumerable<StoredFile> files, string? licenseId = null, DateTimeOffset? acceptedAt = null, string? catalogId = null) =>
        new(CurrentVersion, sourceRepo, revision, [.. files], licenseId, acceptedAt, catalogId, DateTimeOffset.UtcNow, new Dictionary<string, string>());
}

/// <summary>One quant of a model: the router loads these. <see cref="Id"/> is <c>publisher/repo:QUANT</c> (just <c>publisher/repo</c> when the name has no quant).</summary>
public sealed record StoredVariant(string Id, string Quant, IReadOnlyList<StoredFile> Files, long Size, string? MmprojPath, GgufInfo? Gguf)
{
    /// <summary>The file to pass to the engine: the first shard of a split model.</summary>
    public string PrimaryPath => Files[0].Path;
}

/// <summary>A model folder (<c>publisher/repo</c>) with everything found in it.</summary>
public sealed record StoredModel(string Id, string Directory, ModelManifest? Manifest, IReadOnlyList<StoredVariant> Variants, long SizeOnDisk)
{
    /// <summary>True when no <c>wicked.json</c> exists: someone copied the GGUF in.</summary>
    public bool HandCopied => Manifest is null;
}

/// <summary>
/// The on-disk model store (D6): <c>&lt;root&gt;/&lt;publisher&gt;/&lt;repo&gt;/&lt;file&gt;.gguf</c>, plain files another tool can read. Scanning
/// also finds hand-copied GGUFs anywhere under the root and reads their headers. Every path built from an id or file name is
/// checked to stay inside the root, deletes remove only files the store knows are models (never a wildcard), and nothing
/// blocks a caller's thread.
/// </summary>
public sealed partial class ModelStore(string root)
{
    public const string ManifestName = "wicked.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ConcurrentDictionary<(string Path, long Size, long Ticks), GgufInfo?> _headers = new();
    private readonly SemaphoreSlim _write = new(1, 1);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Free bytes on the drive that holds the store, or null when the system cannot say.</summary>
    public long? FreeSpace()
    {
        Directory.CreateDirectory(Root);
        return DownloadManager.DriveFreeSpace(Root);
    }

    /// <summary>The folder of a model id (<c>publisher/repo</c>); throws for ids that are not two plain names.</summary>
    public string DirectoryFor(string modelId)
    {
        var parts = modelId.Split('/');
        if (parts.Length != 2 || parts.Any(p => !NamePattern().IsMatch(p) || p.EndsWith('.'))) throw new ArgumentException($"Not a model id (expected publisher/repo): '{modelId}'", nameof(modelId));
        var managed = Path.Combine(Root, parts[0], parts[1]);
        // "local/name" is also how hand-copied folders sitting directly under the root are named.
        return parts[0] == "local" && !Directory.Exists(managed) && Directory.Exists(Path.Combine(Root, parts[1])) ? Path.Combine(Root, parts[1]) : managed;
    }

    /// <summary>Where a repo file is stored; <paramref name="relativeFile"/> may keep the repo's subfolders but never leaves the model folder.</summary>
    public string PathFor(string modelId, string relativeFile)
    {
        var folder = DirectoryFor(modelId);
        var full = Path.GetFullPath(Path.Combine(folder, relativeFile));
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.GetFileName(full) == ManifestName)
        {
            throw new ArgumentException($"Not a valid file name for {modelId}: '{relativeFile}'", nameof(relativeFile));
        }

        return full;
    }

    /// <summary>Finds every model under the root: managed ones by their manifest, anything else by its <c>.gguf</c> files.</summary>
    public async Task<IReadOnlyList<StoredModel>> ScanAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(Root)) return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true };

        // Group by model: publisher/repo for anything three or more levels down (subfolders such as Q4_K_M/ belong to the repo),
        // a top-level folder as one local model, and a loose file in the root as a model of its own.
        var groups = new SortedDictionary<string, (string Directory, bool Loose, List<(string Full, string Relative, long Size)> Files)>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Root, "*.gguf", options).Order(StringComparer.Ordinal))
        {
            var parts = Path.GetRelativePath(Root, file).Split(Path.DirectorySeparatorChar);
            string id;
            string directory;
            bool loose = false;
            if (parts.Length >= 3)
            {
                id = parts[0] + "/" + parts[1];
                directory = Path.Combine(Root, parts[0], parts[1]);
            }
            else if (parts.Length == 2)
            {
                id = "local/" + parts[0];
                directory = Path.Combine(Root, parts[0]);
            }
            else
            {
                id = "local/" + Path.GetFileNameWithoutExtension(file);
                directory = Root;
                loose = true;
            }

            if (!groups.TryGetValue(id, out var group)) groups[id] = group = (directory, loose, []);
            group.Files.Add((file, Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(file).Length));
        }

        var models = new List<StoredModel>();
        foreach (var (id, group) in groups)
        {
            ct.ThrowIfCancellationRequested();
            var manifest = group.Loose ? null : await ReadManifestFileAsync(group.Directory, ct).ConfigureAwait(false);
            models.Add(await BuildAsync(id, group.Directory, group.Loose, manifest, group.Files, ct).ConfigureAwait(false));
        }

        return models;
    }

    public async Task<StoredModel?> GetAsync(string modelId, CancellationToken ct = default) =>
        (await ScanAsync(ct).ConfigureAwait(false)).FirstOrDefault(m => m.Id == modelId);

    public async Task<ModelManifest?> ReadManifestAsync(string modelId, CancellationToken ct = default) =>
        await ReadManifestFileAsync(DirectoryFor(modelId), ct).ConfigureAwait(false);

    /// <summary>Writes the manifest atomically (temp file, then rename).</summary>
    public async Task WriteManifestAsync(string modelId, ModelManifest manifest, CancellationToken ct = default)
    {
        var directory = DirectoryFor(modelId);
        Directory.CreateDirectory(directory);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(directory, ManifestName);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(manifest, Json), ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>Sets or removes (<paramref name="value"/> null) one per-model setting, creating a manifest for a hand-copied model.</summary>
    public async Task SetSettingAsync(string modelId, string key, string? value, CancellationToken ct = default)
    {
        var manifest = await ReadManifestAsync(modelId, ct).ConfigureAwait(false)
                       ?? ModelManifest.New(null, null, []);
        var settings = new Dictionary<string, string>(manifest.Settings, StringComparer.Ordinal);
        if (value is null) settings.Remove(key);
        else settings[key] = value;
        await WriteManifestAsync(modelId, manifest with { Settings = settings }, ct).ConfigureAwait(false);
    }

    /// <summary>Records a finished download: hashes are taken from the download manager's result, sizes from disk.</summary>
    public async Task RegisterAsync(string modelId, string? sourceRepo, string? revision, IEnumerable<(string Relative, string? Sha256)> files, string? licenseId = null, DateTimeOffset? acceptedAt = null, string? catalogId = null, CancellationToken ct = default)
    {
        var existing = await ReadManifestAsync(modelId, ct).ConfigureAwait(false);
        var stored = (existing?.Files ?? []).ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach (var (relative, sha) in files)
        {
            stored[relative] = new StoredFile(relative, new FileInfo(PathFor(modelId, relative)).Length, sha);
        }

        var manifest = (existing ?? ModelManifest.New(sourceRepo, revision, [])) with
        {
            SourceRepo = sourceRepo ?? existing?.SourceRepo,
            Revision = revision ?? existing?.Revision,
            Files = [.. stored.Values.OrderBy(f => f.Path, StringComparer.Ordinal)],
            LicenseId = licenseId ?? existing?.LicenseId,
            AcceptedAt = acceptedAt ?? existing?.AcceptedAt,
            CatalogId = catalogId ?? existing?.CatalogId,
        };
        await WriteManifestAsync(modelId, manifest, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes one quant's files (and the manifest entries for them); the model folder goes when nothing is left.</summary>
    public async Task DeleteVariantAsync(string modelId, string quant, CancellationToken ct = default)
    {
        var model = await GetAsync(modelId, ct).ConfigureAwait(false) ?? throw new FileNotFoundException($"No stored model {modelId}");
        var variant = model.Variants.FirstOrDefault(v => string.Equals(v.Quant, quant, StringComparison.OrdinalIgnoreCase)) ?? throw new FileNotFoundException($"{modelId} has no {quant}");
        foreach (var file in variant.Files)
        {
            DeleteFile(file.Path);
            DeleteFile(file.Path + ".part");
            DeleteFile(file.Path + ".part.json");
        }

        var remaining = model.Variants.Where(v => v != variant).ToList();
        if (model.Manifest is { } manifest)
        {
            var gone = variant.Files.Select(f => Path.GetRelativePath(model.Directory, f.Path).Replace(Path.DirectorySeparatorChar, '/')).ToHashSet(StringComparer.Ordinal);
            await WriteManifestAsync(modelId, manifest with { Files = [.. manifest.Files.Where(f => !gone.Contains(f.Path))] }, ct).ConfigureAwait(false);
        }

        if (remaining.Count == 0)
        {
            if (variant.MmprojPath is { } mmproj) DeleteFile(mmproj);
            if (model.Directory != Root) DeleteFile(Path.Combine(model.Directory, ManifestName));
            PruneEmpty(model.Directory);
        }
    }

    /// <summary>Deletes a whole model: its GGUF files, their leftover partial downloads and the manifest, then the folders if empty. Other files in the folder are left alone.</summary>
    public async Task DeleteAsync(string modelId, CancellationToken ct = default)
    {
        var model = await GetAsync(modelId, ct).ConfigureAwait(false) ?? throw new FileNotFoundException($"No stored model {modelId}");
        foreach (var variant in model.Variants)
        {
            foreach (var file in variant.Files)
            {
                DeleteFile(file.Path);
                DeleteFile(file.Path + ".part");
                DeleteFile(file.Path + ".part.json");
            }

            if (variant.MmprojPath is { } mmproj) DeleteFile(mmproj);
        }

        if (model.Directory != Root) DeleteFile(Path.Combine(model.Directory, ManifestName));
        PruneEmpty(model.Directory);
    }

    /// <summary>Recomputes the sha256 of every file the manifest has a hash for; returns the paths that no longer match.</summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(string modelId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var manifest = await ReadManifestAsync(modelId, ct).ConfigureAwait(false) ?? throw new FileNotFoundException($"{modelId} has no manifest");
        var bad = new List<string>();
        var hashed = manifest.Files.Where(f => f.Sha256 is not null).ToList();
        long total = hashed.Sum(f => f.Size);
        long done = 0;
        foreach (var file in hashed)
        {
            var path = PathFor(modelId, file.Path);
            if (!File.Exists(path))
            {
                bad.Add(file.Path);
                continue;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, n);
                done += n;
                progress?.Report(total == 0 ? 1 : (double)done / total);
            }

            if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase)) bad.Add(file.Path);
        }

        return bad;
    }

    private async Task<StoredModel> BuildAsync(string id, string directory, bool loose, ModelManifest? manifest, List<(string Full, string Relative, long Size)> files, CancellationToken ct)
    {
        var hashes = manifest?.Files.ToDictionary(f => f.Path, f => f.Sha256, StringComparer.Ordinal) ?? [];
        var hubFiles = files.Select(f => new HubFile(f.Relative, f.Size, "", null, false)).ToList();
        var mmprojs = GgufNames.Projectors(hubFiles);
        string Full(string relative) => Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        var variants = new List<StoredVariant>();
        foreach (var variant in GgufNames.Variants(hubFiles))
        {
            var stored = variant.Files.Select(f => new StoredFile(Full(f.Path), f.Size, hashes.GetValueOrDefault(f.Path))).ToList();
            var info = await ReadInfoAsync(stored[0].Path, ct).ConfigureAwait(false);
            var mmproj = mmprojs.FirstOrDefault(m => Path.GetDirectoryName(m.Path) == Path.GetDirectoryName(variant.Files[0].Path)) ?? mmprojs.FirstOrDefault();
            variants.Add(new StoredVariant(loose ? id : $"{id}:{variant.Quant}", variant.Quant, stored, variant.TotalSize, mmproj is null ? null : Full(mmproj.Path), info));
        }

        // Files whose names carry no quant (a hand-copied "Qwen3-8B.gguf") are still models.
        var covered = variants.SelectMany(v => v.Files).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var file in files.Where(f => !covered.Contains(f.Full) && GgufNames.Parse(f.Relative) is { IsMmproj: false, IsSidecar: false }))
        {
            var info = await ReadInfoAsync(file.Full, ct).ConfigureAwait(false);
            var stem = Path.GetFileNameWithoutExtension(file.Full);
            variants.Add(new StoredVariant(loose ? id : $"{id}:{stem}", "", [new StoredFile(file.Full, file.Size, hashes.GetValueOrDefault(file.Relative))], file.Size, null, info));
        }

        return new StoredModel(id, directory, manifest, variants, files.Sum(f => f.Size));
    }

    private async Task<GgufInfo?> ReadInfoAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var key = (path, info.Length, info.LastWriteTimeUtc.Ticks);
        if (_headers.TryGetValue(key, out var cached)) return cached;
        try
        {
            var header = await GgufReader.ReadHeaderAsync(path, ct).ConfigureAwait(false);
            return _headers[key] = GgufInfo.From(header);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException)
        {
            Log.Warn($"Could not read the GGUF header of {path}: {ex.Message}");
            return _headers[key] = null;
        }
    }

    private static async Task<ModelManifest?> ReadManifestFileAsync(string directory, CancellationToken ct)
    {
        var path = Path.Combine(directory, ManifestName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<ModelManifest>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Warn($"Ignoring unreadable {path}: {ex.Message}");
            return null;
        }
    }

    private void DeleteFile(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException($"Refusing to delete outside the store: {full}");
        try
        {
            File.Delete(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not delete {full}: {ex.Message}");
        }
    }

    private void PruneEmpty(string directory)
    {
        // Remove empty folders from the model folder up to (not including) the root; Directory.Delete refuses non-empty ones.
        for (var current = Path.GetFullPath(directory); current.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal); current = Path.GetDirectoryName(current)!)
        {
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(current).Where(d => !Directory.EnumerateFileSystemEntries(d).Any())) Directory.Delete(sub);
                if (Directory.EnumerateFileSystemEntries(current).Any()) return;
                Directory.Delete(current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
