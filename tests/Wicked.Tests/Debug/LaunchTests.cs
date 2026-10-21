using System.Text.Json;
using System.Text.Json.Nodes;
using Wicked.Core.Debug;

namespace Wicked.Tests.Debug;

/// <summary>launch.json as VS Code reads it, its variables, the generated starter, and starting what it names.</summary>
public sealed class LaunchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Sample = """
        {
          // A comment, as VS Code allows.
          "version": "0.2.0",
          "configurations": [
            {
              "name": "Launch app",
              "type": "coreclr",
              "request": "launch",
              "preLaunchTask": "build",
              "program": "${workspaceFolder}/bin/app.dll",
              "args": ["--port", "${input:port}",],   /* trailing comma above */
              "env": { "HOME_DIR": "${env:HOME_FOR_TEST}" },
              "linux": { "program": "${workspaceFolder}/bin/linux/app.dll" },
              "windows": { "program": "C:\\app\\app.dll" },
              "osx": { "program": "${workspaceFolder}/bin/mac/app.dll" },
            },
            { "name": "Attach", "type": "coreclr", "request": "attach", "processId": "${command:pickProcess}" },
            { "name": "Server", "type": "node", "request": "launch", "program": "${file}" },
          ],
          "compounds": [
            { "name": "Both", "configurations": ["Launch app", "Server"], "stopAll": true, "preLaunchTask": "build all" },
          ],
          "inputs": [
            { "id": "port", "type": "promptString", "description": "Port", "default": "8080" },
            { "id": "mode", "type": "pickString", "options": ["debug", { "value": "release", "label": "Release" }] },
          ],
        }
        """;

    [Fact]
    public void CommentsAndTrailingCommasAreAcceptedAndThePlatformBlockWins()
    {
        var file = LaunchFile.Parse(Sample, "linux");

        Assert.Equal(["Launch app", "Attach", "Server", "Both"], file.Names);
        var launch = file.Configuration("Launch app")!;
        Assert.Equal("coreclr", launch.Type);
        Assert.Equal("launch", launch.Request);
        Assert.Equal("build", launch.PreLaunchTask);
        Assert.Equal("${workspaceFolder}/bin/linux/app.dll", (string?)launch.Properties["program"]);
        Assert.Null(launch.Properties["linux"]);
        Assert.Equal("C:\\app\\app.dll", (string?)LaunchFile.Parse(Sample, "windows").Configuration("Launch app")!.Properties["program"]);
        Assert.Equal("${workspaceFolder}/bin/mac/app.dll", (string?)LaunchFile.Parse(Sample, "osx").Configuration("Launch app")!.Properties["program"]);
    }

    [Fact]
    public void CompoundsAndInputsAreKept()
    {
        var file = LaunchFile.Parse(Sample, "linux");

        var both = file.Compound("Both")!;
        Assert.Equal(["Launch app", "Server"], both.Configurations);
        Assert.True(both.StopAll);
        Assert.Equal("build all", both.PreLaunchTask);
        Assert.Equal("8080", file.Inputs.Single(i => i.Id == "port").Default);
        Assert.Equal(["debug", "release"], file.Inputs.Single(i => i.Id == "mode").Options);
    }

    [Fact]
    public void AnInvalidFileSaysWhichLineAndAConfigurationWithoutATypeSaysWhich()
    {
        var broken = Assert.Throws<LaunchException>(() => LaunchFile.Parse("{\n  \"configurations\": [\n    { \"name\": \n  ]\n}"));
        var untyped = Assert.Throws<LaunchException>(() => LaunchFile.Parse("""{"configurations":[{"name":"Oops","request":"launch"}]}"""));
        var odd = Assert.Throws<LaunchException>(() => LaunchFile.Parse("""{"configurations":[{"name":"Odd","type":"x","request":"run"}]}"""));

        Assert.Contains("launch.json could not be read", broken.Message);
        Assert.Contains("line", broken.Message);
        Assert.Contains("\"Oops\"", untyped.Message);
        Assert.Contains("\"run\"", odd.Message);
        Assert.Same(LaunchFile.Empty, LaunchFile.Parse("[]"));
    }

    private static LaunchContext Context(Action<Dictionary<string, string>>? env = null, Func<LaunchInput, CancellationToken, Task<string?>>? ask = null)
    {
        var variables = new Dictionary<string, string> { ["HOME_FOR_TEST"] = "/home/me", ["EMPTY"] = "" };
        env?.Invoke(variables);
        return new LaunchContext
        {
            WorkspaceFolder = "/work/app",
            ActiveFile = "/work/app/src/Main.cs",
            SelectedText = "selected",
            LineNumber = 12,
            Env = n => variables.GetValueOrDefault(n),
            UserHome = "/home/me",
            Cwd = "/work",
            Ask = ask,
            Config = n => n == "my.setting" ? "from settings" : null,
        };
    }

    [Theory]
    [InlineData("${workspaceFolder}/bin", "/work/app/bin")]
    [InlineData("${workspaceFolderBasename}", "app")]
    [InlineData("${file}", "/work/app/src/Main.cs")]
    [InlineData("${fileBasename}", "Main.cs")]
    [InlineData("${fileBasenameNoExtension}", "Main")]
    [InlineData("${fileExtname}", ".cs")]
    [InlineData("${fileDirname}", "/work/app/src")]
    [InlineData("${fileDirnameBasename}", "src")]
    [InlineData("${relativeFile}", "src/Main.cs")]
    [InlineData("${relativeFileDirname}", "src")]
    [InlineData("${lineNumber}", "12")]
    [InlineData("${selectedText}", "selected")]
    [InlineData("${cwd}", "/work")]
    [InlineData("${userHome}/x", "/home/me/x")]
    [InlineData("${env:HOME_FOR_TEST}/.cache", "/home/me/.cache")]
    [InlineData("[${env:NOT_SET}]", "[]")]
    [InlineData("${config:my.setting}", "from settings")]
    [InlineData("${workspaceFolder}${/}x${pathSeparator}y", "/work/app/x/y")]
    [InlineData("no variables here", "no variables here")]
    [InlineData("${workspaceFolder} and ${fileBasename}", "/work/app and Main.cs")]
    public async Task EveryVariableVSCodeDefinesIsFilledIn(string text, string expected)
    {
        var actual = await LaunchVariables.ResolveAsync(text, Context(), Ct);
        // Paths come back with the platform's separator, as VS Code gives them; the expectations are written with '/'.
        Assert.Equal(expected, OperatingSystem.IsWindows() ? actual.Replace('\\', '/') : actual);
    }

    [Fact]
    public async Task AVariableThatCannotBeFilledInIsAnErrorThatNamesIt()
    {
        var noFile = Context() with { ActiveFile = null };

        var file = await Assert.ThrowsAsync<LaunchException>(() => LaunchVariables.ResolveAsync("${file}", noFile, Ct));
        var unknown = await Assert.ThrowsAsync<LaunchException>(() => LaunchVariables.ResolveAsync("${nonsense}", Context(), Ct));
        var setting = await Assert.ThrowsAsync<LaunchException>(() => LaunchVariables.ResolveAsync("${config:missing.setting}", Context(), Ct));

        Assert.Contains("${file}", file.Message);
        Assert.Contains("open in the editor", file.Message);
        Assert.Contains("${nonsense}", unknown.Message);
        Assert.Contains("missing.setting", setting.Message);
    }

    [Fact]
    public async Task ResolvingWalksTheWholeConfigurationAndLeavesNumbersAndNamesAlone()
    {
        var file = LaunchFile.Parse(Sample, "linux");
        var context = Context(ask: (input, _) => Task.FromResult<string?>(input.Id == "port" ? "9090" : "x")) with { Inputs = file.Inputs };

        var resolved = await LaunchVariables.ResolveAsync(file.Configuration("Launch app")!.Properties, context, Ct);

        Assert.Equal("/work/app/bin/linux/app.dll", (string?)resolved["program"]);
        Assert.Equal(["--port", "9090"], resolved["args"]!.AsArray().Select(a => (string?)a));
        Assert.Equal("/home/me", (string?)resolved["env"]!["HOME_DIR"]);
        Assert.Equal("coreclr", (string?)resolved["type"]);
        Assert.Contains("${input:port}", file.Configuration("Launch app")!.Properties.ToJsonString()); // the original is untouched
    }

    [Fact]
    public async Task AnInputIsAskedOncePerStartEvenWhenUsedTwiceAndCancellingStopsTheStart()
    {
        var asked = new List<string>();
        var context = Context(ask: (input, _) =>
        {
            asked.Add(input.Id);
            return Task.FromResult<string?>("v");
        }) with { Inputs = [new LaunchInput("a", "promptString", null, null, [], false)] };

        var value = await LaunchVariables.ResolveAsync("${input:a}-${input:a}", context, Ct);
        var cancelled = context with { Ask = (_, _) => Task.FromResult<string?>(null) };
        var ex = await Assert.ThrowsAsync<LaunchException>(() => LaunchVariables.ResolveAsync("${input:a}", cancelled, Ct));

        Assert.Equal("v-v", value);
        Assert.Equal(["a"], asked);
        Assert.Contains("cancelled", ex.Message);
        await Assert.ThrowsAsync<LaunchException>(() => LaunchVariables.ResolveAsync("${input:undeclared}", context, Ct));
    }

    [Fact]
    public async Task ACommandVariableRunsTheCommandAndWithoutOneItIsAnError()
    {
        var context = Context() with { Command = (name, _) => Task.FromResult<string?>(name == "pickProcess" ? "4711" : null) };

        Assert.Equal("4711", await LaunchVariables.ResolveAsync("${command:pickProcess}", context, Ct));
        await Assert.ThrowsAsync<LaunchException>(() => LaunchVariables.ResolveAsync("${command:other}", context, Ct));
        await Assert.ThrowsAsync<LaunchException>(() => LaunchVariables.ResolveAsync("${command:pickProcess}", Context(), Ct));
    }

    // ── The starter ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryStarterIsAValidLaunchFileWhoseConfigurationsHaveAnAdapterInTheCatalog()
    {
        using var profile = new TempProfile();
        foreach (var language in LaunchStarter.Languages)
        {
            var starter = await LaunchStarter.CreateAsync(profile.Root, language, Ct);
            var file = LaunchFile.Parse(starter.Text);

            Assert.NotEmpty(file.Configurations);
            Assert.NotNull(starter.Adapter);
            Assert.All(file.Configurations, c => Assert.Contains(starter.Adapter!, DebugAdapterCatalog.ForType(c.Type)));
            Assert.Contains("\"version\": \"0.2.0\"", starter.Text);
        }
    }

    [Fact]
    public async Task TheCSharpStarterPointsAtTheProjectsOwnOutputWithItsFrameworkAndAssemblyName()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(Path.Combine(profile.Root, "src", "Tool"));
        File.WriteAllText(Path.Combine(profile.Root, "src", "Tool", "Tool.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><AssemblyName>tool-cli</AssemblyName></PropertyGroup></Project>");

        var starter = await LaunchStarter.CreateAsync(profile.Root, "csharp", Ct);

        var launch = LaunchFile.Parse(starter.Text).Configuration(".NET: Launch")!;
        Assert.Equal("${workspaceFolder}/src/Tool/bin/Debug/net9.0/tool-cli.dll", (string?)launch.Properties["program"]);
        Assert.Equal("${workspaceFolder}/src/Tool", (string?)launch.Properties["cwd"]);
        Assert.Equal("netcoredbg", starter.Adapter!.Id);
    }

    [Fact]
    public async Task TheRustStarterNamesTheCrateAndTheLanguageIsDetectedFromTheFileOrTheFolder()
    {
        using var profile = new TempProfile();
        File.WriteAllText(Path.Combine(profile.Root, "Cargo.toml"), "[package]\nname = \"my-crate\"\nversion = \"0.1.0\"\n");

        var starter = await LaunchStarter.CreateAsync(profile.Root, "rust", Ct);

        Assert.Equal("${workspaceFolder}/target/debug/my-crate", (string?)LaunchFile.Parse(starter.Text).Configurations[0].Properties["program"]);
        Assert.Equal("rust", await LaunchStarter.DetectAsync(profile.Root, null, Ct));
        Assert.Equal("python", await LaunchStarter.DetectAsync(profile.Root, "python", Ct));
        Assert.Equal("typescript", await LaunchStarter.DetectAsync(profile.Root, "typescriptreact", Ct));
        using var empty = new TempProfile();
        Assert.Null(await LaunchStarter.DetectAsync(empty.Root, "markdown", Ct));
    }

    // ── Starting ─────────────────────────────────────────────────────────────────────────────────

    private sealed class Recorder : IDebugAdapterProvider
    {
        public List<(string Type, JsonElement Configuration)> Resolved { get; } = [];

        public Task<AdapterEndpoint> ResolveAsync(string type, JsonElement configuration, CancellationToken ct)
        {
            Resolved.Add((type, configuration.Clone()));
            throw new DebugAdapterMissingException(type, null, "stop here: the test only looks at what was resolved");
        }
    }

    [Fact]
    public async Task AConfigurationReachesTheAdapterResolvedWithoutTheClientsOwnProperties()
    {
        var recorder = new Recorder();
        var launcher = new DebugLauncher(new DebugManager(new BreakpointStore(), recorder)) { RunTask = (_, _) => Task.FromResult<string?>(null) };
        var file = LaunchFile.Parse(Sample, "linux");
        var context = Context(ask: (_, _) => Task.FromResult<string?>("7000"));

        await Assert.ThrowsAsync<DebugAdapterMissingException>(() => launcher.StartAsync(file, "Launch app", context, Ct));

        var (type, configuration) = Assert.Single(recorder.Resolved);
        Assert.Equal("coreclr", type);
        Assert.Equal("/work/app/bin/linux/app.dll", configuration.GetProperty("program").GetString());
        Assert.Equal("7000", configuration.GetProperty("args")[1].GetString());
        Assert.False(configuration.TryGetProperty("preLaunchTask", out _));
        Assert.Equal("Launch app", configuration.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ThePreLaunchTaskRunsFirstWithItsLabelAndAFailureStopsTheStart()
    {
        var recorder = new Recorder();
        var order = new List<string>();
        var launcher = new DebugLauncher(new DebugManager(new BreakpointStore(), recorder))
        {
            RunTask = (label, _) =>
            {
                order.Add("task " + label);
                return Task.FromResult<string?>(label == "build" ? "The build failed with 3 errors" : null);
            },
        };
        var file = LaunchFile.Parse(Sample, "linux");

        var ex = await Assert.ThrowsAsync<LaunchException>(() => launcher.StartAsync(file, "Launch app", Context(ask: (_, _) => Task.FromResult<string?>("1")), Ct));

        Assert.Equal("The build failed with 3 errors", ex.Message);
        Assert.Equal(["task build"], order);
        Assert.Empty(recorder.Resolved);
    }

    [Fact]
    public async Task WithoutATaskRunnerAConfigurationThatNeedsOneSaysSo()
    {
        var launcher = new DebugLauncher(new DebugManager(new BreakpointStore(), new Recorder()));
        var file = LaunchFile.Parse(Sample, "linux");

        var ex = await Assert.ThrowsAsync<LaunchException>(() => launcher.StartAsync(file, "Launch app", Context(ask: (_, _) => Task.FromResult<string?>("1")), Ct));

        Assert.Contains("\"build\"", ex.Message);
        Assert.Contains("no task runner", ex.Message);
    }

    [Fact]
    public async Task ACompoundStartsEveryMemberAfterItsOwnTaskAndAnUnknownNameIsAnError()
    {
        var provider = new FakeAdapterProvider();
        var manager = new DebugManager(new BreakpointStore(), provider);
        var tasks = new List<string>();
        var launcher = new DebugLauncher(manager) { RunTask = (label, _) => { tasks.Add(label); return Task.FromResult<string?>(null); } };
        using var profile = new TempProfile();
        var program = System.Text.Json.JsonEncodedText.Encode(DebugKit.Program(profile));
        var text = $$"""
            { "configurations": [
                { "name": "A", "type": "fake", "request": "launch", "program": "{{program}}", "hangAtEnd": true },
                { "name": "B", "type": "fake", "request": "launch", "program": "{{program}}", "hangAtEnd": true } ],
              "compounds": [ { "name": "Both", "configurations": ["A", "B"], "stopAll": true, "preLaunchTask": "prepare" } ] }
            """;
        var file = LaunchFile.Parse(text);

        var sessions = await launcher.StartAsync(file, "Both", Context(), Ct);

        Assert.Equal(["A", "B"], sessions.Select(s => s.Name));
        Assert.Equal(["prepare"], tasks);
        Assert.Equal(2, manager.Sessions.Count);

        await sessions[0].StopAsync(Ct);
        await DebugKit.WaitAsync(() => sessions[1].State == DebugSessionState.Ended, "stopAll to end the other member");
        await Assert.ThrowsAsync<LaunchException>(() => launcher.StartAsync(file, "Nothing", Context(), Ct));
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task ThePostDebugTaskRunsWhenTheSessionEnds()
    {
        var provider = new FakeAdapterProvider();
        var manager = new DebugManager(new BreakpointStore(), provider);
        var tasks = new List<string>();
        var launcher = new DebugLauncher(manager) { RunTask = (label, _) => { lock (tasks) tasks.Add(label); return Task.FromResult<string?>(null); } };
        using var profile = new TempProfile();
        var program = System.Text.Json.JsonEncodedText.Encode(DebugKit.Program(profile));
        var file = LaunchFile.Parse($$"""{ "configurations": [ { "name": "A", "type": "fake", "request": "launch", "program": "{{program}}", "hangAtEnd": true, "postDebugTask": "clean" } ] }""");

        var session = (await launcher.StartAsync(file, "A", Context(), Ct)).Single();
        await session.StopAsync(Ct);

        await DebugKit.WaitAsync(() => { lock (tasks) return tasks.Contains("clean"); }, "the post-debug task");
        await manager.DisposeAsync();
    }

    [Fact]
    public void AnAttachConfigurationIsSentAsAnAttachRequest()
    {
        var configuration = LaunchFile.Parse(Sample, "linux").Configuration("Attach")!;

        var request = DebugLauncher.BuildRequest(configuration, JsonNode.Parse("""{"name":"Attach","type":"coreclr","request":"attach","processId":"4711","presentation":{"order":1}}""")!.AsObject());

        Assert.Equal("attach", request.Request);
        Assert.Equal("4711", request.Arguments.GetProperty("processId").GetString());
        Assert.False(request.Arguments.TryGetProperty("presentation", out _));
    }
}
