using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Core.Catalog;
using Wicked.Core.Http;
using Wicked.Core.Models;

namespace Wicked.Tests.Models;

/// <summary>
/// The whole install path against local stand-ins for GitHub and Hugging Face: on a profile with no engine, installing
/// a model fetches the engine for this machine first (the CPU build, the only one the stand-in offers), then the model's
/// files, verified, and the store and the state show both.
/// </summary>
public sealed class ModelServicesTests
{
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static byte[] EngineArchive(string tag)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"llama-{tag}/") { Mode = (UnixFileMode)0b111_101_101 });
            var script = "#!/bin/sh\ncase \"$1\" in --version) echo \"version: 0.7.0-dev (build 999, commit abc1234)\" >&2 ;; esac\n";
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"llama-{tag}/llama-server")
            {
                Mode = (UnixFileMode)0b111_101_101,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(script)),
            });
        }

        return buffer.ToArray();
    }

    [Fact]
    public async Task InstallingAPickFetchesTheEngineThenTheModelVerified()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the stand-in engine is a shell script");
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TempProfile();
        var engine = EngineArchive("b999");
        var weights = Encoding.UTF8.GetBytes(new string('w', 200_000));
        var platform = OperatingSystem.IsMacOS() ? "macos-arm64" : "ubuntu-x64";

        await using var server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/engine.tar.gz", () => Results.Bytes(engine));
            app.MapGet("/repos/ggml-org/llama.cpp/releases", (HttpContext http) => Results.Text(JsonSerializer.Serialize(new[]
            {
                new
                {
                    tag_name = "b999",
                    published_at = DateTimeOffset.UtcNow,
                    prerelease = true,
                    assets = new[]
                    {
                        new { name = $"llama-b999-bin-{platform}.tar.gz", size = engine.Length, browser_download_url = $"{http.Request.Scheme}://{http.Request.Host}/engine.tar.gz", digest = "sha256:" + Sha(engine) },
                    },
                },
            }), "application/json"));
            app.MapGet("/test/tiny-GGUF/resolve/main/tiny-Q4_K_M.gguf", () => Results.Bytes(weights, enableRangeProcessing: true));
        });

        var http = WickedHttp.CreateClient(offline: () => false);
        await using var services = new ModelServices(profile.Paths, http, hubBase: server.Url("/"), githubBase: server.Url("/"));
        var progress = new List<InstallProgress>();
        services.Progress += p =>
        {
            lock (progress) progress.Add(p);
        };
        await services.InitializeAsync(allowNetwork: false, ct);
        Assert.NotNull(services.State.Catalog);
        Assert.Null(services.State.Engine);

        // A real catalog model, pointed at the stand-in's file.
        var real = services.State.Catalog!.Catalog.Models.First();
        var model = real with { Repo = "test/tiny-GGUF", Revision = "main" };
        var tier = new QuantTier("Q4_K_M", [new CatalogFile("tiny-Q4_K_M.gguf", weights.Length, Sha(weights))], 90);
        var pick = new Pick(PickKind.Everyday, model, tier, null!, 8192, "test", []);

        await services.InstallAsync(pick, ct);

        Assert.Equal("b999-cpu", services.State.Engine?.Id);
        Assert.True(services.State.IsInstalled(model, tier));
        Assert.Equal(weights, await File.ReadAllBytesAsync(services.Store.PathFor("test/tiny-GGUF", "tiny-Q4_K_M.gguf"), ct));
        Assert.Empty(services.State.Busy);
        lock (progress)
        {
            Assert.Contains(progress, p => p.ModelId == "engine");
            Assert.Contains(progress, p => p.ModelId == "test/tiny-GGUF" && p.Fraction == 1);
        }
    }
}
