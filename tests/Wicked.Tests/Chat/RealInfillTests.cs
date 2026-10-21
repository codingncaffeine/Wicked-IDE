using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Core.Models;

namespace Wicked.Tests.Chat;

/// <summary>
/// Ghost text against the real engine: a small coding model with fill-in-the-middle tokens, its /infill answered through
/// the router. Skipped unless WICKED_REAL_ENGINE=1 (it downloads the CPU engine and a ~400 MB model into scratch/).
/// </summary>
public sealed class RealInfillTests
{
    private const string Repo = "bartowski/Qwen2.5-Coder-0.5B-Instruct-GGUF";
    private const string Id = Repo + ":Q4_K_M";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACodingModelFillsTheMiddleThroughTheRouter()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WICKED_REAL_ENGINE") == "1", "set WICKED_REAL_ENGINE=1 to download and run a real engine and model");
        var root = Path.Combine(Scratch(), "real-engine", "profile");
        Directory.CreateDirectory(root);
        var paths = WickedPaths.Resolve(v => v == WickedPaths.HomeVariable ? root : null);
        paths.EnsureCreated();
        await using var services = new ModelServices(paths);
        services.Configure(new ModelOptions(Backend: EngineBackendKind.Cpu));
        await services.InitializeAsync(allowNetwork: false, Ct);
        await services.EnsureEngineAsync(Ct);
        if (services.State.Installed.All(m => m.Id != Repo))
        {
            var detail = await services.GetHubRepoAsync(Repo, Ct);
            await services.InstallFromHubAsync(Repo, detail.Variants.Single(v => v.Quant == "Q4_K_M"), detail.Model, licenceAccepted: true, Ct);
        }

        for (var i = 0; i < 100 && services.Runtime.Models.All(m => m.Id != Id); i++) await Task.Delay(50, Ct);
        var infill = new InfillEngine(services.Runtime, Wicked.Core.Http.WickedHttp.Shared);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await infill.CompleteAsync(new InfillRequest(Id,
            "def add(a, b):\n    return ",
            "\n\nprint(add(2, 3))\n",
            [new InfillContext("util.py", "def sub(a, b):\n    return a - b\n")],
            MaxTokens: 16, TimeBudgetMs: 20000), Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"infill '{result.Text}' {result.Tokens} tokens {result.TokensPerSecond:0.0} tok/s in {started.ElapsedMilliseconds} ms");
        Assert.Contains("a + b", result.Text, StringComparison.Ordinal);
        await services.Runtime.UnloadAsync(Id, Ct);
    }

    private static string Scratch()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) return Path.Combine(dir.FullName, "scratch");
        }

        throw new InvalidOperationException("Wicked.slnx not found above the test binaries");
    }
}
