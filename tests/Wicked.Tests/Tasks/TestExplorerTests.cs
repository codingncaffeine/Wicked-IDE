using Wicked.Core.Testing;
using TestResult = Wicked.Core.Testing.TestResult;

namespace Wicked.Tests.Tasks;

/// <summary>The explorer against a real xunit project built in a scratch folder, and against recorded tool output replayed by stand-in programs.</summary>
public sealed class TestExplorerTests
{
    private static TestService Service(Sandbox box, ToolEnv? tools = null, IReadOnlyList<ITestAdapter>? adapters = null)
    {
        var service = new TestService(null, adapters, tools, box.Path_("work")) { WorkspaceFolder = box.Root };
        return service;
    }

    internal static void PlantTinyProject(Sandbox box)
    {
        // The folder is under the checkout; an empty props file keeps the checkout's own build settings out of it.
        box.Write("Directory.Build.props", "<Project />");
        box.Write("Directory.Build.targets", "<Project />");
        box.Write(Path.Combine("Tiny", "Tiny.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <OutputType>Exe</OutputType>
                <IsPackable>false</IsPackable>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
                <PackageReference Include="xunit.v3" Version="3.2.2" />
                <PackageReference Include="xunit.runner.visualstudio" Version="3.1.5" />
              </ItemGroup>
              <ItemGroup><Using Include="Xunit" /></ItemGroup>
            </Project>
            """);
        box.Write(Path.Combine("Tiny", "MathTests.cs"), "namespace Tiny.Tests;\n\npublic class MathTests\n{\n    [Fact]\n    public void Adds() => Assert.Equal(4, 2 + 2);\n\n    [Fact]\n    public void Fails() => Assert.Equal(5, 2 + 2);\n\n    [Theory]\n    [InlineData(1)]\n    [InlineData(2)]\n    public void Positive(int x) => Assert.True(x > 0);\n}\n");
        box.Write(Path.Combine("Tiny", "OtherTests.cs"), "namespace Tiny.Tests.Deep;\n\npublic class OtherTests\n{\n    [Fact]\n    public void Works() { }\n\n    [Fact(Skip = \"not now\")]\n    public void Skipped() { }\n}\n");
    }

    private static TestRow Find(TestService service, string name) => service.Flatten().First(r => r.Name == name);

    [Fact]
    public async Task ARealXunitProjectIsListedRunAndItsFailureShownWithMessageAndLocation()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the build of the scratch project is exercised on Linux");
        using var box = new Sandbox();
        PlantTinyProject(box);
        using var service = Service(box, adapters: [new DotnetTestAdapter()]);
        var changes = 0;
        service.Changed += () => Interlocked.Increment(ref changes);

        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within(180);
        var rows = service.Flatten();
        Assert.Equal(["Tiny.Tests", "MathTests", "Adds", "Fails", "Positive(x: 1)", "Positive(x: 2)", "Tiny.Tests.Deep", "OtherTests", "Skipped", "Works"], rows.Select(r => r.Name).Skip(1));
        Assert.Equal(TestKind.Project, rows[0].Kind);
        Assert.All(rows.Where(r => r.Kind == TestKind.Test), r => Assert.Equal(TestState.Unknown, r.State));

        // Run one test: only it changes.
        await service.RunAsync([Find(service, "Adds").Id], TestContext.Current.CancellationToken).Within(180);
        Assert.Equal(TestState.Passed, Find(service, "Adds").State);
        Assert.Equal(TestState.Unknown, Find(service, "Fails").State);
        Assert.NotNull(Find(service, "Adds").Duration);

        // Run everything: the failing test shows its message and where it failed; the skipped one is skipped.
        await service.RunAllAsync(TestContext.Current.CancellationToken).Within(180);
        var fails = Find(service, "Fails");
        Assert.Equal(TestState.Failed, fails.State);
        Assert.Contains("Expected: 5", fails.Message, StringComparison.Ordinal);
        Assert.Contains("Actual:   4", fails.Message, StringComparison.Ordinal);
        var place = TestLocations.Find(fails.Stack!.Split('\n')[0], [box.Root]);
        Assert.Equal(box.Path_(Path.Combine("Tiny", "MathTests.cs")), place!.File);
        Assert.Equal(9, place.Line);
        Assert.Equal(TestState.Skipped, Find(service, "Skipped").State);
        Assert.Equal(TestState.Passed, Find(service, "Positive(x: 2)").State);
        Assert.Equal((6, 4, 1, 1), service.Counts());
        Assert.Equal(TestState.Failed, service.Flatten()[0].State);
        Assert.True(changes > 3);

        // Run failed: only the failing test runs, and the others keep what they had.
        await service.RunFailedAsync(TestContext.Current.CancellationToken).Within(180);
        Assert.Equal(TestState.Failed, Find(service, "Fails").State);
        Assert.Equal(TestState.Passed, Find(service, "Adds").State);

        // A refresh keeps the results of tests that are still there.
        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within(180);
        Assert.Equal(TestState.Failed, Find(service, "Fails").State);

        // Go to test: the class's file and the method's line.
        var located = await service.LocateAsync(Find(service, "Fails").Id, TestContext.Current.CancellationToken).Within();
        Assert.Equal((box.Path_(Path.Combine("Tiny", "MathTests.cs")), 9), located);
    }

    [Fact]
    public async Task AProjectThatDoesNotBuildShowsWhyInsteadOfAnEmptyTree()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the build of the scratch project is exercised on Linux");
        using var box = new Sandbox();
        PlantTinyProject(box);
        box.Write(Path.Combine("Tiny", "Broken.cs"), "namespace Tiny.Tests;\npublic class Broken { void M() { int x = ; } }\n");
        using var service = Service(box, adapters: [new DotnetTestAdapter()]);
        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within(180);
        var project = Assert.Single(service.Flatten());
        Assert.Equal(TestState.Failed, project.State);
        Assert.Contains("error CS", project.Message, StringComparison.Ordinal);
    }

    // ── Stand-in tools replaying recorded output ─────────────────────────────────────────────────

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string PlantTool(Sandbox box, string name, string script)
    {
        var dir = box.Path_("fakebin");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + script);
        MakeExecutable(path);
        return dir;
    }

    private static ToolEnv Tools(Sandbox box, string fakeBin) => new()
    {
        Path = fakeBin + Path.PathSeparator + "/usr/bin" + Path.PathSeparator + "/bin",
        Variables = new Dictionary<string, string> { ["FAKE_LOG"] = box.Path_("args.log") },
    };

    private static string[] Logged(Sandbox box) => File.Exists(box.Path_("args.log")) ? File.ReadAllLines(box.Path_("args.log")) : [];

    [Fact]
    public async Task CargoIsListedAndOnlyThePickedTestsAreRunByExactName()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs /bin/sh");
        using var box = new Sandbox();
        box.Write("Cargo.toml", "[package]\nname = \"demo\"\n");
        box.Write(Path.Combine("src", "lib.rs"), "#[test]\nfn subtracts() {}\n");
        box.Write("list.txt", "     Running unittests src/lib.rs (target/debug/deps/demo-0123)\ntests::adds: test\ntests::subtracts: test\n\n2 tests, 0 benchmarks\n");
        box.Write("run.txt", "running 1 test\ntest tests::subtracts ... FAILED\n\nfailures:\n\n---- tests::subtracts stdout ----\n\nthread 'tests::subtracts' panicked at src/lib.rs:2:9:\nboom\n\n\nfailures:\n    tests::subtracts\n\ntest result: FAILED. 0 passed; 1 failed\n");
        var bin = PlantTool(box, "cargo", $"echo \"$@\" >> \"$FAKE_LOG\"\ncase \"$*\" in *--list*) cat '{box.Path_("list.txt")}';; *) cat '{box.Path_("run.txt")}'; exit 101;; esac\n");
        using var service = Service(box, Tools(box, bin), [new CargoTestAdapter()]);

        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within();
        Assert.Equal([Path.GetFileName(box.Root), "unittests src/lib.rs", "tests", "adds", "subtracts"], service.Flatten().Select(r => r.Name));
        await service.RunAsync([Find(service, "subtracts").Id], TestContext.Current.CancellationToken).Within();

        var run = Logged(box).Last();
        Assert.Contains("--exact tests::subtracts", run, StringComparison.Ordinal);
        Assert.DoesNotContain("tests::adds", run, StringComparison.Ordinal);
        var failed = Find(service, "subtracts");
        Assert.Equal((TestState.Failed, "boom"), (failed.State, failed.Message));
        Assert.Contains("src/lib.rs:2:9", failed.Stack, StringComparison.Ordinal);
        Assert.Equal(TestState.Unknown, Find(service, "adds").State);
    }

    [Fact]
    public async Task GoIsListedPerPackageAndARunNamesTheTestsInARunPattern()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs /bin/sh");
        using var box = new Sandbox();
        box.Write("go.mod", "module example.com/demo\n");
        box.Write("list.txt", "TestAdd\nTestSub\nok  \texample.com/demo\t0.002s\n");
        box.Write("run.txt", "{\"Action\":\"output\",\"Test\":\"TestSub\",\"Output\":\"    calc_test.go:21: got 1\\n\"}\n{\"Action\":\"fail\",\"Test\":\"TestSub\",\"Elapsed\":0.25}\n");
        var bin = PlantTool(box, "go", $"echo \"$@\" >> \"$FAKE_LOG\"\ncase \"$*\" in *-list*) cat '{box.Path_("list.txt")}';; *) cat '{box.Path_("run.txt")}'; exit 1;; esac\n");
        using var service = Service(box, Tools(box, bin), [new GoTestAdapter()]);

        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within();
        await service.RunAsync([Find(service, "TestSub").Id], TestContext.Current.CancellationToken).Within();

        Assert.Contains("-run ^(TestSub)$", Logged(box).Last(), StringComparison.Ordinal);
        var sub = Find(service, "TestSub");
        Assert.Equal((TestState.Failed, TimeSpan.FromSeconds(0.25), "calc_test.go:21: got 1"), (sub.State, sub.Duration, sub.Message));
    }

    [Fact]
    public async Task PytestRunsByNodeIdAndReadsItsJUnitFileWhenPytestIsInstalled()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs /bin/sh");
        using var box = new Sandbox();
        box.Write("pytest.ini", "[pytest]\n");
        box.Write("collect.txt", "tests/test_math.py::test_add\ntests/test_math.py::test_sub\n\n2 tests collected in 0.01s\n");
        box.Write("junit.xml", "<testsuites><testsuite><testcase classname=\"tests.test_math\" name=\"test_sub\" time=\"0.5\"><failure message=\"assert 1 == 2\">tests/test_math.py:7: AssertionError</failure></testcase></testsuite></testsuites>");
        var bin = PlantTool(box, "pytest", $"echo \"$@\" >> \"$FAKE_LOG\"\ncase \"$*\" in *--collect-only*) cat '{box.Path_("collect.txt")}';; *) for a in \"$@\"; do case \"$a\" in --junitxml=*) cp '{box.Path_("junit.xml")}' \"${{a#--junitxml=}}\";; esac; done; exit 1;; esac\n");
        using var service = Service(box, Tools(box, bin), [new PytestAdapter()]);

        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within();
        await service.RunAsync([Find(service, "test_sub").Id], TestContext.Current.CancellationToken).Within();
        Assert.Contains("tests/test_math.py::test_sub", Logged(box).Last(), StringComparison.Ordinal);
        Assert.DoesNotContain("test_add", Logged(box).Last(), StringComparison.Ordinal);
        var sub = Find(service, "test_sub");
        Assert.Equal((TestState.Failed, "assert 1 == 2", TimeSpan.FromSeconds(0.5)), (sub.State, sub.Message, sub.Duration));

        // Without pytest on the path there is simply nothing to list (no failure to explain).
        using var without = Service(box, new ToolEnv { Path = box.Path_("empty") }, [new PytestAdapter()]);
        await without.DiscoverAsync(TestContext.Current.CancellationToken).Within();
        Assert.False(without.HasProjects);
    }

    [Fact]
    public async Task JestFilesAreListedAndTheCasesAppearAfterARun()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "needs /bin/sh");
        using var box = new Sandbox();
        box.Write("package.json", "{ \"devDependencies\": { \"jest\": \"^29\" } }");
        var test = box.Write(Path.Combine("src", "sum.test.js"), "x");
        box.Write("report.json", "{\"testResults\":[{\"name\":\"" + test.Replace("\\", "\\\\", StringComparison.Ordinal) + "\",\"status\":\"failed\",\"assertionResults\":[{\"ancestorTitles\":[\"sum\"],\"title\":\"adds\",\"status\":\"passed\",\"duration\":3,\"failureMessages\":[]},{\"ancestorTitles\":[\"sum\"],\"title\":\"subtracts\",\"status\":\"failed\",\"duration\":4,\"failureMessages\":[\"Error: nope\\n    at Object.<anonymous> (" + test.Replace("\\", "\\\\", StringComparison.Ordinal) + ":9:21)\"]}]}]}");
        var bin = box.Path_(Path.Combine("node_modules", ".bin"));
        Directory.CreateDirectory(bin);
        var script = $"#!/bin/sh\necho \"$@\" >> \"$FAKE_LOG\"\ncase \"$*\" in *--listTests*) echo '{test}';; *) for a in \"$@\"; do case \"$a\" in --outputFile=*) cp '{box.Path_("report.json")}' \"${{a#--outputFile=}}\";; esac; done; exit 1;; esac\n";
        File.WriteAllText(Path.Combine(bin, "jest"), script);
        MakeExecutable(Path.Combine(bin, "jest"));
        using var service = Service(box, Tools(box, box.Path_("none")), [new JsTestAdapter()]);

        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within();
        Assert.Equal([Path.GetFileName(box.Root), Path.Combine("src", "sum.test.js")], service.Flatten().Select(r => r.Name));
        await service.RunAllAsync(TestContext.Current.CancellationToken).Within();

        var names = service.Flatten().Select(r => r.Name).ToList();
        Assert.Contains("sum > adds", names);
        var subtracts = Find(service, "sum > subtracts");
        Assert.Equal(TestState.Failed, subtracts.State);
        Assert.Equal("Error: nope", subtracts.Message);
        Assert.Equal(TestState.Failed, service.Flatten()[0].State);
        Assert.Contains("--json", Logged(box).Last(), StringComparison.Ordinal);
    }

    // ── The service on its own ───────────────────────────────────────────────────────────────────

    private sealed class SlowAdapter : ITestAdapter
    {
        public string Name => "slow";

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<TestNode>> DiscoverAsync(TestWorkspace workspace, CancellationToken ct)
        {
            var project = new TestNode("slow:p", "p", TestKind.Project) { Adapter = this };
            project.Add(new TestNode("slow:p|a", "a", TestKind.Test));
            project.Add(new TestNode("slow:p|b", "b", TestKind.Test));
            return Task.FromResult<IReadOnlyList<TestNode>>([project]);
        }

        public async Task RunAsync(TestWorkspace workspace, TestRunRequest request, ITestRunSink sink, CancellationToken ct)
        {
            sink.Result(new TestResult("slow:p|a", TestState.Passed));
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }

        public Task<(string File, int Line)?> LocateAsync(TestNode node, CancellationToken ct) => Task.FromResult<(string File, int Line)?>(null);
    }

    [Fact]
    public async Task StoppingARunLeavesWhatFinishedAndMarksTheRestNotRun()
    {
        using var box = new Sandbox();
        var adapter = new SlowAdapter();
        using var service = Service(box, adapters: [adapter]);
        await service.DiscoverAsync(TestContext.Current.CancellationToken).Within();
        var run = service.RunAllAsync(TestContext.Current.CancellationToken);
        await adapter.Started.Task.Within();
        Assert.True(service.IsBusy);
        Assert.Equal(TestState.Running, Find(service, "b").State);

        service.Cancel();
        await run.Within();
        Assert.False(service.IsBusy);
        Assert.Equal(TestState.Passed, Find(service, "a").State);
        Assert.Equal(TestState.Unknown, Find(service, "b").State);
    }
}
