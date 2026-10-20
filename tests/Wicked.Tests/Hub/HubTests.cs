using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Http;
using Wicked.Core.Hub;

namespace Wicked.Tests;

public sealed class HubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HubClient ClientFor(LocalServer server, Func<string?>? token = null) =>
        new(WickedHttp.CreateClient(offline: () => false), token, server.BaseAddress);

    private const string ModelJson = """
        {"id":"ggml-org/gemma-3-1b-it-GGUF","sha":"f9c28bcd","lastModified":"2025-03-12T10:30:05.000Z","gated":false,
         "downloads":223293,"likes":54,"tags":["gguf","license:gemma"],"cardData":{"license":"gemma"},
         "gguf":{"total":999885952,"architecture":"gemma3","context_length":32768,"chat_template":"x"},
         "siblings":[{"rfilename":"README.md"},{"rfilename":"gemma-3-1b-it-Q4_K_M.gguf"}]}
        """;

    [Fact]
    public async Task ReadsModelInfo()
    {
        await using var server = await LocalServer.StartAsync(app =>
            app.MapGet("/api/models/{owner}/{name}", () => Results.Text(ModelJson, "application/json")));

        var model = await ClientFor(server).GetModelAsync("ggml-org/gemma-3-1b-it-GGUF", Ct);

        Assert.Equal("f9c28bcd", model.Sha);
        Assert.Equal(Gating.None, model.Gated);
        Assert.Equal("gemma", model.License);
        Assert.Equal("gemma3", model.Gguf!.Architecture);
        Assert.Equal(32768, model.Gguf.ContextLength);
        Assert.Equal(999885952, model.Gguf.Total);
        Assert.Equal(new DateTimeOffset(2025, 3, 12, 10, 30, 5, TimeSpan.Zero), model.LastModified);
        Assert.Equal(["README.md", "gemma-3-1b-it-Q4_K_M.gguf"], model.Siblings);
        Assert.Equal(54, model.Likes);
    }

    [Theory]
    [InlineData("\"auto\"", Gating.Auto)]
    [InlineData("\"manual\"", Gating.Manual)]
    [InlineData("false", Gating.None)]
    public async Task ReadsTheGatedField(string json, Gating expected)
    {
        await using var server = await LocalServer.StartAsync(app =>
            app.MapGet("/api/models/{owner}/{name}", () => Results.Text("{\"id\":\"a/b\",\"sha\":\"s\",\"gated\":" + json + "}", "application/json")));

        Assert.Equal(expected, (await ClientFor(server).GetModelAsync("a/b", Ct)).Gated);
    }

    [Fact]
    public async Task FollowsTheLinkCursorAcrossPages()
    {
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/api/models/{owner}/{name}/tree/{rev}", (HttpContext c) =>
        {
            var cursor = c.Request.Query["cursor"].ToString();
            if (cursor == "")
            {
                c.Response.Headers.Link = $"<{c.Request.Scheme}://{c.Request.Host}/api/models/o/n/tree/main?recursive=true&cursor=abc%3D>; rel=\"next\"";
                return Results.Text("""[{"type":"file","oid":"o1","size":10,"path":"a.gguf","lfs":{"oid":"sha-a","size":10}},{"type":"directory","oid":"d","size":0,"path":"sub"}]""", "application/json");
            }

            return Results.Text("""[{"type":"file","oid":"o2","size":5,"path":"sub/README.md"}]""", "application/json");
        }));

        var files = await ClientFor(server).ListFilesAsync("o/n", "main", Ct);

        Assert.Equal(3, files.Count);
        Assert.Equal("sha-a", files[0].LfsSha256);
        Assert.Equal(10, files[0].Size);
        Assert.True(files[1].IsDirectory);
        Assert.Null(files[2].LfsSha256);
        Assert.Equal("README.md", files[2].Name);
    }

    [Fact]
    public async Task SearchSendsTheGgufFilterAndParsesRows()
    {
        string? query = null;
        await using var server = await LocalServer.StartAsync(app => app.MapGet("/api/models", (HttpContext c) =>
        {
            query = c.Request.QueryString.Value;
            return Results.Text("""[{"id":"unsloth/Qwen3.8-27B-GGUF","likes":5224,"downloads":6408192,"tags":["gguf"],"createdAt":"2026-08-13T08:28:40.000Z","gated":"auto"}]""", "application/json");
        }));

        var rows = await ClientFor(server).SearchAsync("qwen3", limit: 5, ct: Ct);

        Assert.Contains("filter=gguf", query, StringComparison.Ordinal);
        Assert.Contains("search=qwen3", query, StringComparison.Ordinal);
        Assert.Contains("limit=5", query, StringComparison.Ordinal);
        Assert.Contains("direction=-1", query, StringComparison.Ordinal);
        var row = Assert.Single(rows);
        Assert.Equal("unsloth/Qwen3.8-27B-GGUF", row.Id);
        Assert.Equal(6408192, row.Downloads);
        Assert.Equal(Gating.Auto, row.Gated);
    }

    [Theory]
    [InlineData(401, typeof(AuthRequiredException))]
    [InlineData(403, typeof(GatedRepoException))]
    [InlineData(404, typeof(RepoNotFoundException))]
    public async Task MapsErrorStatusesToTypedExceptions(int status, Type expected)
    {
        await using var server = await LocalServer.StartAsync(app =>
            app.MapGet("/api/models/{owner}/{name}", (HttpContext c) => Results.StatusCode(status)));

        var ex = await Assert.ThrowsAnyAsync<HubException>(() => ClientFor(server).GetModelAsync("o/n", Ct));
        Assert.IsType(expected, ex);
    }

    [Fact]
    public async Task RateLimitCarriesRetryAfter()
    {
        await using var server = await LocalServer.StartAsync(app =>
            app.MapGet("/api/models/{owner}/{name}", (HttpContext c) =>
            {
                c.Response.Headers.RetryAfter = "37";
                return Results.StatusCode((int)HttpStatusCode.TooManyRequests);
            }));

        var ex = await Assert.ThrowsAsync<RateLimitedException>(() => ClientFor(server).GetModelAsync("o/n", Ct));
        Assert.Equal(TimeSpan.FromSeconds(37), ex.RetryAfter);
    }

    [Fact]
    public async Task SendsTheTokenAsBearer()
    {
        string? auth = null;
        await using var server = await LocalServer.StartAsync(app =>
            app.MapGet("/api/models/{owner}/{name}", (HttpContext c) =>
            {
                auth = c.Request.Headers.Authorization.ToString();
                return Results.Text(ModelJson, "application/json");
            }));

        await ClientFor(server, () => "hf_abc").GetModelAsync("o/n", Ct);

        Assert.Equal("Bearer hf_abc", auth);
    }

    [Fact]
    public void ResolveUrlEscapesEachSegment()
    {
        var hub = new HubClient(new HttpClient());

        Assert.Equal(
            "https://huggingface.co/unsloth/Qwen3-GGUF/resolve/main/UD-Q4/a%20b.gguf",
            hub.ResolveUrl("unsloth/Qwen3-GGUF", "main", "UD-Q4/a b.gguf").AbsoluteUri);
        Assert.Contains("refs%2Fpr%2F1", hub.ResolveUrl("o/n", "refs/pr/1", "x.gguf").AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gemma-3-1b-it-Q4_K_M.gguf", "Q4_K_M", false, null, null, false)]
    [InlineData("Qwen3-30B-A3B-IQ4_XS.gguf", "IQ4_XS", false, null, null, false)]
    [InlineData("model-Q8_0.gguf", "Q8_0", false, null, null, false)]
    [InlineData("model-BF16.gguf", "BF16", false, null, null, false)]
    [InlineData("Qwen3-Coder-30B-A3B-Instruct-UD-Q4_K_XL.gguf", "Q4_K_XL", true, null, null, false)]
    [InlineData("llama-q5_k_m.gguf", "Q5_K_M", false, null, null, false)]
    [InlineData("Kimi-K2-UD-IQ1_S-00002-of-00006.gguf", "IQ1_S", true, 2, 6, false)]
    [InlineData("Q6_K/model-00001-of-00003.gguf", "Q6_K", false, 1, 3, false)]
    [InlineData("mmproj-F16.gguf", "F16", false, null, null, true)]
    [InlineData("gpt-oss-20b-MXFP4.gguf", "MXFP4", false, null, null, false)]
    [InlineData("Qwen3-8B.gguf", null, false, null, null, false)]
    public void ParsesQuantNames(string path, string? quant, bool dynamic, int? part, int? count, bool mmproj)
    {
        var name = GgufNames.Parse(path);

        Assert.Equal(quant, name.Quant);
        Assert.Equal(dynamic, name.Dynamic);
        Assert.Equal(part, name.Part);
        Assert.Equal(count, name.PartCount);
        Assert.Equal(mmproj, name.IsMmproj);
    }

    [Fact]
    public void GroupsShardsAndSeparatesProjectors()
    {
        HubFile F(string p, long size) => new(p, size, "o", "s", false);
        var files = new[]
        {
            F("m-Q8_0.gguf", 800),
            F("m-Q4_K_M.gguf", 400),
            F("Q6_K/m-00001-of-00002.gguf", 300),
            F("Q6_K/m-00002-of-00002.gguf", 300),
            F("mmproj-F16.gguf", 50),
            F("README.md", 1),
        };

        var variants = GgufNames.Variants(files);

        Assert.Equal(["Q4_K_M", "Q6_K", "Q8_0"], variants.Select(v => v.Quant));
        Assert.Equal(600, variants[1].TotalSize);
        Assert.True(variants[1].IsSharded);
        Assert.Equal("mmproj-F16.gguf", Assert.Single(GgufNames.Projectors(files)).Path);
    }
}
