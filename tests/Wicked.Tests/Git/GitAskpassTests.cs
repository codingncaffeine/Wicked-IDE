using System.Collections.Concurrent;
using System.Diagnostics;
using Wicked.Core.Git;

namespace Wicked.Tests.Git;

public sealed class GitAskpassTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(int Exit, string Output)> RunHelperAsync(AskpassRelay relay, string prompt)
    {
        var info = new ProcessStartInfo(relay.ScriptPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        info.ArgumentList.Add(prompt);
        foreach (var (key, value) in relay.Variables)
        {
            if (value is not null) info.Environment[key] = value;
        }

        using var process = Process.Start(info)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, output);
    }

    [Fact]
    public async Task TheHelperRelaysThePromptAndReturnsTheAnswerAndNothingElse()
    {
        if (OperatingSystem.IsWindows()) return;
        using var box = GitSandbox.CreateEmpty();
        using var relay = AskpassRelay.Create(box.Root);
        Assert.NotNull(relay);
        var asked = new ConcurrentQueue<AskpassRequest>();
        relay.Handler = request =>
        {
            asked.Enqueue(request);
            return Task.FromResult<string?>("p4ss word with 'quotes' and $dollar");
        };

        var (exit, output) = await RunHelperAsync(relay, "Password for 'https://user@example.invalid': ");
        Assert.Equal(0, exit);
        Assert.Equal("p4ss word with 'quotes' and $dollar\n", output);
        var request = Assert.Single(asked);
        Assert.Equal("Password for 'https://user@example.invalid': ", request.Prompt);
        Assert.True(request.IsSecret);
    }

    [Fact]
    public async Task CancellingThePromptMakesTheHelperFailWithNoOutput()
    {
        if (OperatingSystem.IsWindows()) return;
        using var box = GitSandbox.CreateEmpty();
        using var relay = AskpassRelay.Create(box.Root);
        Assert.NotNull(relay);
        relay.Handler = _ => Task.FromResult<string?>(null);
        var (exit, output) = await RunHelperAsync(relay, "Username for 'https://example.invalid': ");
        Assert.NotEqual(0, exit);
        Assert.Equal("", output);

        // No handler at all counts as a cancel: nothing hangs.
        relay.Handler = null;
        var (noneExit, _) = await RunHelperAsync(relay, "Username for 'x': ");
        Assert.NotEqual(0, noneExit);
    }

    [Fact]
    public async Task GitAsksThroughTheRelayForAUsernameAndAPassword()
    {
        if (OperatingSystem.IsWindows()) return;
        using var box = GitSandbox.CreateEmpty();
        using var relay = AskpassRelay.Create(box.Root);
        Assert.NotNull(relay);
        var asked = new ConcurrentQueue<AskpassRequest>();
        relay.Handler = request =>
        {
            asked.Enqueue(request);
            return Task.FromResult<string?>(request.Prompt.StartsWith("Username", StringComparison.Ordinal) ? "alice" : "hunter2");
        };

        var vars = new Dictionary<string, string?>(box.Env.Variables);
        foreach (var (key, value) in relay.Variables) vars[key] = value;
        var env = new GitEnvironment { Timeout = TimeSpan.FromSeconds(30), Variables = vars, Log = box.Env.Log };
        var result = await GitProcess.RunAsync(env, box.Root, ["credential", "fill"], "protocol=https\nhost=example.invalid\n\n", Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Contains("username=alice", result.Output, StringComparison.Ordinal);
        Assert.Contains("password=hunter2", result.Output, StringComparison.Ordinal);
        var prompts = asked.ToArray();
        Assert.Equal(2, prompts.Length);
        Assert.False(prompts[0].IsSecret);
        Assert.StartsWith("Username for", prompts[0].Prompt, StringComparison.Ordinal);
        Assert.True(prompts[1].IsSecret);
        Assert.StartsWith("Password for", prompts[1].Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManyPromptsAtOnceEachGetTheirOwnAnswer()
    {
        if (OperatingSystem.IsWindows()) return;
        using var box = GitSandbox.CreateEmpty();
        using var relay = AskpassRelay.Create(box.Root);
        Assert.NotNull(relay);
        relay.Handler = async request =>
        {
            await Task.Delay(Random.Shared.Next(10, 80), Ct);
            return "answer to " + request.Prompt;
        };

        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => RunHelperAsync(relay, $"Enter passphrase number {i}: ")));
        for (var i = 0; i < runs.Length; i++) Assert.Equal($"answer to Enter passphrase number {i}: \n", runs[i].Output);
    }

    [Fact]
    public async Task WithoutTheRelayGitNeverWaitsForATerminal()
    {
        using var box = GitSandbox.CreateEmpty();
        var result = await GitProcess.RunAsync(box.Env, box.Root, ["credential", "fill"], "protocol=https\nhost=example.invalid\n\n", Ct);
        Assert.False(result.Ok);
    }

    [Fact]
    public void ThePromptKindDecidesWhetherTheAnswerIsHidden()
    {
        Assert.True(AskpassRequest.From("Password for 'https://a@b': ").IsSecret);
        Assert.True(AskpassRequest.From("Enter passphrase for key '/home/u/.ssh/id_ed25519': ").IsSecret);
        Assert.False(AskpassRequest.From("Username for 'https://github.com': ").IsSecret);
        Assert.False(AskpassRequest.From("Are you sure you want to continue connecting (yes/no/[fingerprint])? ").IsSecret);
    }
}
