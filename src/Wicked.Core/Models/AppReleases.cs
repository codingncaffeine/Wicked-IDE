using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Wicked.Core.Models;

/// <summary>A published release of Wicked: <c>Version</c> without the leading v, where its notes are, and when it came out.</summary>
public sealed record AppRelease(string Version, string Url, string? Title, DateTimeOffset? Published);

/// <summary>
/// Asks GitHub Releases whether a newer Wicked than this build exists (D15). Banner only: on Linux the package manager updates
/// the app. The ETag of the last answer is sent back so an unchanged answer costs no rate limit.
/// </summary>
public sealed class AppReleases(HttpClient http, string? cacheFile = null, Uri? apiBase = null, string repository = "codingncaffeine/Wicked-IDE")
{
    private readonly Uri _api = apiBase ?? new Uri("https://api.github.com/");
    private (string? ETag, AppRelease? Release)? _remembered;

    /// <summary>The latest release, or null when the repository has none. Throws the HTTP and rate-limit failures for the caller to back off on.</summary>
    public async Task<AppRelease?> LatestAsync(CancellationToken ct = default)
    {
        var known = _remembered ??= await ReadCacheAsync().ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, $"repos/{repository}/releases/latest"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (known?.ETag is { } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified && known is { } same) return same.Release;
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            var after = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(10);
            throw new Hub.RateLimitedException(after);
        }

        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        AppRelease? release = tag is null ? null : new AppRelease(
            tag.TrimStart('v', 'V'),
            root.TryGetProperty("html_url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString()! : $"https://github.com/{repository}/releases",
            root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
            root.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(p.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : null);
        _remembered = (response.Headers.ETag?.ToString(), release);
        await WriteCacheAsync().ConfigureAwait(false);
        return release;
    }

    /// <summary>Whether <paramref name="release"/> is newer than the running build (a version with a <c>+revision</c> suffix is compared without it).</summary>
    public static bool IsNewer(AppRelease release, string running)
    {
        var plus = running.IndexOf('+', StringComparison.Ordinal);
        return TryParse(release.Version, out var theirs) && TryParse(plus > 0 ? running[..plus] : running, out var mine) && theirs > mine;
    }

    private static bool TryParse(string text, out Version version)
    {
        var dash = text.IndexOf('-', StringComparison.Ordinal);
        var core = dash > 0 ? text[..dash] : text;
        return Version.TryParse(core, out version!);
    }

    private sealed record Cached(string? ETag, AppRelease? Release);

    private async Task<(string?, AppRelease?)?> ReadCacheAsync()
    {
        try
        {
            if (cacheFile is null || !File.Exists(cacheFile)) return null;
            var cached = JsonSerializer.Deserialize<Cached>(await File.ReadAllTextAsync(cacheFile).ConfigureAwait(false));
            return cached is null ? null : (cached.ETag, cached.Release);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteCacheAsync()
    {
        if (cacheFile is null || _remembered is not { } r) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            var temp = cacheFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new Cached(r.ETag, r.Release))).ConfigureAwait(false);
            File.Move(temp, cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not write the release cache: {ex.Message}");
        }
    }
}
