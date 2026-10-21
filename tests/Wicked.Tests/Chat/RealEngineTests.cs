using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Engines;
using Wicked.Core.Models;

namespace Wicked.Tests.Chat;

/// <summary>
/// The whole path against the real thing: the CPU llama.cpp build installed through <c>EnsureEngineAsync</c>, a tiny real model
/// (bartowski's SmolLM2-135M-Instruct at Q4_0, Apache-2.0, 92 MB), loaded through the router and streamed from.
/// Skipped unless <c>WICKED_REAL_ENGINE=1</c>. Downloads are kept in the repository's gitignored <c>scratch/real-engine</c>
/// so a second run starts at once.
/// </summary>
public sealed class RealEngineTests
{
    private const string Repo = "bartowski/SmolLM2-135M-Instruct-GGUF";
    private const string Id = Repo + ":Q4_0";
    private const string DraftId = Repo + ":Q2_K";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Say(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static async Task<ModelServices> StartAsync(bool withDraft = false)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WICKED_REAL_ENGINE") == "1", "set WICKED_REAL_ENGINE=1 to download and run a real engine and model");
        var root = Path.Combine(ScratchFolder(), "real-engine", "profile");
        Directory.CreateDirectory(root);
        var paths = WickedPaths.Resolve(v => v == WickedPaths.HomeVariable ? root : null);
        paths.EnsureCreated();
        var services = new ModelServices(paths);
        services.Configure(new ModelOptions(Backend: EngineBackendKind.Cpu));
        await services.InitializeAsync(allowNetwork: false, Ct);

        var engine = await services.EnsureEngineAsync(Ct);
        Say($"engine {engine.Id} at {engine.ServerPath}");
        if (services.State.Installed.All(m => m.Id != Repo))
        {
            var detail = await services.GetHubRepoAsync(Repo, Ct);
            await services.InstallFromHubAsync(Repo, detail.Variants.Single(v => v.Quant == "Q4_0"), detail.Model, licenceAccepted: true, Ct);
        }

        if (withDraft && services.State.Installed.SelectMany(m => m.Variants).All(v => v.Id != DraftId))
        {
            var detail = await services.GetHubRepoAsync(Repo, Ct);
            await services.InstallFromHubAsync(Repo, detail.Variants.Single(v => v.Quant == "Q2_K"), detail.Model, licenceAccepted: true, Ct);
        }

        for (var i = 0; i < 100 && services.Runtime.Models.All(m => m.Id != Id || withDraft && services.Runtime.Models.All(d => d.Id != DraftId)); i++) await Task.Delay(50, Ct); // the list follows the store on a worker
        return services;
    }

    private static async Task<(string Content, string Reasoning, ChatUsage? Usage, ChatFinished? Finished)> AskAsync(ModelServices services, string question, int maxTokens = 24)
    {
        var content = new System.Text.StringBuilder();
        var reasoning = new System.Text.StringBuilder();
        ChatUsage? usage = null;
        ChatFinished? finished = null;
        await foreach (var chunk in services.Chat.StreamAsync(new ChatRequest(Id, [new ChatMessage(ChatRole.User, question)], new ChatSampling(Temperature: 0.2, MaxTokens: maxTokens)), Ct))
        {
            switch (chunk)
            {
                case ContentDelta c: content.Append(c.Text); break;
                case ReasoningDelta r: reasoning.Append(r.Text); break;
                case ChatUsage u: usage = u; break;
                case ChatFinished f: finished = f; break;
            }
        }

        return (content.ToString(), reasoning.ToString(), usage, finished);
    }

