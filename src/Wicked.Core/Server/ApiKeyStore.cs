using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wicked.Core.Server;

/// <summary>An API key as listed: never the key itself (only its hash is kept), just enough to recognise it.</summary>
public sealed record ApiKeyInfo(string Id, string Name, string Prefix, DateTimeOffset Created);

/// <summary>
/// The keys the API gateway accepts (D12), kept in the profile's config folder as SHA-256 hashes: a key is shown once, when it is
/// made, and cannot be read back. With no keys the gateway is open on this machine only; any key turns checking on. A key
/// is presented as <c>Authorization: Bearer</c> or <c>x-api-key</c>.
/// </summary>
public sealed class ApiKeyStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    private readonly SemaphoreSlim _write = new(1, 1);

    private sealed record Stored(string Id, string Name, string Hash, string Prefix, DateTimeOffset Created);

    public string Path { get; } = path;

    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(CancellationToken ct = default) =>
        [.. (await ReadAsync(ct).ConfigureAwait(false)).Select(k => new ApiKeyInfo(k.Id, k.Name, k.Prefix, k.Created))];

    /// <summary>Makes a key named <paramref name="name"/>; the returned text is the only time it can be seen.</summary>
    public async Task<(ApiKeyInfo Info, string Key)> CreateAsync(string name, CancellationToken ct = default)
    {
        var key = "wk-" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var info = new ApiKeyInfo(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4)), string.IsNullOrWhiteSpace(name) ? "Unnamed key" : name.Trim(), key[..7] + "…", DateTimeOffset.UtcNow);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            all.Add(new Stored(info.Id, info.Name, Hash(key), info.Prefix, info.Created));
            await WriteAsync(all, ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }

        return (info, key);
    }

    public async Task<bool> RevokeAsync(string id, CancellationToken ct = default)
    {
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var all = await ReadAsync(ct).ConfigureAwait(false);
            if (all.RemoveAll(k => k.Id == id) == 0) return false;
            await WriteAsync(all, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _write.Release();
        }
    }

    /// <summary>The keys as the gateway checks them: a snapshot, taken again when the keys change.</summary>
    public async Task<ApiKeyCheck> LoadCheckAsync(CancellationToken ct = default) =>
        new([.. (await ReadAsync(ct).ConfigureAwait(false)).Select(k => (new ApiKeyInfo(k.Id, k.Name, k.Prefix, k.Created), Convert.FromHexString(k.Hash)))]);

    public static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<List<Stored>> ReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists(Path)) return [];
            return JsonSerializer.Deserialize<List<Stored>>(await File.ReadAllTextAsync(Path, ct).ConfigureAwait(false), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warn($"Ignoring unreadable {Path}: {ex.Message}");
            return [];
        }
    }

    private async Task WriteAsync(List<Stored> keys, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        var temp = Path + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(keys, Json), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>A snapshot of the keys to check a request against. <see cref="Open"/> means there are none: nothing is required of a local caller.</summary>
public sealed class ApiKeyCheck(IReadOnlyList<(ApiKeyInfo Info, byte[] Hash)> keys)
{
    public bool Open => keys.Count == 0;

    public int Count => keys.Count;

    /// <summary>The key a request presents (<c>Authorization: Bearer</c> or <c>x-api-key</c>), if it is one of ours. The comparison does not stop at the first differing byte.</summary>
    public ApiKeyInfo? Match(string? authorization, string? apiKeyHeader)
    {
        var presented = apiKeyHeader is { Length: > 0 } ? apiKeyHeader
            : authorization is { Length: > 7 } && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        if (presented is null) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        ApiKeyInfo? found = null;
        foreach (var (info, stored) in keys)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, stored)) found = info;
        }

        return found;
    }
}
