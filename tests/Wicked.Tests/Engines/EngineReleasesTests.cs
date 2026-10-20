using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Engines;
using Wicked.Core.Http;

namespace Wicked.Tests;

public sealed class EngineReleasesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Sample = """
        [
          {"tag_name":"b11759","draft":false,"prerelease":true,"published_at":"2026-10-20T21:26:07Z",
           "assets":[
             {"name":"llama-b11759-bin-ubuntu-x64.tar.gz","size":22236214,"digest":"sha256:789C7AF4276A08171B862F806F42138F71E3B805C7378EBCEEBE93F29FEF132","browser_download_url":"https://github.com/ggml-org/llama.cpp/releases/download/b11759/llama-b11759-bin-ubuntu-x64.tar.gz"},
             {"name":"llama-b11759-xcframework.zip","size":66859935,"digest":null,"browser_download_url":"https://github.com/x/y.zip"}]},
          {"tag_name":"v0.7.0","draft":false,"prerelease":false,"published_at":"2026-10-01T00:00:00Z","assets":[]},
          {"tag_name":"b11700","draft":true,"prerelease":true,"published_at":"2026-10-19T00:00:00Z","assets":[]}
        ]
        """;

    [Fact]
    public void ParsesTagsAssetsAndDigests()
    {
        var releases = EngineReleases.Parse(Sample);

        var release = Assert.Single(releases);
        Assert.Equal("b11759", release.Tag);
        Assert.Equal(11759, release.Build);
        Assert.True(release.Prerelease);
        Assert.Equal(2, release.Assets.Count);
        Assert.Equal("789c7af4276a08171b862f806f42138f71e3b805c7378ebceebe93f29fef132", release.Assets[0].Sha256);
        Assert.Null(release.Assets[1].Sha256);
        Assert.Equal(22236214, release.Assets[0].Size);
        Assert.Equal(11759, EngineRelease.ParseBuild("b11759"));
        Assert.Null(EngineRelease.ParseBuild("v0.7.0"));
    }

    [Fact]
    public async Task SendsConditionalRequestsAndReusesTheStoredAnswerOn304()
    {
        var seen = new List<(string? IfNoneMatch, string? Auth, string? Accept)>();
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/repos/ggml-org/llama.cpp/releases", (HttpContext c) =>
        {
            var inm = c.Request.Headers.IfNoneMatch.ToString();
            seen.Add((inm.Length > 0 ? inm : null, c.Request.Headers.Authorization.ToString(), c.Request.Headers.Accept.ToString()));
            if (inm == "\"abc\"") return Results.StatusCode(304);
            c.Response.Headers.ETag = "\"abc\"";
            return Results.Text(Sample, "application/json");
        }));
        using var cacheDir = new TempProfile();
        var cache = Path.Combine(cacheDir.Root, "releases.json");
        using var http = WickedHttp.CreateClient(offline: () => false);
        var client = new EngineReleases(http, () => "ghp_test", cache, server.BaseAddress);

        var first = await client.ListAsync(5, Ct);
        var second = await client.ListAsync(5, Ct);
        var fresh = await new EngineReleases(http, () => null, cache, server.BaseAddress).ListAsync(5, Ct);

        Assert.Equal(first.Select(r => r.Tag), second.Select(r => r.Tag));
        Assert.Equal(first.Select(r => r.Tag), fresh.Select(r => r.Tag));
        Assert.Equal(3, seen.Count);
        Assert.Null(seen[0].IfNoneMatch);
        Assert.Equal("\"abc\"", seen[1].IfNoneMatch);
        Assert.Equal("\"abc\"", seen[2].IfNoneMatch);
        Assert.Equal("Bearer ghp_test", seen[0].Auth);
        Assert.Contains("github+json", seen[0].Accept, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsAnExhaustedRateLimitWithItsResetTime()
    {
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/repos/ggml-org/llama.cpp/releases", (HttpContext c) =>
        {
            c.Response.Headers["x-ratelimit-remaining"] = "0";
            c.Response.Headers["x-ratelimit-reset"] = "1792533310";
            return Results.StatusCode(403);
        }));
        using var http = WickedHttp.CreateClient(offline: () => false);

        var ex = await Assert.ThrowsAsync<GitHubRateLimitException>(() => new EngineReleases(http, null, null, server.BaseAddress).ListAsync(3, Ct));

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792533310), ex.ResetsAt);
    }
}