    [Fact]
    public async Task ARealModelLoadsStreamsAReplyAndUnloads()
    {
        await using var services = await StartAsync();
        var runtime = services.Runtime;
        var model = Assert.Single(runtime.Models, m => m.Id == Id);
        Assert.Equal(RunState.Unloaded, model.State);
        Assert.Null(runtime.Router); // nothing starts until a load or a chat is asked for

        var changes = 0;
        runtime.Changed += (_, _) => Interlocked.Increment(ref changes);
        await runtime.LoadAsync(Id, Ct);
        var loaded = runtime.Models.Single(m => m.Id == Id);
        Assert.Equal(RunState.Loaded, loaded.State);
        Assert.True(loaded.ContextSize > 0);

        var (content, _, usage, finished) = await AskAsync(services, "Say hello in one short sentence.");

        Say($"reply: {content}");
        Say($"usage: {usage}; finished: {finished}; state changes: {changes}; context {loaded.ContextSize}");
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.NotNull(usage);
        Assert.True(usage.PromptTokens > 0 && usage.CompletionTokens > 0);
        Assert.NotNull(finished);

        await runtime.UnloadAsync(Id, Ct);
        Assert.Equal(RunState.Unloaded, runtime.Models.Single(m => m.Id == Id).State);
    }

    [Fact]
    public async Task EveryFlagTheRuntimeWritesIsAcceptedByTheRealEngine()
    {
        await using var services = await StartAsync(withDraft: true);
        var runtime = services.Runtime;
        var logBefore = File.Exists(runtime.LogPath) ? new FileInfo(runtime.LogPath).Length : 0; // the log of earlier runs does not count
        await runtime.SaveSettingsAsync(Id, new ModelRunSettings(
            ContextSize: 4096, GpuLayers: "0", KvCacheType: "q8_0", FlashAttention: true, DraftModelId: DraftId, Reasoning: false, Sampling: new ChatSampling(0.3, 0.9, 16)), Ct);

        await runtime.LoadAsync(Id, Ct);
        var loaded = runtime.Models.Single(m => m.Id == Id);
        var (content, _, usage, _) = await AskAsync(services, "Name a colour.", maxTokens: 8);

        Say($"preset:\n{await File.ReadAllTextAsync(runtime.PresetPath, Ct)}");
        Say($"reply: {content}; usage {usage}; context {loaded.ContextSize}");
        Assert.Equal(RunState.Loaded, loaded.State);
        Assert.Equal(4096, loaded.ContextSize);
        // The draft model is not just loaded, it drafts: the engine reports how many of its tokens were accepted.
        var drafted = false;
        for (var i = 0; i < 60 && !drafted; i++)
        {
            drafted = (await File.ReadAllTextAsync(runtime.LogPath, Ct)).AsSpan((int)Math.Min(logBefore, int.MaxValue)).Contains("draft acceptance", StringComparison.Ordinal);
            if (!drafted) await Task.Delay(50, Ct);
        }

        Assert.True(drafted, "the engine never reported draft acceptance");
        Assert.False(string.IsNullOrWhiteSpace(content));
        await runtime.SaveSettingsAsync(Id, new ModelRunSettings(), Ct); // leave the shared profile as it was
        await runtime.UnloadAsync(Id, Ct);
    }

    [Fact]
    public async Task AModelTheEngineCannotLoadFailsWithTheEnginesOwnWords()
    {
        await using var services = await StartAsync();
        var runtime = services.Runtime;
        var folder = Path.Combine(services.Store.Root, "junk");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "broken-Q4_0.gguf");
        await File.WriteAllBytesAsync(file, [.. "GGUF"u8.ToArray(), .. new byte[200]], Ct);
        try
        {
            await services.RescanAsync(Ct);
            var broken = "local/junk:Q4_0";
            for (var i = 0; i < 100 && runtime.Models.All(m => m.Id != broken); i++) await Task.Delay(50, Ct);

            var ex = await Assert.ThrowsAsync<ChatException>(() => runtime.LoadAsync(broken, Ct));

            Say($"error: {ex.Message}");
            var model = runtime.Models.Single(m => m.Id == broken);
            Assert.Equal(RunState.Failed, model.State);
            Assert.False(string.IsNullOrWhiteSpace(model.Error));
            // The others still work after a failure.
            await runtime.LoadAsync(Id, Ct);
            await runtime.UnloadAsync(Id, Ct);
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(folder);
        }
    }

    /// <summary>The repository's gitignored <c>scratch</c> folder: found by walking up from the test binaries to the solution.</summary>
    private static string ScratchFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) return Path.Combine(dir.FullName, "scratch");
        }

        throw new InvalidOperationException("Wicked.slnx not found above the test binaries");
    }
}
