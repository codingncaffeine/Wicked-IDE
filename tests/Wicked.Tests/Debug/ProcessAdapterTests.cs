using System.Diagnostics;
using Wicked.Core.Debug;

namespace Wicked.Tests.Debug;

/// <summary>Real processes: the scripted adapter run as a child over stdio and over a port, and programs run on an adapter's behalf.</summary>
public sealed class ProcessAdapterTests : IAsyncDisposable
{
    private readonly TempProfile _profile = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync()
    {
        _profile.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string Dotnet
    {
        get
        {
            var root = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            var dotnet = Path.Combine(root, "dotnet");
            return File.Exists(dotnet) ? dotnet : "dotnet";
        }
    }

    private static string Fake => Path.Combine(AppContext.BaseDirectory, "Wicked.FakeAdapter.dll");

    private sealed class Fixed(AdapterEndpoint endpoint) : IDebugAdapterProvider
    {
        public Task<AdapterEndpoint> ResolveAsync(string type, System.Text.Json.JsonElement configuration, CancellationToken ct) => Task.FromResult(endpoint);
    }

    [Fact]
    public async Task ASessionRunsAgainstAnAdapterStartedAsAChildProcessOverStdio()
    {
        var program = DebugKit.Program(_profile);
        var store = new BreakpointStore();
        store.Add(program, 3);
        var endpoint = new ProcessAdapterEndpoint(new AdapterCommand(Dotnet, [Fake], new Dictionary<string, string>(), AdapterTransport.Stdio), _profile.Root);
        await using var manager = new DebugManager(store, new Fixed(endpoint));

        var session = await manager.StartAsync(DebugKit.Launch(program), null, Ct);
        var watch = new DebugKit.Watch(session);
        await DebugKit.WaitAsync(() => session.State == DebugSessionState.Stopped, "the stop", 30);
        var (frames, _) = await session.StackTraceAsync(1, 0, 20, Ct);
        await session.StopAsync(Ct);

        Assert.Equal(3, frames[0].Line);
        Assert.Equal(DebugSessionState.Ended, session.State);
        Assert.NotNull(watch);
    }

    [Fact]
    public async Task ASessionConnectsToAnAdapterThatListensOnAPortItWasToldAndTheProcessEndsWithTheSession()
    {
        var program = DebugKit.Program(_profile);
        var store = new BreakpointStore();
        store.Add(program, 2);
        var endpoint = new ProcessAdapterEndpoint(new AdapterCommand(Dotnet, [Fake, "--port", "{port}"], new Dictionary<string, string>(), AdapterTransport.Tcp), _profile.Root);
        await using var manager = new DebugManager(store, new Fixed(endpoint));

        var session = await manager.StartAsync(DebugKit.Launch(program), null, Ct);
        await DebugKit.WaitAsync(() => session.State == DebugSessionState.Stopped, "the stop", 30);
        var scopes = await session.ScopesAsync(1, Ct);
        var locals = await session.VariablesAsync(scopes[0].VariablesReference, ct: Ct);
        await session.StopAsync(Ct);

        Assert.Equal("2", locals.Single(v => v.Name == "pc").Value);
    }

    [Fact]
    public async Task AnAdapterThatCannotBeStartedIsAnErrorThatSaysWhich()
    {
        var endpoint = new ProcessAdapterEndpoint(new AdapterCommand(Path.Combine(_profile.Root, "not-a-program"), [], new Dictionary<string, string>(), AdapterTransport.Stdio), _profile.Root);

        var ex = await Assert.ThrowsAsync<IOException>(() => endpoint.OpenAsync(Ct));

        Assert.Contains("not-a-program", ex.Message);
    }

    [Fact]
    public async Task AnAdapterThatExitsBeforeItListensIsReportedWithItsLastWords()
    {
        // `sh -c` prints a line to stderr and exits at once: the port never opens.
        var endpoint = new ProcessAdapterEndpoint(new AdapterCommand("/bin/sh", ["-c", "echo 'adapter: cannot bind' >&2; exit 3", "{port}"], new Dictionary<string, string>(), AdapterTransport.Tcp), _profile.Root);

        var ex = await Assert.ThrowsAsync<IOException>(() => endpoint.OpenAsync(Ct));

        Assert.Contains("exited before it listened", ex.Message);
        Assert.Contains("code 3", ex.Message);
        Assert.Contains("cannot bind", ex.Message);
    }

    [Fact]
    public async Task AProgramRunOnTheAdaptersBehalfHasItsOutputSentToTheConsoleAndEndsWithTheSession()
    {
        var output = new List<(string Category, string Text)>();
        var request = new RunInTerminalArguments("integrated", "t", _profile.Root, ["/bin/sh", "-c", "echo out-line; echo err-line >&2; sleep 60"], new Dictionary<string, string?> { ["FROM_TEST"] = "1" });

        var result = await DetachedProcess.RunAsync(request, (c, t) =>
        {
            lock (output) output.Add((c, t));
        }, Ct);
        await DebugKit.WaitAsync(() => { lock (output) return output.Count >= 2; }, "the program's output");

        Assert.NotNull(result.ProcessId);
        lock (output)
        {
            Assert.Contains(("stdout", "out-line\n"), output);
            Assert.Contains(("stderr", "err-line\n"), output);
        }

        var process = Process.GetProcessById(result.ProcessId!.Value);
        Assert.False(process.HasExited);
        result.Handle!.Dispose();
        await DebugKit.WaitAsync(() => !Alive(result.ProcessId.Value), "the program to be ended");
    }

    private static bool Alive(int id)
    {
        try
        {
            using var p = Process.GetProcessById(id);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task AProgramRunOnTheAdaptersBehalfLivesInAProcessGroupOfItsOwnWhereTheSystemHasSetsid()
    {
        if (!File.Exists("/usr/bin/setsid")) return;
        var request = new RunInTerminalArguments("integrated", null, _profile.Root, ["/bin/sh", "-c", "sleep 60"], null);

        var result = await DetachedProcess.RunAsync(request, null, Ct);
        try
        {
            // setsid takes a moment to run before it replaces itself with the program.
            await DebugKit.WaitAsync(() => GroupOf(result.ProcessId!.Value) == result.ProcessId, "the program to leave our process group");
            Assert.NotEqual(GroupOf(Environment.ProcessId), GroupOf(result.ProcessId!.Value));
        }
        finally
        {
            result.Handle!.Dispose();
        }
    }

    private static int GroupOf(int id)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{id}/stat");
            return int.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (IOException)
        {
            return -1;
        }
    }

    [Fact]
    public async Task WhateverIsStillRunningWhenWickedExitsIsEndedByTheRegistry()
    {
        var request = new RunInTerminalArguments("integrated", null, _profile.Root, ["/bin/sh", "-c", "sleep 60"], null);
        var result = await DetachedProcess.RunAsync(request, null, Ct);
        var before = ProcessRegistry.Count;
        Assert.True(Alive(result.ProcessId!.Value));

        ProcessRegistry.KillAll();

        await DebugKit.WaitAsync(() => !Alive(result.ProcessId.Value), "the registry to end the program");
        Assert.True(before >= 1);
    }

    [Fact]
    public void TheProcessListHasRunningProgramsWithTheirCommandLinesAndNotThisOne()
    {
        var list = ProcessLister.List();

        Assert.NotEmpty(list);
        Assert.All(list, p => Assert.NotEmpty(p.CommandLine));
        Assert.DoesNotContain(list, p => p.Id == Environment.ProcessId);
    }
}
