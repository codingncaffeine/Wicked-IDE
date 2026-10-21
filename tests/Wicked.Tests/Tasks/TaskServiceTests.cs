using System.Collections.Concurrent;
using System.Collections.Immutable;
using Wicked.Core.Tasks;

namespace Wicked.Tests.Tasks;

/// <summary>Tasks run as real child processes of /bin/sh, so these are skipped where there is none.</summary>
public sealed class TaskServiceTests
{
    private static bool Posix => !OperatingSystem.IsWindows();

    private static (TaskService Service, RecordingOutput Output, FakeEnvironment Window, ConcurrentQueue<(string Owner, ImmutableArray<TaskProblem> Problems)> Problems) Make(Sandbox box)
    {
        var output = new RecordingOutput();
        var window = new FakeEnvironment(box.Root);
        var service = new TaskService(output, window, n => n == "SHELL" ? "/bin/sh" : Environment.GetEnvironmentVariable(n)) { WorkspaceFolder = box.Root };
        var problems = new ConcurrentQueue<(string, ImmutableArray<TaskProblem>)>();
        service.ProblemsChanged += (owner, list) => problems.Enqueue((owner, list));
        return (service, output, window, problems);
    }

    private static async Task<TaskRunResult> RunAsync(TaskService service, string label)
    {
        var catalog = await service.LoadAsync(TestContext.Current.CancellationToken);
        var run = service.Start(catalog.Find(label)!, catalog);
        return await run.Completion.Within();
    }

    [Fact]
    public async Task AShellTaskStreamsItsOutputInOrderAndReportsItsExitCode()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.WriteTasks("""
            { "version": "2.0.0", "tasks": [
              { "label": "say", "type": "shell", "command": "echo one; echo two 1>&2; printf '\\033[31mred\\033[0m\\n'; exit 3",
                "options": { "env": { "GREETING": "hi" } } },
              { "label": "env", "type": "shell", "command": "echo $GREETING ${workspaceFolderBasename} $PWD", "options": { "env": { "GREETING": "hi" } } },
              { "label": "ok", "type": "process", "command": "echo", "args": ["a b", "c"] }
            ] }
            """);
        var (service, output, _, _) = Make(box);
        using var _s = service;

        var result = await RunAsync(service, "say");
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        var lines = output.Lines("say");
        Assert.Equal("one", lines.First(l => l == "one"));
        Assert.Contains("two", lines);
        // The colour codes are kept in what the sink receives (a terminal will show them); the panel strips them.
        Assert.Contains("\u001b[31mred\u001b[0m", lines);
        Assert.Matches(@"^> Executing task at \d\d:\d\d:\d\d: echo one", lines[0]);
        Assert.Matches(@"^> Task failed with exit code 3 in \d+\.\d s <$", lines[^1]);
        Assert.Equal(3, output.Outcome("say")!.ExitCode);

        var env = await RunAsync(service, "env");
        Assert.Contains($"hi {Path.GetFileName(box.Root)} {box.Root}", output.Lines("env"));
        Assert.True(env.Succeeded);

