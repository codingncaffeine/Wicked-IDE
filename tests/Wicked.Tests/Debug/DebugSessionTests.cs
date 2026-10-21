using System.Text.Json.Nodes;
using Wicked.Core.Debug;
using Wicked.FakeAdapter;
using static Wicked.Tests.Debug.DebugKit;

namespace Wicked.Tests.Debug;

/// <summary>A debug session against the scripted adapter: the protocol's flow, stops, stepping, looking at variables.</summary>
public sealed class DebugSessionTests : IAsyncDisposable
{
    private readonly TempProfile _profile = new();
    private readonly BreakpointStore _store = new();
    private readonly FakeAdapterProvider _adapters;
    private readonly DebugManager _manager;

    public DebugSessionTests()
    {
        _adapters = new FakeAdapterProvider();
        _manager = new DebugManager(_store, _adapters);
    }

    public async ValueTask DisposeAsync()
    {
        await _manager.DisposeAsync();
        _profile.Dispose();
    }

    private async Task<(DebugSession Session, Watch Watch)> StartAsync(string program, Action<JsonObject>? more = null)
    {
        var request = Launch(program, more: more);
        DebugSession? created = null;
        Watch? watch = null;
        _manager.SessionStarted += s =>
        {
            // A child the adapter asks for can start before the parent finishes: only the first is the one started here.
            if (created is not null) return;
            created = s;
            watch = new Watch(s);
        };
        var session = await _manager.StartAsync(request, null, Ct);
        await WaitAsync(() => watch is not null, "the watch");
        Assert.Same(created, session);
        return (session, watch!);
    }

    [Fact]
    public async Task StopsAtABreakpointAndShowsTheStackScopesAndVariables()
    {
        var program = Program(_profile);
        _store.Add(program, 3);

        var (session, watch) = await StartAsync(program);
        var stop = await watch.NextStopAsync();

        Assert.Equal("breakpoint", stop.Reason);
        Assert.Equal(DebugSessionState.Stopped, session.State);
        Assert.Equal(1, session.StoppedThreadId);
        var threads = await session.RefreshThreadsAsync(Ct);
        Assert.Equal("main", Assert.Single(threads).Name);
        var (frames, total) = await session.StackTraceAsync(1, 0, 20, Ct);
        Assert.Equal(2, total);
        Assert.Equal(["Main", "Caller"], frames.Select(f => f.Name));
        Assert.Equal(3, frames[0].Line);
        Assert.Equal(program, frames[0].Source!.Path);
        var scopes = await session.ScopesAsync(frames[0].Id, Ct);
        Assert.Equal(["Locals", "Globals"], scopes.Select(s => s.Name));
        var locals = await session.VariablesAsync(scopes[0].VariablesReference, ct: Ct);
        Assert.Equal("3", locals.Single(v => v.Name == "pc").Value);
        Assert.Equal("7", locals.Single(v => v.Name == "counter").Value);
        var obj = locals.Single(v => v.Name == "obj");
        var children = await session.VariablesAsync(obj.VariablesReference, ct: Ct);
        Assert.Equal(["a", "b", "list"], children.Select(c => c.Name));
    }

    [Fact]
    public async Task APageOfAnIndexedVariableIsAskedFor()
    {
        var program = Program(_profile);
        _store.Add(program, 2);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        var page = await session.VariablesAsync(11, "indexed", 100, 50, Ct);

        Assert.Equal(50, page.Count);
        Assert.Equal("[100]", page[0].Name);
        Assert.Equal("200", page[0].Value);
        Assert.Equal("[149]", page[^1].Name);
    }

    [Fact]
    public async Task StepsLineByLineAndTheStackFollows()
    {
        var program = Program(_profile);
        _store.Add(program, 2);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        await session.StepOverAsync(ct: Ct);
        var step = await watch.NextStopAsync();
        var (afterOver, _) = await session.StackTraceAsync(1, 0, 20, Ct);
        await session.StepIntoAsync(ct: Ct);
        await watch.NextStopAsync();
        var (afterInto, _) = await session.StackTraceAsync(1, 0, 20, Ct);
        await session.StepOutAsync(ct: Ct);
        await watch.NextStopAsync();
        var (afterOut, _) = await session.StackTraceAsync(1, 0, 20, Ct);

        Assert.Equal("step", step.Reason);
        Assert.Equal(3, afterOver[0].Line);
        Assert.Equal(4, afterInto[0].Line);
        Assert.Equal(5, afterOut[0].Line);
    }

