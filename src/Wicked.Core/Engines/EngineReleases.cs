using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Wicked.Core.Engines;

/// <summary>GitHub said the unauthenticated limit (60 requests an hour) is used up; <see cref="ResetsAt"/> says when it renews.</summary>
public sealed class GitHubRateLimitException(DateTimeOffset resetsAt)
    : IOException($"GitHub rate limit reached; resets at {resetsAt:HH:mm:ss}")
{
    public DateTimeOffset ResetsAt { get; } = resetsAt;
}

/// <summary>
/// Lists llama.cpp releases from the GitHub API. Conditional requests: the ETag of the last answer goes back as
/// <c>If-None-Match</c> and a 304 returns the stored list without using the rate limit. The list is remembered in memory
/// and, when a cache file is given, across runs. Never uses <c>/releases/latest</c>: it points at a <c>v0.x</c> tag that has no assets.
/// </summary>
public sealed class EngineReleases(HttpClient http, Func<string?>? token = null, string? cacheFile = null, Uri? apiBase = null)
{
    public static readonly Uri DefaultApi = new("https://api.github.com/");

    private static readonly JsonSerializerOptions CacheJson = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    private readonly Uri _api = apiBase ?? DefaultApi;
    private readonly Dictionary<int, Cached> _memory = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The newest <paramref name="count"/> releases, newest first, drafts excluded.</summary>
    public async Task<IReadOnlyList<EngineRelease>> ListAsync(int count = 10, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var cached = await LoadCacheAsync(count, ct).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, $"repos/ggml-org/llama.cpp/releases?per_page={count.ToString(CultureInfo.InvariantCulture)}"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            var t = token?.Invoke() is { Length: > 0 } explicitToken ? explicitToken : Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (t is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);
            if (cached?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null) return cached.Releases;
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
                && response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0")
            {
                var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var r)
                            && long.TryParse(r.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch)
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                    : DateTimeOffset.UtcNow.AddMinutes(10);
                throw new GitHubRateLimitException(reset);
            }

            response.EnsureSuccessStatusCode();
            var releases = Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var fresh = new Cached(response.Headers.ETag?.ToString(), releases);
            _memory[count] = fresh;
            await SaveCacheAsync(count, fresh).ConfigureAwait(false);
            return releases;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The newest release that has every archive <paramref name="choice"/> needs, or null.</summary>
    public static EngineRelease? Newest(IEnumerable<EngineRelease> releases, EngineChoice choice) =>
        releases.Where(choice.IsAvailableIn).OrderByDescending(r => r.Build).FirstOrDefault();

    public static IReadOnlyList<EngineRelease> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<EngineRelease>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
            var tag = e.GetProperty("tag_name").GetString() ?? "";
            if (EngineRelease.ParseBuild(tag) is not { } build) continue;
            var assets = new List<EngineAsset>();
            if (e.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in a.EnumerateArray())
                {
                    var digest = asset.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                    assets.Add(new EngineAsset(
                        asset.GetProperty("name").GetString() ?? "",
                        asset.GetProperty("size").GetInt64(),
                        new Uri(asset.GetProperty("browser_download_url").GetString()!),
                        digest is { } x && x.StartsWith("sha256:", StringComparison.Ordinal) ? x[7..].ToLowerInvariant() : null));
                }
            }

            var published = e.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                ? DateTimeOffset.Parse(p.GetString()!, CultureInfo.InvariantCulture)
                : default;
            result.Add(new EngineRelease(tag, build, published, e.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True, assets));
        }

        return result;
    }

    private string? CachePath(int count) =>
        cacheFile is null ? null : Path.ChangeExtension(cacheFile, $".{count.ToString(CultureInfo.InvariantCulture)}.json");

    private async Task<Cached?> LoadCacheAsync(int count, CancellationToken ct)
    {
        if (_memory.TryGetValue(count, out var hit)) return hit;
        if (CachePath(count) is not { } path || !File.Exists(path)) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            var loaded = JsonSerializer.Deserialize<Cached>(text, CacheJson);
            if (loaded is not null) _memory[count] = loaded;
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Warn($"Ignoring the engine release cache: {ex.Message}");
            return null;
        }
    }

    private async Task SaveCacheAsync(int count, Cached cached)
    {
        if (CachePath(count) is not { } path) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(cached, CacheJson)).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not write the engine release cache: {ex.Message}");
        }
    }

    private sealed record Cached(string? ETag, IReadOnlyList<EngineRelease> Releases);
}
