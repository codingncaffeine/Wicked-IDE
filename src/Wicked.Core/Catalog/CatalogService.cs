using System.Net;
using System.Text;

namespace Wicked.Core.Catalog;

public enum CatalogOrigin
{
    Network,
    Cache,
    Embedded,
}

/// <summary>A verified catalog, where it came from, and a note when a newer source was rejected or unreachable.</summary>
public sealed record CatalogLoad(ModelCatalog Catalog, CatalogOrigin Origin, string? Note);

/// <summary>
/// Gets the catalog (D8): the signed file from the repository, kept in the cache folder, with a copy built into the app as the
/// offline fallback. Every source is verified against the compiled-in public key before it is believed, and a source older
/// than one already held (a replayed old file) is ignored. Never throws for network trouble: the worst case is the built-in copy.
/// </summary>
public sealed class CatalogService(HttpClient http, string cacheDirectory, Uri? baseAddress = null, string? publicKey = null)
{
    public static readonly Uri DefaultBase = new("https://raw.githubusercontent.com/codingncaffeine/Wicked-IDE/main/catalog/");

    private const string FileName = "catalog.json";
    private const int MaxBytes = 8 * 1024 * 1024;

    private readonly Uri _base = baseAddress ?? DefaultBase;
    private readonly string _key = publicKey ?? CatalogVerifier.PublicKey;

    /// <summary>The copy built into the assembly. It is checked against the key too, but trusted if the check fails (it ships in the binary).</summary>
    public static (byte[] Json, string Signature) ReadEmbedded()
    {
        var assembly = typeof(CatalogService).Assembly;
        byte[] Read(string name)
        {
            using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException($"Missing embedded resource {name}");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        return (Read(FileName), Encoding.UTF8.GetString(Read(FileName + ".sig")).Trim());
    }

    public async Task<CatalogLoad> LoadAsync(bool allowNetwork = true, CancellationToken ct = default)
    {
        var (embeddedJson, embeddedSignature) = ReadEmbedded();
        if (!CatalogVerifier.Verify(embeddedJson, embeddedSignature, _key)) Log.Warn("The built-in catalog does not match the signing key; using it anyway");
        var best = new CatalogLoad(ModelCatalog.Parse(embeddedJson), CatalogOrigin.Embedded, null);

        if (await ReadCacheAsync(ct).ConfigureAwait(false) is { } cached && cached.Catalog.Generated > best.Catalog.Generated) best = cached;
        if (!allowNetwork) return best;

        try
        {
            var fetched = await FetchAsync(best.Catalog.Generated, ct).ConfigureAwait(false);
            if (fetched is not null) return fetched;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Log.Warn($"Could not refresh the model catalog: {ex.Message}");
            return best with { Note = "Using the saved catalog: the online one could not be reached or did not verify" };
        }

        return best;
    }

    private async Task<CatalogLoad?> FetchAsync(DateTimeOffset newerThan, CancellationToken ct)
    {
        var etagFile = Path.Combine(cacheDirectory, FileName + ".etag");
        var known = File.Exists(etagFile) && File.Exists(Path.Combine(cacheDirectory, FileName)) ? await File.ReadAllTextAsync(etagFile, ct).ConfigureAwait(false) : null;
        var (json, etag) = await GetBytesAsync(new Uri(_base, FileName), known, ct).ConfigureAwait(false);
        if (json is null) return null; // 304: the copy held is still the current one
        var signature = Encoding.UTF8.GetString((await GetBytesAsync(new Uri(_base, FileName + ".sig"), null, ct).ConfigureAwait(false)).Bytes!);
        if (!CatalogVerifier.Verify(json, signature, _key)) throw new InvalidDataException("The downloaded catalog's signature does not verify");
        var catalog = ModelCatalog.Parse(json);
        if (catalog.Schema > ModelCatalog.SupportedSchema) throw new InvalidDataException($"The online catalog uses schema {catalog.Schema}, which this version cannot read");
        if (catalog.Generated <= newerThan) return null;

        await WriteCacheAsync(json, signature, ct).ConfigureAwait(false);
        if (etag is not null) await File.WriteAllTextAsync(etagFile, etag, ct).ConfigureAwait(false);
        return new CatalogLoad(catalog, CatalogOrigin.Network, null);
    }

    private async Task<(byte[]? Bytes, string? ETag)> GetBytesAsync(Uri url, string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var newTag = response.Headers.ETag?.ToString();
        if (response.StatusCode == HttpStatusCode.NotModified) return (null, newTag);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidDataException($"{url} was not found");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("The catalog file is implausibly large");
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                if (buffer.Length > MaxBytes) throw new InvalidDataException("The catalog file is implausibly large");
            }

            return (buffer.ToArray(), newTag);
        }
    }

    private async Task<CatalogLoad?> ReadCacheAsync(CancellationToken ct)
    {
        var path = Path.Combine(cacheDirectory, FileName);
        try
        {
            if (!File.Exists(path) || !File.Exists(path + ".sig")) return null;
            var json = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var signature = await File.ReadAllTextAsync(path + ".sig", ct).ConfigureAwait(false);
            if (!CatalogVerifier.Verify(json, signature, _key))
            {
                Log.Warn("The cached catalog does not verify; ignoring it");
                return null;
            }

            var catalog = ModelCatalog.Parse(json);
            return catalog.Schema > ModelCatalog.SupportedSchema ? null : new CatalogLoad(catalog, CatalogOrigin.Cache, null);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            Log.Warn($"Ignoring the cached catalog: {ex.Message}");
            return null;
        }
    }

    private async Task WriteCacheAsync(byte[] json, string signature, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var path = Path.Combine(cacheDirectory, FileName);
            // Signature first: a reader that sees a new file with an old signature just fails to verify and falls back.
            await File.WriteAllTextAsync(path + ".sig.tmp", signature, ct).ConfigureAwait(false);
            await File.WriteAllBytesAsync(path + ".tmp", json, ct).ConfigureAwait(false);
            File.Move(path + ".sig.tmp", path + ".sig", overwrite: true);
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (IOException ex)
        {
            Log.Warn($"Could not cache the catalog: {ex.Message}");
        }
    }

}