    [Fact]
    public async Task ContinuingToTheEndEndsTheSessionWithTheProgramsOutputAndExitCode()
    {
        var program = Program(_profile);
        var (session, watch) = await StartAsync(program);

        await WaitAsync(() => session.State == DebugSessionState.Ended, "the end");

        Assert.Contains("Hello from the fake adapter", watch.Text);
        Assert.Contains("exited with code 0", watch.Text);
        Assert.Equal(0, session.ExitCode);
        Assert.Empty(_manager.Sessions);
    }

    [Fact]
    public async Task ContinueAfterAStopGoesOnToTheNextBreakpoint()
    {
        var program = Program(_profile);
        _store.Add(program, 2);
        _store.Add(program, 5);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        await session.ContinueAsync(ct: Ct);
        await watch.NextStopAsync();
        var (frames, _) = await session.StackTraceAsync(1, 0, 20, Ct);

        Assert.Equal(5, frames[0].Line);
    }

    [Fact]
    public async Task ConditionHitCountAndLogpointBreakpointsBehaveAsTheyAreSet()
    {
        var program = Program(_profile, 8);
        _store.Add(program, 3, condition: "pc > 100");
        _store.Add(program, 4, logMessage: "reached {pc}");
        _store.Add(program, 6, hitCondition: "1");
        _store.Add(program, 7, condition: "pc == 7");

        var (session, watch) = await StartAsync(program);
        var first = await watch.NextStopAsync();
        var (a, _) = await session.StackTraceAsync(1, 0, 20, Ct);
        await session.ContinueAsync(ct: Ct);
        await watch.NextStopAsync();
        var (b, _) = await session.StackTraceAsync(1, 0, 20, Ct);

        Assert.Equal(6, a[0].Line); // line 3's condition is false and line 4 only logs
        Assert.Equal(7, b[0].Line);
        Assert.Contains("reached 4", watch.Text);
        Assert.Equal("breakpoint", first.Reason);
    }

    [Fact]
    public async Task AnUnboundBreakpointIsReportedUnverifiedWithTheAdaptersReason()
    {
        var program = Program(_profile, 3);
        var beyond = _store.Add(program, 99);
        var fine = _store.Add(program, 2);

        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        Assert.False(session.ResultFor(beyond.Id)!.Verified);
        Assert.Equal("There is no code on that line", session.ResultFor(beyond.Id)!.Message);
        Assert.True(session.ResultFor(fine.Id)!.Verified);
    }

    [Fact]
    public async Task ABreakpointAddedWhileStoppedIsSentAndHitLater()
    {
        var program = Program(_profile);
        _store.Add(program, 2);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        var added = _store.Add(program, 5);
        await WaitAsync(() => session.ResultFor(added.Id)?.Verified == true, "the new breakpoint to be verified");
        await session.ContinueAsync(ct: Ct);
        await watch.NextStopAsync();
        var (frames, _) = await session.StackTraceAsync(1, 0, 20, Ct);

        Assert.Equal(5, frames[0].Line);
    }

    [Fact]
    public async Task ADisabledBreakpointIsNotSentAndARemovedOneIsTakenBack()
    {
        var program = Program(_profile);
        var off = _store.Add(program, 2);
        _store.Update(off.Id, b => b with { Enabled = false });
        var removed = _store.Add(program, 3);
        var kept = _store.Add(program, 5);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();
        _store.Remove(removed.Id);
        await WaitAsync(() => _adapters.Adapters[0].Requests.Count(r => r.StartsWith("setBreakpoints", StringComparison.Ordinal)) >= 2, "the update to be sent");

        Assert.Null(session.ResultFor(off.Id));
        await session.ContinueAsync(ct: Ct);
        await watch.NextStopAsync();
        var (frames, _) = await session.StackTraceAsync(1, 0, 20, Ct);
        Assert.Equal(5, frames[0].Line);
        Assert.True(session.ResultFor(kept.Id)!.Verified);
    }

