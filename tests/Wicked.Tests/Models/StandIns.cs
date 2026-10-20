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

/// <summary>Local stand-ins for GitHub (llama.cpp releases) and Hugging Face, so the model half is exercised end to end without the network.</summary>
internal static class StandIns
{
    public static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    public static bool CanRunEngine => !OperatingSystem.IsWindows();

    /// <summary>A tar.gz holding a <c>llama-server</c> shell script that answers <c>--version</c> and <c>--list-devices</c>.</summary>
    public static byte[] EngineArchive(string tag, int build)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false))
        {
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, $"llama-{tag}/") { Mode = (UnixFileMode)0b111_101_101 });
            var script = $"#!/bin/sh\ncase \"$1\" in\n  --version) echo \"version: 0.7.0-dev (build {build}, commit abc1234)\" >&2 ;;\n"
                         + "  --list-devices) echo \"Available devices:\"; echo \"  Vulkan0: Test GPU (8192 MiB, 8000 MiB free)\" ;;\nesac\n";
            tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"llama-{tag}/llama-server")
            {
                Mode = (UnixFileMode)0b111_101_101,
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(script)),
            });
        }

        return buffer.ToArray();
    }

    /// <summary>Serves releases for the given tags; each release has a CPU and a Vulkan build (the same archive).</summary>
    public static void MapGitHub(WebApplication app, params int[] builds) => MapGitHub(app, builds, () => builds);

    /// <summary>As above, but which of the builds the listing shows can change while the test runs.</summary>
    public static void MapGitHub(WebApplication app, int[] builds, Func<int[]> visible)
    {
        var archives = builds.ToDictionary(b => b, b => EngineArchive($"b{b}", b));
        foreach (var (build, bytes) in archives) app.MapGet($"/engine-{build}.tar.gz", () => Results.Bytes(bytes));
        app.MapGet("/repos/ggml-org/llama.cpp/releases", (HttpContext http) =>
        {
            var host = $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Text(JsonSerializer.Serialize(visible().OrderDescending().Select(b => new
            {
                tag_name = $"b{b}",
                published_at = DateTimeOffset.UtcNow,
                prerelease = true,
                assets = new[] { "ubuntu-x64", "ubuntu-vulkan-x64" }.Select(suffix => new
                {
                    name = $"llama-b{b}-bin-{suffix}.tar.gz",
                    size = archives[b].Length,
                    browser_download_url = $"{host}/engine-{b}.tar.gz",
                    digest = "sha256:" + Sha(archives[b]),
                }).ToArray(),
            })), "application/json");
        });
    }

    /// <summary>A catalog model re-pointed at a stand-in repo, with one tier per file.</summary>
    public static (CatalogModel Model, QuantTier Tier) CatalogModelFor(ModelServices services, string repo, string revision, params (string Name, byte[] Bytes)[] files)
    {
        var real = services.State.Catalog!.Catalog.Models.First();
        var model = real with { Repo = repo, Revision = revision, Name = "Test Model" };
        var tier = new QuantTier("Q4_K_M", [.. files.Select(f => new CatalogFile(f.Name, f.Bytes.Length, Sha(f.Bytes)))], 90);
        return (model, tier);
    }

    public static ModelServices Services(TempProfile profile, LocalServer server) =>
        new(profile.Paths, WickedHttp.CreateClient(offline: () => false), hubBase: server.Url("/"), githubBase: server.Url("/"));

    /// <summary>Bytes that look like weights: patterned, so a wrong byte shows.</summary>
    public static byte[] Weights(int size, byte seed = 7)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)((i * 31) ^ (i >> 8) ^ seed);
        return bytes;
    }
}
