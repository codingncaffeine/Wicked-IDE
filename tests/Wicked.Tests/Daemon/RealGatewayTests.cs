using System.Diagnostics;
using System.Text.Json;
using Wicked.Core;
using Wicked.Core.Catalog;
using Wicked.Core.Models;
using Wicked.Daemon;

namespace Wicked.Tests.Daemon;

/// <summary>
/// The gateway against the real thing, asked with the real <c>curl</c>: the CPU llama.cpp build installed through
/// <c>EnsureEngineAsync</c>, bartowski's SmolLM2-135M-Instruct at Q4_0 (Apache-2.0, 92 MB) installed under a catalog id of its own,
/// and every route of the gateway tried. Skipped unless <c>WICKED_REAL_ENGINE=1</c>. Downloads are kept in the repository's
/// gitignored <c>scratch/real-engine</c> (a profile of its own, so the CPU build is the one installed); only the daemon's socket lives in a short
/// temporary folder, because a Unix socket path cannot be as long as the repository's.
/// </summary>
public sealed class RealGatewayTests
{
    private const string Repo = "bartowski/SmolLM2-135M-Instruct-GGUF";
    private const string CatalogId = "smollm2-135m";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Say(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static async Task<(int Code, string Output)> CurlAsync(params string[] args)
    {
        var info = new ProcessStartInfo("curl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-sS", "--max-time", "120", "-w", "\n[http %{http_code}]" }.Concat(args)) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromSeconds(150));
        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await output + await error);
    }

    [Fact(Timeout = 900_000)]
    public async Task CurlCompletesAChatThroughTheGatewayAgainstACatalogIdWithTheRealEngine()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WICKED_REAL_ENGINE") == "1", "set WICKED_REAL_ENGINE=1 to download and run a real engine and model");
        var root = Path.Combine(ScratchFolder(), "real-engine", "gateway-profile");
        var socketFolder = Path.Combine(Path.GetTempPath(), "wk-real-" + Guid.NewGuid().ToString("N")[..8]);
        var paths = new WickedPaths(Path.Combine(root, "config"), Path.Combine(root, "data"), Path.Combine(root, "state"), Path.Combine(root, "cache"), socketFolder, Isolated: true);
        paths.EnsureCreated();
        // The daemon reads its options from settings.json, so the CPU build is chosen there.
        await Wicked.Core.Settings.SettingsFile.WriteAsync(paths.SettingsFile, new Wicked.Core.Settings.WickedSettings { RuntimeBackend = "cpu", ServerPort = 0 }, Ct);
        var services = new ModelServices(paths);
        await using var daemon = await DaemonHost.StartAsync(paths, Ct, new DaemonOptions { ServicesFactory = _ => services, AllowNetwork = true, RunUpdates = false });
        try
        {
            await daemon.Ready;
            var engine = await services.EnsureEngineAsync(Ct);
            Say($"engine {engine.Id}");
            if (services.State.Installed.All(m => m.Manifest?.CatalogId != CatalogId))
            {
                var detail = await services.GetHubRepoAsync(Repo, Ct);
                var variant = detail.Variants.Single(v => v.Quant == "Q4_0");
                var tier = new QuantTier("Q4_0", [.. variant.Files.Select(f => new CatalogFile(f.Path, f.Size, f.LfsSha256))], 85);
                var model = new CatalogModel(
                    CatalogId, "SmolLM2 135M", Repo, detail.Model.Sha, "apache-2.0", null, false, [Capability.Chat], 8192, new ParameterCount(0.135), new DateOnly(2024, 11, 1),
                    new RoleScores(Fast: 10), [], new ModelShape(30, 30, 0, 0, 3, 64, 64, 64, 64), [tier]);
                await services.InstallModelAsync(model, tier, licenceAccepted: true, Ct);
            }

            for (var i = 0; i < 100 && services.Runtime.Models.All(m => m.Id != Repo + ":Q4_0"); i++) await Task.Delay(50, Ct);
            var gateway = daemon.Gateway.Address!;
            Say($"gateway {gateway}");

            var models = await CurlAsync($"{gateway}/v1/models");
            Say("GET /v1/models\n" + models.Output);
            Assert.Contains($"\"id\":\"{CatalogId}\"", models.Output, StringComparison.Ordinal);

            var chat = await CurlAsync($"{gateway}/v1/chat/completions", "-H", "Content-Type: application/json",
                "-d", $$"""{"model": "{{CatalogId}}", "max_tokens": 24, "temperature": 0.2, "messages": [{"role": "user", "content": "Say hello in one short sentence."}]}""");
            Say("POST /v1/chat/completions (a catalog id, loaded on this first request)\n" + chat.Output);
            Assert.Equal(0, chat.Code);
            Assert.EndsWith("[http 200]", chat.Output, StringComparison.Ordinal);
            using (var json = JsonDocument.Parse(chat.Output[..chat.Output.LastIndexOf("\n[http", StringComparison.Ordinal)]))
            {
                Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()));
                Assert.True(json.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32() > 0);
            }

            var streamed = await CurlAsync("-N", $"{gateway}/v1/chat/completions", "-H", "Content-Type: application/json",
                "-d", $$"""{"model": "{{CatalogId}}", "stream": true, "max_tokens": 16, "messages": [{"role": "user", "content": "Name a colour."}]}""");
            Say("POST /v1/chat/completions (stream)\n" + streamed.Output);
            Assert.Contains("data: [DONE]", streamed.Output, StringComparison.Ordinal);

            var anthropic = await CurlAsync($"{gateway}/v1/messages", "-H", "Content-Type: application/json", "-H", "anthropic-version: 2023-06-01",
                "-d", $$"""{"model": "{{CatalogId}}", "max_tokens": 16, "messages": [{"role": "user", "content": "Name a colour."}]}""");
            Say("POST /v1/messages (Anthropic)\n" + anthropic.Output);
            Assert.EndsWith("[http 200]", anthropic.Output, StringComparison.Ordinal);
            Assert.Contains("\"type\":\"message\"", anthropic.Output, StringComparison.Ordinal);

            var anthropicStream = await CurlAsync("-N", $"{gateway}/v1/messages", "-H", "Content-Type: application/json", "-H", "anthropic-version: 2023-06-01",
                "-d", $$"""{"model": "{{CatalogId}}", "max_tokens": 16, "stream": true, "messages": [{"role": "user", "content": "Name a colour."}]}""");
            Say("POST /v1/messages (Anthropic, stream)\n" + anthropicStream.Output);
            Assert.Contains("message_stop", anthropicStream.Output, StringComparison.Ordinal);

            // The routes the plan lists besides: recorded for the build in use, not required of it.
            var completions = await CurlAsync($"{gateway}/v1/completions", "-H", "Content-Type: application/json", "-d", $$"""{"model": "{{CatalogId}}", "prompt": "The sky is", "max_tokens": 8}""");
            Say("POST /v1/completions\n" + completions.Output);
            var responses = await CurlAsync($"{gateway}/v1/responses", "-H", "Content-Type: application/json", "-d", $$"""{"model": "{{CatalogId}}", "input": "Say hi.", "max_output_tokens": 16}""");
            Say("POST /v1/responses\n" + responses.Output);
            var embeddings = await CurlAsync($"{gateway}/v1/embeddings", "-H", "Content-Type: application/json", "-d", $$"""{"model": "{{CatalogId}}", "input": "hello"}""");
            Say("POST /v1/embeddings\n" + embeddings.Output);

            var log = await CurlAsync("--unix-socket", paths.DaemonSocket, "http://wickedd/v1/wicked/log");
            Say("request log\n" + log.Output);
            Assert.Contains("\"route\":\"/v1/chat/completions\"", log.Output, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(socketFolder, recursive: true);
            }
            catch (IOException)
            {
                // The daemon removes its own socket on the way out; a leftover folder in /tmp is harmless.
            }
        }
    }

    private static string ScratchFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) return Path.Combine(dir.FullName, "scratch");
        }

        throw new InvalidOperationException("Wicked.slnx not found above the test binaries");
    }
}