    [Fact]
    public async Task FunctionBreakpointsAndExceptionFiltersStopTheProgram()
    {
        var program = Program(_profile, 8, (4, "func helper"), (6, "throw oops"));
        _store.AddFunction("helper");
        var (session, watch) = await StartAsync(program);
        var first = await watch.NextStopAsync();
        _store.SetFilter("all", true);
        await WaitAsync(() => _adapters.Adapters[0].Requests.Any(r => r.StartsWith("setExceptionBreakpoints", StringComparison.Ordinal) && r.Contains("\"all\"", StringComparison.Ordinal)), "the filter to be sent");
        await session.ContinueAsync(ct: Ct);
        var second = await watch.NextStopAsync();
        var info = await session.ExceptionInfoAsync(1, Ct);

        Assert.Equal("function breakpoint", first.Reason);
        Assert.Equal("exception", second.Reason);
        Assert.Equal("FakeError", info!.ExceptionId);
        Assert.Contains(_store.Filters, f => f.Filter == "uncaught" && f.Enabled);
        Assert.Contains(_store.Filters, f => f.Filter == "all" && f.Enabled);
    }

    [Fact]
    public async Task EvaluateAnswersInEveryContextAndAnErrorCarriesTheAdaptersMessage()
    {
        var program = Program(_profile);
        _store.Add(program, 3);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        var sum = await session.EvaluateAsync("pc + counter", 1, "repl", Ct);
        var hover = await session.EvaluateAsync("counter", 1, "hover", Ct);
        var watched = await session.EvaluateAsync("obj", 1, "watch", Ct);
        var ex = await Assert.ThrowsAsync<DapException>(() => session.EvaluateAsync("nothing", 1, "repl", Ct));

        Assert.Equal("10", sum.Result);
        Assert.Equal("7", hover.Result);
        Assert.NotEqual(0, watched.VariablesReference);
        Assert.Equal("Unknown identifier 'nothing'", ex.Message);
    }

    [Fact]
    public async Task SettingAVariableChangesWhatIsShownNext()
    {
        var program = Program(_profile);
        _store.Add(program, 3);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();
        var scopes = await session.ScopesAsync(1, Ct);

        var set = await session.SetVariableAsync(scopes[0].VariablesReference, "counter", "41", Ct);
        var after = await session.EvaluateAsync("counter", 1, "watch", Ct);

        Assert.Equal("41", set.Value);
        Assert.Equal("41", after.Result);
        await Assert.ThrowsAsync<DapException>(() => session.SetVariableAsync(scopes[0].VariablesReference, "counter", "banana", Ct));
    }

