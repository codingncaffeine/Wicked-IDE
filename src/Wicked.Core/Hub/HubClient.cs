using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wicked.Core.Hub;

/// <summary>
/// The Hugging Face Hub's read API, as far as Wicked needs it: model info (the revision sha and GGUF header summary that
/// drive update checks), the file tree with sha256s, search, and download URLs. The token comes from the caller (settings)
/// or <c>HF_TOKEN</c>; it is sent only to the Hub host (the shared client drops it on a redirect to the CDN).
/// </summary>
public sealed partial class HubClient(HttpClient http, Func<string?>? token = null, Uri? baseAddress = null)
{
    public static readonly Uri DefaultBase = new("https://huggingface.co/");

    private readonly Uri _base = baseAddress ?? DefaultBase;
    private readonly Func<string?> _token = token ?? (() => null);

    [GeneratedRegex("<(?<url>[^>]+)>\\s*;\\s*rel=\"next\"", RegexOptions.CultureInvariant)]
    private static partial Regex NextLink();

    /// <summary>The explicit token, else <c>HF_TOKEN</c>, else <c>HUGGING_FACE_HUB_TOKEN</c>; null for anonymous.</summary>
    public string? Token => _token() is { Length: > 0 } t ? t
        : Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 } e ? e
        : Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN") is { Length: > 0 } h ? h : null;

    /// <summary>The URL a file downloads from; the Hub answers it with a redirect to its CDN.</summary>
    public Uri ResolveUrl(string repo, string revision, string file) =>
        new(_base, $"{repo}/resolve/{Uri.EscapeDataString(revision)}/{EscapePath(file)}");

    public async Task<HubModel> GetModelAsync(string repo, CancellationToken ct = default) =>
        (await GetModelAsync(repo, null, ct).ConfigureAwait(false)).Model ?? throw new InvalidOperationException("The Hub answered 304 to a request that carried no ETag");

    /// <summary>
    /// As above, sending <paramref name="etag"/> as <c>If-None-Match</c> when there is one: a repo that has not changed answers 304 and the model is null.
    /// The ETag to send next time comes back with the answer (null when the Hub gave none).
    /// </summary>
    public async Task<(HubModel? Model, string? ETag)> GetModelAsync(string repo, string? etag, CancellationToken ct = default)
    {
        var (doc, _, newTag) = await GetJsonCoreAsync(new Uri(_base, $"api/models/{repo}"), repo, etag, ct).ConfigureAwait(false);
        if (doc is null) return (null, etag);
        using var _ = doc;
        var root = doc.RootElement;
        HubGguf? gguf = null;
        if (root.TryGetProperty("gguf", out var g) && g.ValueKind == JsonValueKind.Object)
        {
            gguf = new HubGguf(Str(g, "architecture"), Long(g, "context_length"), Long(g, "total"));
        }

        var tags = StringArray(root, "tags");
        var license = root.TryGetProperty("cardData", out var card) && card.ValueKind == JsonValueKind.Object ? Str(card, "license") : null;
        license ??= tags.FirstOrDefault(t => t.StartsWith("license:", StringComparison.Ordinal))?[8..];
        var siblings = root.TryGetProperty("siblings", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(e => Str(e, "rfilename")).OfType<string>().ToList()
            : [];
        return (new HubModel(
            Str(root, "id") ?? repo,
            Str(root, "sha") ?? "",
            Date(root, "lastModified"),
            ParseGated(root),
            license,
            Long(root, "downloads") ?? 0,
            Long(root, "likes") ?? 0,
            tags,
            siblings,
            gguf), newTag);
    }

    /// <summary>The newest commits of a revision (the titles that say what an update changed), newest first.</summary>
    public async Task<IReadOnlyList<HubCommit>> GetCommitsAsync(string repo, string revision = "main", int limit = 10, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync(new Uri(_base, $"api/models/{repo}/commits/{Uri.EscapeDataString(revision)}"), repo, ct).ConfigureAwait(false);
        return [.. doc.RootElement.EnumerateArray().Take(limit).Select(e => new HubCommit(Str(e, "id") ?? "", Str(e, "title") ?? "", Date(e, "date")))];
    }

    /// <summary>Every file of a revision, folders flattened (<c>recursive=true</c>), following the <c>Link: rel="next"</c> cursor until the Hub stops sending one.</summary>
    public async Task<IReadOnlyList<HubFile>> ListFilesAsync(string repo, string revision = "main", CancellationToken ct = default)
    {
        var files = new List<HubFile>();
        Uri? next = new(_base, $"api/models/{repo}/tree/{Uri.EscapeDataString(revision)}?recursive=true&expand=false");
        for (var page = 0; next is not null && page < 200; page++)
        {
            var (doc, link) = await GetJsonWithLinkAsync(next, repo, ct).ConfigureAwait(false);
            using (doc)
            {
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var lfs = e.TryGetProperty("lfs", out var l) && l.ValueKind == JsonValueKind.Object ? l : (JsonElement?)null;
                    files.Add(new HubFile(
                        Str(e, "path") ?? "",
                        lfs is { } lf ? Long(lf, "size") ?? Long(e, "size") ?? 0 : Long(e, "size") ?? 0,
                        Str(e, "oid") ?? "",
                        lfs is { } lf2 ? Str(lf2, "oid") : null,
                        Str(e, "type") == "directory"));
                }
            }

            next = link;
        }

        return files;
    }

    public async Task<IReadOnlyList<HubSearchResult>> SearchAsync(string query, string filter = "gguf", string sort = "downloads", int limit = 20, CancellationToken ct = default)
    {
        var url = new StringBuilder($"api/models?search={Uri.EscapeDataString(query)}&sort={Uri.EscapeDataString(sort)}&direction=-1&limit={limit.ToString(CultureInfo.InvariantCulture)}");
        if (filter.Length > 0) url.Append("&filter=").Append(Uri.EscapeDataString(filter));
        foreach (var field in new[] { "downloads", "likes", "tags", "createdAt", "lastModified", "gated" }) url.Append("&expand[]=").Append(field);

        using var doc = await GetJsonAsync(new Uri(_base, url.ToString()), "search", ct).ConfigureAwait(false);
        return [.. doc.RootElement.EnumerateArray().Select(e => new HubSearchResult(
            Str(e, "id") ?? "",
            Long(e, "downloads") ?? 0,
            Long(e, "likes") ?? 0,
            Date(e, "createdAt"),
            Date(e, "lastModified"),
            ParseGated(e),
            StringArray(e, "tags")))];
    }

    private async Task<JsonDocument> GetJsonAsync(Uri url, string what, CancellationToken ct)
    {
        var (doc, _) = await GetJsonWithLinkAsync(url, what, ct).ConfigureAwait(false);
        return doc;
    }

    private async Task<(JsonDocument Doc, Uri? Next)> GetJsonWithLinkAsync(Uri url, string what, CancellationToken ct)
    {
        var (doc, next, _) = await GetJsonCoreAsync(url, what, null, ct).ConfigureAwait(false);
        return (doc!, next);
    }

    private async Task<(JsonDocument? Doc, Uri? Next, string? ETag)> GetJsonCoreAsync(Uri url, string what, string? etag, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (etag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        if (Token is { } t && url.Host == _base.Host) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        switch (response.StatusCode)
        {
            case HttpStatusCode.Unauthorized: throw new AuthRequiredException(what);
            case HttpStatusCode.Forbidden: throw new GatedRepoException(what);
            case HttpStatusCode.NotModified: return (null, null, etag);
            case HttpStatusCode.NotFound: throw new RepoNotFoundException(what);
            case HttpStatusCode.TooManyRequests:
                var after = response.Headers.RetryAfter?.Delta
                            ?? (response.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : (TimeSpan?)null)
                            ?? TimeSpan.FromSeconds(60);
                throw new RateLimitedException(after < TimeSpan.Zero ? TimeSpan.Zero : after);
        }

        response.EnsureSuccessStatusCode();
        Uri? next = null;
        if (response.Headers.TryGetValues("Link", out var links))
        {
            foreach (var link in links)
            {
                var m = NextLink().Match(link);
                if (m.Success && Uri.TryCreate(url, m.Groups["url"].Value, out var n)) next = n;
            }
        }

        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return (await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false), next, response.Headers.ETag?.ToString());
        }
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString)); // not a path: a Hub URL path

    private static Gating ParseGated(JsonElement e) =>
        e.TryGetProperty("gated", out var g) && g.ValueKind == JsonValueKind.String
            ? g.GetString() == "manual" ? Gating.Manual : Gating.Auto
            : Gating.None;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        Str(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static List<string> StringArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array
            ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
            : [];
}
