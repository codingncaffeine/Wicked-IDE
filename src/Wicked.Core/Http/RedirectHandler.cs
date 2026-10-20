using System.Net;

namespace Wicked.Core.Http;

/// <summary>
/// Follows redirects itself (the socket layer has auto-redirect off) for one reason: Hugging Face answers a download
/// with a 302 to a CDN host, and a bearer token must never be sent there. The <c>Authorization</c> header is dropped
/// whenever the host or port changes, and a redirect from https to http is not followed at all. Headers the first hop
/// carries that the CDN does not (<c>x-linked-etag</c>, <c>x-linked-size</c>, <c>x-repo-commit</c>) are copied onto the
/// final response, since they are the file's sha256, size and revision.
/// </summary>
internal sealed class RedirectHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const int MaxHops = 10;

    private static readonly string[] CarriedHeaders = ["x-linked-etag", "x-linked-size", "x-repo-commit"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var carried = new List<(string Name, string[] Values)>();
        var current = request;

        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (!IsRedirect(response.StatusCode) || response.Headers.Location is not { } location || current.RequestUri is not { } from)
            {
                break;
            }

            var target = location.IsAbsoluteUri ? location : new Uri(from, location);
            if (from.Scheme == Uri.UriSchemeHttps && target.Scheme != Uri.UriSchemeHttps) break;
            if (current.Content is not null && response.StatusCode != HttpStatusCode.SeeOther) break;

            foreach (var name in CarriedHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values)) carried.Add((name, values.ToArray()));
            }

            var next = Clone(current, target, response.StatusCode);
            response.Dispose();
            current = next;
            response = await base.SendAsync(next, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (name, values) in carried)
        {
            if (!response.Headers.Contains(name)) response.Headers.TryAddWithoutValidation(name, values);
        }

        return response;
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static HttpRequestMessage Clone(HttpRequestMessage source, Uri target, HttpStatusCode status)
    {
        var method = status == HttpStatusCode.SeeOther && source.Method != HttpMethod.Head ? HttpMethod.Get : source.Method;
        var clone = new HttpRequestMessage(method, target) { Version = source.Version, VersionPolicy = source.VersionPolicy };
        var sameOrigin = source.RequestUri is { } from && from.Host.Equals(target.Host, StringComparison.OrdinalIgnoreCase) && from.Port == target.Port;
        foreach (var header in source.Headers)
        {
            if (!sameOrigin && header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;
            if (!sameOrigin && header.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) continue;
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var option in source.Options) clone.Options.TryAdd(option.Key, option.Value);
        return clone;
    }
}