    [Fact]
    public async Task StopEndsTheProgramThroughTerminateAndLeavesNoSession()
    {
        var program = Program(_profile);
        _store.Add(program, 2);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        await session.StopAsync(Ct);

        Assert.Equal(DebugSessionState.Ended, session.State);
        Assert.Empty(_manager.Sessions);
        Assert.Contains(_adapters.Adapters[0].Requests, r => r.StartsWith("terminate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopFallsBackToDisconnectWhenTheAdapterCannotTerminate()
    {
        var provider = new FakeAdapterProvider(new ScriptedAdapterOptions { SupportsTerminate = false });
        await using var manager = new DebugManager(_store, provider);
        var program = Program(_profile);
        _store.Add(program, 2);
        var session = await manager.StartAsync(Launch(program), null, Ct);
        await WaitAsync(() => session.State == DebugSessionState.Stopped, "the stop");

        await session.StopAsync(Ct);

        Assert.Equal(DebugSessionState.Ended, session.State);
        Assert.DoesNotContain(provider.Adapters[0].Requests, r => r.StartsWith("terminate", StringComparison.Ordinal));
        Assert.Contains(provider.Adapters[0].Requests, r => r.StartsWith("disconnect", StringComparison.Ordinal) && r.Contains("\"terminateDebuggee\":true", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedLaunchThrowsTheAdaptersMessageAndLeavesNothingBehind()
    {
        var program = Program(_profile);

        var ex = await Assert.ThrowsAsync<DapException>(() => _manager.StartAsync(Launch(program, more: o => o["failLaunch"] = "The program cannot be started: no such file"), null, Ct));

        Assert.Equal("The program cannot be started: no such file", ex.Message);
        Assert.Empty(_manager.Sessions);
    }

    [Fact]
    public async Task StopOnEntryStopsAtTheFirstLineBeforeAnyBreakpoint()
    {
        var program = Program(_profile);
        var (session, watch) = await StartAsync(program, o => o["stopOnEntry"] = true);

        var stop = await watch.NextStopAsync();
        var (frames, _) = await session.StackTraceAsync(1, 0, 20, Ct);

        Assert.Equal("entry", stop.Reason);
        Assert.Equal(1, frames[0].Line);
    }

    [Fact]
    public async Task RestartUsesTheAdaptersOwnRestartWhenItHasOneAndStartsAgainWhenItDoesNot()
    {
        var program = Program(_profile);
        _store.Add(program, 2);
        var (first, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        var again = await _manager.RestartAsync(first, Ct);
        await WaitAsync(() => again.State == DebugSessionState.Stopped, "the restarted program to stop");

        Assert.NotSame(first, again);
        Assert.Equal(DebugSessionState.Ended, first.State);
        Assert.Equal(DebugSessionState.Stopped, again.State);

        var provider = new FakeAdapterProvider(new ScriptedAdapterOptions { SupportsRestart = true });
        await using var manager = new DebugManager(_store, provider);
        var native = await manager.StartAsync(Launch(program), null, Ct);
        await WaitAsync(() => native.State == DebugSessionState.Stopped, "the stop");
        var same = await manager.RestartAsync(native, Ct);
        await WaitAsync(() => provider.Adapters[0].Requests.Any(r => r.StartsWith("restart", StringComparison.Ordinal)), "the restart request");
        Assert.Same(native, same);
    }

    [Fact]
    public async Task TheAdaptersRequestToRunAProgramInATerminalIsAnsweredByTheManager()
    {
        var program = Program(_profile);
        RunInTerminalArguments? seen = null;
        _manager.RunInTerminal = (args, _, _) =>
        {
            seen = args;
            return Task.FromResult(new RunInTerminalResult(4242));
        };

        var (_, watch) = await StartAsync(program, o =>
        {
            o["console"] = "integratedTerminal";
            o["terminalCommand"] = new JsonArray("/bin/app", "--flag");
        });
        await WaitAsync(() => watch.Text.Contains("terminal process 4242", StringComparison.Ordinal), "the terminal answer");

        Assert.Equal(["/bin/app", "--flag"], seen!.Args);
        Assert.Equal("integrated", seen.Kind);
    }

    [Fact]
    public async Task AChildSessionTheAdapterAsksForStartsBesideItsParent()
    {
        var program = Program(_profile);
        _store.Add(program, 2);

        var (parent, _) = await StartAsync(program, o => o["child"] = new JsonObject { ["name"] = "Child", ["type"] = "fake" });
        await WaitAsync(() => _manager.Sessions.Count == 2, "the child session");

        var child = _manager.Sessions.Single(s => s != parent);
        Assert.Equal("Child", child.Name);
        Assert.Same(parent, child.Parent);
        await WaitAsync(() => child.State == DebugSessionState.Stopped, "the child to stop at the shared breakpoint");
        Assert.Equal(2, _adapters.Adapters.Count);
        Assert.Equal(1, _adapters.Resolved); // the child used its parent's adapter again, not a new lookup
    }

    [Fact]
    public async Task AnAdapterThatClosesMidSessionEndsItInsteadOfHangingTheViews()
    {
        var program = Program(_profile);
        _store.Add(program, 2);
        var (session, watch) = await StartAsync(program);
        await watch.NextStopAsync();

        _adapters.Adapters[0].Die();
        await WaitAsync(() => session.State == DebugSessionState.Ended, "the session to notice");

        await Assert.ThrowsAsync<DapException>(() => session.EvaluateAsync("pc", 1, "repl", Ct));
    }
}
