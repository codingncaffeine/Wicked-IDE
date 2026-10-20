using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Http;

namespace Wicked.Tests;

public sealed class WickedHttpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    [Fact]
    public async Task OfflineModeRefusesTheInternetButNotLoopback()
    {
        var fake = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = WickedHttp.CreateClient(fake, offline: () => true);

        var ex = await Assert.ThrowsAsync<OfflineException>(() => client.GetAsync("https://huggingface.co/api/models", Ct));
        Assert.Equal("huggingface.co", ex.Uri.Host);
        Assert.Empty(fake.Seen);

        using var ok = await client.GetAsync("http://127.0.0.1:9931/health", Ct);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var ok6 = await client.GetAsync("http://[::1]:9931/health", Ct);
        Assert.Equal(HttpStatusCode.OK, ok6.StatusCode);
    }

    [Fact]
    public async Task SendsOurUserAgent()
    {
        var fake = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = WickedHttp.CreateClient(fake, offline: () => false);

        using var _ = await client.GetAsync("https://example.test/", Ct);

        Assert.StartsWith("Wicked/", fake.Seen[0].Headers.UserAgent.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuthorizationDoesNotFollowARedirectToAnotherHost()
    {
        string? atCdn = "unset";
        string? atOrigin = null;
        await using var cdn = await LocalServer.StartAsync(app => app.MapGet("/file", (HttpContext c) =>
        {
            atCdn = c.Request.Headers.Authorization.ToString();
            return Results.Text("payload");
        }), host: "127.0.0.1");
        await using var origin = await LocalServer.StartAsync(app => app.MapGet("/resolve", (HttpContext c) =>
        {
            atOrigin = c.Request.Headers.Authorization.ToString();
            c.Response.Headers["x-linked-etag"] = "\"abc123\"";
            c.Response.Headers["x-repo-commit"] = "deadbeef";
            return Results.Redirect(cdn.Url("/file").ToString());
        }));
        using var client = WickedHttp.CreateClient(offline: () => false);
        using var request = new HttpRequestMessage(HttpMethod.Get, origin.Url("/resolve"));
        request.Headers.Authorization = new("Bearer", "hf_secret");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal("payload", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Bearer hf_secret", atOrigin);
        Assert.Equal("", atCdn);
        Assert.Equal("\"abc123\"", response.Headers.GetValues("x-linked-etag").Single());
        Assert.Equal("deadbeef", response.Headers.GetValues("x-repo-commit").Single());
    }

    [Fact]
    public async Task AuthorizationSurvivesARedirectWithinTheSameHost()
    {
        string? seen = null;
        await using var server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/a", () => Results.Redirect("/b"));
            app.MapGet("/b", (HttpContext c) =>
            {
                seen = c.Request.Headers.Authorization.ToString();
                return Results.Text("ok");
            });
        });
        using var client = WickedHttp.CreateClient(offline: () => false);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url("/a"));
        request.Headers.Authorization = new("Bearer", "t");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal("Bearer t", seen);
    }

    [Fact]
    public async Task RangeHeadersFollowRedirects()
    {
        string? range = null;
        await using var server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/a", () => Results.Redirect("/b"));
            app.MapGet("/b", (HttpContext c) =>
            {
                range = c.Request.Headers.Range.ToString();
                return Results.Text("ok");
            });
        });
        using var client = WickedHttp.CreateClient(offline: () => false);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url("/a"));
        request.Headers.Range = new(100, null);

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal("bytes=100-", range);
    }

    [Fact]
    public async Task ARedirectLoopStopsInsteadOfSpinning()
    {
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/loop", () => Results.Redirect("/loop")));
        using var client = WickedHttp.CreateClient(offline: () => false);

        using var response = await client.GetAsync(server.Url("/loop"), Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    [Fact]
    public async Task DecompressesGzipBodies()
    {
        await using var server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/big", async (HttpContext c) =>
            {
                c.Response.Headers.ContentEncoding = "gzip";
                await using var gzip = new System.IO.Compression.GZipStream(c.Response.Body, System.IO.Compression.CompressionMode.Compress);
                await gzip.WriteAsync(new byte[5000].Select(_ => (byte)120).ToArray());
            });
        });
        using var client = WickedHttp.CreateClient(offline: () => false);

        Assert.Equal(5000, (await client.GetStringAsync(server.Url("/big"), Ct)).Length);
    }
}