        var process = await RunAsync(service, "ok");
        Assert.Contains("a b c", output.Lines("ok"));
        Assert.True(process.Succeeded);
    }

    [Fact]
    public async Task ProblemsAreReportedUnderTheTaskAndClearedWhenItRunsAgain()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.Write("main.c", "int main() { return x; }\n");
        box.WriteTasks("""
            { "version": "2.0.0", "tasks": [
              { "label": "cc", "type": "shell", "command": "echo \"main.c:1:21: error: 'x' undeclared\"; exit 1", "problemMatcher": "$gcc" }
            ] }
            """);
        var (service, _, _, problems) = Make(box);
        using var _s = service;

        var first = await RunAsync(service, "cc");
        var problem = Assert.Single(first.Problems);
        Assert.Equal(Path.Combine(box.Root, "main.c"), problem.File);
        Assert.Equal(1, first.ExitCode);
        Assert.Contains(problems, p => p.Owner == "cc" && p.Problems.Length == 1);

        problems.Clear();
        var again = service.Start((await service.LoadAsync(TestContext.Current.CancellationToken)).Find("cc")!, await service.LoadAsync(TestContext.Current.CancellationToken));
        await again.Completion.Within();
        // The first thing the second run reports is that the task has none.
        Assert.True(problems.TryPeek(out var firstEvent));
        Assert.Equal(("cc", 0), (firstEvent.Owner, firstEvent.Problems.Length));
    }

    [Fact]
    public async Task DependenciesRunFirstInTheOrderAskedAndAFailureStopsTheChain()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.WriteTasks("""
            { "version": "2.0.0", "tasks": [
              { "label": "a", "type": "shell", "command": "sleep 0.4; echo a >> order.txt" },
              { "label": "b", "type": "shell", "command": "echo b >> order.txt" },
              { "label": "seq", "type": "shell", "command": "echo seq >> order.txt", "dependsOn": ["a", "b"], "dependsOrder": "sequence" },
              { "label": "par", "type": "shell", "command": "echo par >> order.txt", "dependsOn": ["a", "b"] },
              { "label": "bad", "type": "shell", "command": "exit 2" },
              { "label": "after", "type": "shell", "command": "echo after >> order.txt", "dependsOn": ["bad"] },
              { "label": "loop1", "type": "shell", "command": "true", "dependsOn": ["loop2"] },
              { "label": "loop2", "type": "shell", "command": "true", "dependsOn": ["loop1"] },
              { "label": "ghost", "type": "shell", "command": "true", "dependsOn": ["nothing"] }
            ] }
            """);
        var (service, output, _, _) = Make(box);
        using var _s = service;
        string Order() => File.Exists(box.Path_("order.txt")) ? string.Join(",", File.ReadAllLines(box.Path_("order.txt"))) : "";

        Assert.True((await RunAsync(service, "seq")).Succeeded);
        Assert.Equal("a,b,seq", Order());

        File.Delete(box.Path_("order.txt"));
        Assert.True((await RunAsync(service, "par")).Succeeded);
        // Together: b is not held back by a's sleep, and the task itself comes last.
        Assert.Equal("b,a,par", Order());

        File.Delete(box.Path_("order.txt"));
        var after = await RunAsync(service, "after");
        Assert.False(after.Succeeded);
        Assert.Contains("'bad'", after.Error, StringComparison.Ordinal);
        Assert.Equal("", Order());

        Assert.Contains("depends on it", (await RunAsync(service, "loop1")).Error ?? (await RunAsync(service, "loop2")).Error, StringComparison.Ordinal);
        Assert.Contains("does not exist", (await RunAsync(service, "ghost")).Error, StringComparison.Ordinal);
        _ = output;
    }

    [Fact]
    public async Task ABackgroundTaskIsReadyAtItsEndLineAndKeepsRunningUntilTerminated()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.WriteTasks("""
            { "version": "2.0.0", "tasks": [
              { "label": "watch", "type": "shell", "isBackground": true,
                "command": "echo BEGIN; sleep 0.3; echo \"w.c:2:3: error: first\"; echo READY; sleep 0.2; echo BEGIN; echo \"w.c:9:1: error: second\"; echo READY; sleep 300",
                "problemMatcher": { "base": "$gcc", "owner": "w", "background": { "beginsPattern": "^BEGIN", "endsPattern": "^READY" } } }
            ] }
            """);
        var (service, _, _, problems) = Make(box);
        using var _s = service;

        var started = DateTime.UtcNow;
        Assert.True(await service.RunTaskByLabelAsync("watch", TestContext.Current.CancellationToken).Within());
        // Ready means the end line was printed (after the 0.3 s sleep), not that the process started.
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(250));
        Assert.True(service.IsRunning("watch"));

        // The second cycle clears the first cycle's problem and reports its own.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !problems.Any(p => p.Problems.Any(x => x.Message == "second"))) await Task.Delay(50, TestContext.Current.CancellationToken);
        var secondCycle = problems.Last(p => p.Problems.Length > 0).Problems;
        Assert.Equal(["second"], secondCycle.Select(x => x.Message));
        Assert.Contains(problems, p => p.Problems.Any(x => x.Message == "first"));

        Assert.True(service.Terminate("watch"));
        var result = await service.RunningTask("watch")!.Completion.Within();
        Assert.True(result.Terminated);
        Assert.False(service.IsRunning("watch"));
    }

    [Fact]
    public async Task TerminatingATaskKillsTheProcessesItStarted()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        var pidFile = box.Path_("child.pid");
        box.WriteTasks($$"""
            { "version": "2.0.0", "tasks": [
              { "label": "tree", "type": "shell", "command": "sleep 300 & echo $! > child.pid; wait" }
            ] }
            """);
        var (service, _, _, _) = Make(box);
        using var _s = service;
        var catalog = await service.LoadAsync(TestContext.Current.CancellationToken);
        var run = service.Start(catalog.Find("tree")!, catalog);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !(File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0)) await Task.Delay(50, TestContext.Current.CancellationToken);
        var child = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Directory.Exists($"/proc/{child}"), "the child is running before the task is stopped");

        service.Terminate("tree");
        var result = await run.Completion.Within();
        Assert.True(result.Terminated);

        // The grandchild is a zombie or gone within a moment; /proc/<pid>/stat says Z for a zombie awaiting its reaper.
        var gone = false;
        for (var i = 0; i < 100 && !gone; i++)
        {
            try
            {
                gone = !Directory.Exists($"/proc/{child}") || File.ReadAllText($"/proc/{child}/stat").Contains(") Z ", StringComparison.Ordinal);
            }
            catch (IOException)
            {
                gone = true;
            }

            if (!gone) await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(gone, "the sleeping child outlived its task");
    }

    [Fact]
    public async Task InputsAreAskedForOnceAndAnUnansweredOneCancelsTheRun()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.WriteTasks("""
            { "version": "2.0.0",
              "inputs": [ { "id": "who", "type": "promptString", "description": "Who?" } ],
              "tasks": [ { "label": "hello", "type": "shell", "command": "echo hello ${input:who} ${input:who}" } ] }
            """);
        var (service, output, window, _) = Make(box);
        using var _s = service;

        window.Answers["who"] = "Ada";
        Assert.True((await RunAsync(service, "hello")).Succeeded);
        Assert.Contains("hello Ada Ada", output.Lines("hello"));

        window.Answers["who"] = null;
        var cancelled = await RunAsync(service, "hello");
        Assert.True(cancelled.Terminated);
    }

    [Fact]
    public async Task AVariableThatCannotBeWorkedOutStopsTheTaskWithAnExplanation()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.WriteTasks("""{ "version": "2.0.0", "tasks": [ { "label": "f", "type": "shell", "command": "cat ${file}" }, { "label": "dir", "type": "shell", "command": "true", "options": { "cwd": "nowhere" } }, { "label": "prog", "type": "process", "command": "no-such-program-here" } ] }""");
        var (service, _, _, _) = Make(box);
        using var _s = service;
        var noFile = await RunAsync(service, "f");
        Assert.Contains("${file}", noFile.Error, StringComparison.Ordinal);
        Assert.Contains("open a file", noFile.Error, StringComparison.Ordinal);
        Assert.Contains("does not exist", (await RunAsync(service, "dir")).Error, StringComparison.Ordinal);
        Assert.Contains("was not found", (await RunAsync(service, "prog")).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTaskByLabelSaysFalseForAMissingTaskAndForOneThatFails()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.WriteTasks("""{ "version": "2.0.0", "tasks": [ { "label": "good", "type": "shell", "command": "true" }, { "label": "bad", "type": "shell", "command": "exit 1" } ] }""");
        var (service, _, _, _) = Make(box);
        using var _s = service;
        Assert.True(await service.RunTaskByLabelAsync("good", TestContext.Current.CancellationToken).Within());
        Assert.False(await service.RunTaskByLabelAsync("bad", TestContext.Current.CancellationToken).Within());
        Assert.False(await service.RunTaskByLabelAsync("missing", TestContext.Current.CancellationToken).Within());
    }

    [Fact]
    public async Task RestartStopsARunningTaskAndStartsItAgain()
    {
        Assert.SkipUnless(Posix, "needs /bin/sh");
        using var box = new Sandbox();
        box.WriteTasks("""{ "version": "2.0.0", "tasks": [ { "label": "srv", "type": "shell", "isBackground": true, "command": "echo start >> runs.txt; sleep 300" } ] }""");
        var (service, _, _, _) = Make(box);
        using var _s = service;
        var catalog = await service.LoadAsync(TestContext.Current.CancellationToken);
        var first = service.Start(catalog.Find("srv")!, catalog);
        await first.Ready.Within();
        string[] Runs() => File.Exists(box.Path_("runs.txt")) ? File.ReadAllLines(box.Path_("runs.txt")) : [];
        var wait = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < wait && Runs().Length < 1) await Task.Delay(50, TestContext.Current.CancellationToken);
        var second = await service.RestartAsync("srv", TestContext.Current.CancellationToken).Within();
        Assert.NotNull(second);
        Assert.NotSame(first, second);
        Assert.True((await first.Completion.Within()).Terminated);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && Runs().Length < 2) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(2, Runs().Length);
        service.TerminateAll();
        await second!.Completion.Within();
    }
}
