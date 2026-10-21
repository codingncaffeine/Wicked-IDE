using System.Diagnostics;
using System.Text.Json.Nodes;
using Wicked.Core.Debug;
using Wicked.Core.Http;

namespace Wicked.Tests.Debug;

/// <summary>
/// The whole path against a real debug adapter: a tiny program built in the repository's gitignored <c>scratch/real-debug</c>,
/// run under netcoredbg (or CodeLLDB, when <c>WICKED_REAL_DEBUG_ADAPTER=codelldb</c> or only that is installed), stopped at a
/// breakpoint, a local variable read, stepped, run to its end. The adapter is the one on the PATH or in
/// <c>scratch/real-debug/adapters</c>, and is installed there from its GitHub release when neither has it. Skipped unless
/// <c>WICKED_REAL_DEBUG=1</c>.
/// </summary>
public sealed class RealDebuggerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Say(string line) => TestContext.Current.TestOutputHelper?.WriteLine(line);

    private static string ScratchFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wicked.slnx"))) return Path.Combine(dir.FullName, "scratch");
        }

        throw new InvalidOperationException("Wicked.slnx not found above the test binaries");
    }

    private const string CSharp = """
        var total = 0;
        for (var i = 1; i <= 3; i++)
        {
            total += i * 10;
        }

        System.Console.WriteLine(total);
        """;

    private const string Js = """
        let total = 0;
        for (let i = 1; i <= 3; i++) {
            total += i * 10;
        }
        console.log(total);
        """;

    private const string C = """
        #include <stdio.h>
        int main(void) {
            int total = 0;
            for (int i = 1; i <= 3; i++) {
                total += i * 10;
            }
            printf("%d\n", total);
            return 0;
        }
        """;

    /// <summary>Runs a build tool with the home folders of the build in scratch, so nothing lands in the user's.</summary>
    private static async Task RunAsync(string program, string[] args, string cwd, string home)
    {
        var info = new ProcessStartInfo(program) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["HOME"] = home;
        info.Environment["DOTNET_CLI_HOME"] = home;
        info.Environment["NUGET_PACKAGES"] = Path.Combine(home, "nuget");
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var errors = process.StandardError.ReadToEndAsync(Ct);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(limit.Token);
        Say(await output);
        Assert.True(process.ExitCode == 0, $"{program} {string.Join(' ', args)} failed:\n{await errors}\n{await output}");
    }

    [Fact(Timeout = 600_000)]
    public async Task ARealAdapterStopsATinyProgramAtABreakpointAndReadsALocalVariable()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("WICKED_REAL_DEBUG") == "1", "set WICKED_REAL_DEBUG=1 to run a program under a real debug adapter (it is installed into scratch/real-debug when missing)");
        var scratch = Path.Combine(ScratchFolder(), "real-debug");
        var home = Path.Combine(scratch, "home");
        Directory.CreateDirectory(home);
        var locator = new DebugAdapterLocator(Path.Combine(scratch, "adapters"));
        var wanted = Environment.GetEnvironmentVariable("WICKED_REAL_DEBUG_ADAPTER");
        var spec = (wanted is null ? DebugAdapterCatalog.ById("netcoredbg") : DebugAdapterCatalog.ById(wanted))!;
        var dotnet = Path.Combine(Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")), "dotnet");
        if (!File.Exists(dotnet)) dotnet = "dotnet";

        if (locator.Find(spec) is null)
        {
            using var http = WickedHttp.CreateClient(offline: () => false);
            Say($"installing {spec.Name}…");
            await new DebugAdapterInstaller(http, Path.Combine(scratch, "adapters")).InstallAsync(spec, new Progress<string>(Say), Ct);
        }

        var command = locator.Find(spec)!;
        Say($"adapter: {command.Program} {string.Join(' ', command.Arguments)}");

        // The program, built here with debug information.
        string program, source;
        int breakLine;
        string type;
        if (spec.Id == "netcoredbg")
        {
            var folder = Path.Combine(scratch, "program-cs");
            Directory.CreateDirectory(folder);
            source = Path.Combine(folder, "Program.cs");
            await File.WriteAllTextAsync(source, CSharp, Ct);
            await File.WriteAllTextAsync(Path.Combine(folder, "tiny.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Optimize>false</Optimize><DebugType>portable</DebugType><UseAppHost>false</UseAppHost><ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>", Ct);
            await RunAsync(dotnet, ["build", "-c", "Debug", "-o", "out", "--nologo", "-v", "q"], folder, home);
            program = Path.Combine(folder, "out", "tiny.dll");
            breakLine = 4;
            type = "coreclr";
        }
        else if (spec.Id == "js-debug")
        {
            var folder = Path.Combine(scratch, "program-js");
            Directory.CreateDirectory(folder);
            source = Path.Combine(folder, "tiny.js");
            await File.WriteAllTextAsync(source, Js, Ct);
            program = source;
            breakLine = 3;
            type = "pwa-node";
        }
        else
        {
            var folder = Path.Combine(scratch, "program-c");
            Directory.CreateDirectory(folder);
            source = Path.Combine(folder, "tiny.c");
            await File.WriteAllTextAsync(source, C, Ct);
            program = Path.Combine(folder, "tiny");
            await RunAsync("cc", ["-g", "-O0", "-o", program, source], folder, home);
            breakLine = 5;
            type = "lldb";
        }

        var store = new BreakpointStore();
        var bp = store.Add(source, breakLine);
        var environment = new Dictionary<string, string>(command.Environment) { ["HOME"] = home, ["DOTNET_CLI_HOME"] = home };
        var endpoint = new ProcessAdapterEndpoint(new AdapterCommand(command.Program, command.Arguments, environment, command.Transport), Path.GetDirectoryName(source)!);
        await using var manager = new DebugManager(store, new FixedProvider(endpoint));
        var arguments = new JsonObject { ["name"] = "Real", ["type"] = type, ["request"] = "launch", ["program"] = program, ["cwd"] = Path.GetDirectoryName(program), ["stopAtEntry"] = false };
        var request = new DebugStartRequest("Real", type, "launch", System.Text.Json.JsonSerializer.SerializeToElement(arguments));

        var output = new System.Text.StringBuilder();
        manager.Output += (_, o) =>
        {
            lock (output) output.Append(o.Text);
        };
        var started = await manager.StartAsync(request, null, Ct);
        // js-debug asks for a second session for the program itself; the one that stops is the one to look at.
        await DebugKit.WaitAsync(() => manager.Sessions.Any(s => s.State == DebugSessionState.Stopped) || started.State == DebugSessionState.Ended, "the first stop", 90);
        Say($"sessions: {string.Join(", ", manager.Sessions.Select(s => s.Name + " " + s.State))}");
        var session = manager.Sessions.First(s => s.State == DebugSessionState.Stopped);
        Say($"stopped: {session.Stop?.Reason}");

        var threadId = session.StoppedThreadId ?? (await session.RefreshThreadsAsync(Ct))[0].Id;
        var (frames, _) = await session.StackTraceAsync(threadId, 0, 20, Ct);
        Say("frames: " + string.Join(" | ", frames.Select(f => $"{f.Name} {Path.GetFileName(f.Source?.Path)}:{f.Line}")));
        var top = frames[0];
        Assert.Equal(breakLine, top.Line);
        Assert.True(store.ForFile(source).Count == 1 && session.ResultFor(bp.Id)?.Verified == true, "the breakpoint was not verified");

        async Task<Dictionary<string, string>> LocalsAsync()
        {
            var scopes = await session.ScopesAsync(top.Id, Ct);
            var all = new Dictionary<string, string>();
            foreach (var scope in scopes.Where(s => !s.Expensive && s.Name is not ("Global" or "Closure" or "Registers")))
            {
                foreach (var v in await session.VariablesAsync(scope.VariablesReference, ct: Ct)) all[v.Name] = v.Value;
            }

            Say("locals: " + string.Join(", ", all.Select(kv => $"{kv.Key}={kv.Value}")));
            return all;
        }

        var first = await LocalsAsync();
        Assert.Equal("1", first["i"]);
        Assert.Equal("0", first["total"]);
        var viaEvaluate = await session.EvaluateAsync("total + i", top.Id, "watch", Ct);
        Assert.Equal("1", viaEvaluate.Result);

        await session.StepOverAsync(threadId, Ct);
        await DebugKit.WaitAsync(() => session.State == DebugSessionState.Stopped && session.Stop?.Reason == "step", "the step", 30);
        (frames, _) = await session.StackTraceAsync(threadId, 0, 20, Ct);
        top = frames[0];
        var second = await LocalsAsync();
        Assert.Equal("10", second["total"]);

        await session.ContinueAsync(threadId, Ct);
        await DebugKit.WaitAsync(() => session.State == DebugSessionState.Stopped, "the second pass of the loop", 30);
        (frames, _) = await session.StackTraceAsync(threadId, 0, 20, Ct);
        top = frames[0];
        var third = await LocalsAsync();
        Assert.Equal("2", third["i"]);

        store.Remove(bp.Id);
        await DebugKit.WaitAsync(() => store.ForFile(source).Count == 0, "the breakpoint removal");
        await Task.Delay(500, Ct);
        await session.ContinueAsync(threadId, Ct);
        await DebugKit.WaitAsync(() => manager.Sessions.Count == 0, "the program to end", 60);
        Say("output: " + output.ToString().Trim());
        lock (output) Assert.Contains("60", output.ToString());
    }

    private sealed class FixedProvider(AdapterEndpoint endpoint) : IDebugAdapterProvider
    {
        public Task<AdapterEndpoint> ResolveAsync(string type, System.Text.Json.JsonElement configuration, CancellationToken ct) => Task.FromResult(endpoint);
    }
}
