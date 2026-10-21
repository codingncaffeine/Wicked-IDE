using System.Collections.Immutable;
using Wicked.Core.Tasks;

namespace Wicked.Tests.Tasks;

public sealed class TaskVariablesTests
{
    private static readonly string Ws = Path.Combine(Path.DirectorySeparatorChar.ToString(), "home", "me", "proj");

    private static VariableContext Context(string? file = null) => new(Ws, file, 12, "sel", ImmutableDictionary<string, string>.Empty.Add("name", "Ada"), n => n == "FOO" ? "bar" : null);

    [Fact]
    public void TheFileVariablesAreWorkedOutFromTheActiveEditor()
    {
        var file = Path.Combine(Ws, "src", "util", "Thing.test.cs");
        var c = Context(file);
        string X(string v) => TaskVariables.Expand(v, c).Text;
        Assert.Equal(Ws, X("${workspaceFolder}"));
        Assert.Equal("proj", X("${workspaceFolderBasename}"));
        Assert.Equal(file, X("${file}"));
        Assert.Equal(Path.Combine("src", "util", "Thing.test.cs"), X("${relativeFile}"));
        Assert.Equal(Path.Combine("src", "util"), X("${relativeFileDirname}"));
        Assert.Equal(Path.Combine(Ws, "src", "util"), X("${fileDirname}"));
        Assert.Equal("util", X("${fileDirnameBasename}"));
        Assert.Equal("Thing.test.cs", X("${fileBasename}"));
        Assert.Equal("Thing.test", X("${fileBasenameNoExtension}"));
        Assert.Equal(".cs", X("${fileExtname}"));
        Assert.Equal("12", X("${lineNumber}"));
        Assert.Equal("sel", X("${selectedText}"));
        Assert.Equal(Path.DirectorySeparatorChar.ToString(), X("${pathSeparator}"));
    }

    [Fact]
    public void EnvAndInputVariablesAreFilledAndSeveralInOneString()
    {
        var c = Context(Path.Combine(Ws, "a.txt"));
        Assert.Equal($"bar and Ada in {Ws}", TaskVariables.Expand("${env:FOO} and ${input:name} in ${workspaceFolder}", c).Text);
        Assert.Equal("", TaskVariables.Expand("${env:UNSET}", c).Text);
        Assert.Equal(["name", "other"], TaskVariables.InputsIn("${input:name} ${input:other} ${file}").ToArray());
    }

    [Fact]
    public void AVariableThatCannotBeWorkedOutIsReportedAndLeftAsWritten()
    {
        var c = Context();
        var e = TaskVariables.Expand("run ${file} ${input:missing} ${nonsense} ${workspaceFolder}", c);
        Assert.Equal(["${file}", "${input:missing}", "${nonsense}"], e.Unresolved);
        Assert.Equal($"run ${{file}} ${{input:missing}} ${{nonsense}} {Ws}", e.Text);
        Assert.Empty(TaskVariables.Expand("no variables", c).Unresolved);
    }

    [Theory]
    [InlineData("make", QuoteMode.Escape, "make")]
    [InlineData("my target", QuoteMode.Escape, "'my target'")]
    [InlineData("$HOME/x", QuoteMode.Escape, "$HOME/x")]
    [InlineData("it's", QuoteMode.Escape, "'it'\\''s'")]
    [InlineData("\"already quoted\"", QuoteMode.Escape, "\"already quoted\"")]
    [InlineData("$HOME", QuoteMode.Strong, "'$HOME'")]
    [InlineData("a b", QuoteMode.Weak, "\"a b\"")]
    [InlineData("", QuoteMode.Escape, "''")]
    [InlineData("a;b", QuoteMode.Escape, "'a;b'")]
    public void ArgumentsAreQuotedForTheShellOnlyWhenNeeded(string value, QuoteMode mode, string expected) =>
        Assert.Equal(expected, TaskCommandLine.Quote(new TaskArgument(value, mode), windows: false));
}

public sealed class TaskDetectorTests
{
    [Fact]
    public void NpmScriptsBecomeTasksWithTheirPackageManagerAndGroups()
    {
        using var box = new Sandbox();
        box.Write("package.json", """{ "name": "x", "scripts": { "build": "tsc -p .", "test": "jest", "lint": "eslint src", "dev": "vite" } }""");
        box.Write("pnpm-lock.yaml", "");
        var tasks = TaskDetector.Detect(box.Root);
        var build = tasks.Single(t => t.Label == "npm: build");
        Assert.Equal("pnpm run build", build.Command!.Value);
        Assert.Equal((TaskGroup.Build, "tsc -p .", true, "npm"), (build.Group, build.Detail, build.Detected, build.Source));
        Assert.Equal("$tsc", Assert.Single(build.Matchers).Name);
        Assert.Equal(TaskGroup.Test, tasks.Single(t => t.Label == "npm: test").Group);
        Assert.Equal("$eslint-stylish", Assert.Single(tasks.Single(t => t.Label == "npm: lint").Matchers).Name);
        Assert.Equal(4, tasks.Length);
    }

    [Fact]
    public void MakeTargetsAreFoundAndAssignmentsPatternsAndSpecialTargetsAreNot()
    {
        using var box = new Sandbox();
        box.Write("Makefile", ".PHONY: all clean\nCC := gcc\nOBJ = a.o\n\nall: app\n\napp: $(OBJ)\n\t$(CC) -o app $(OBJ)\n\n%.o: %.c\n\t$(CC) -c $<\n\ntest check: app\n\t./app --test\n\nclean:\n\trm -f app\n");
        var tasks = TaskDetector.Detect(box.Root);
        Assert.Equal(["make: all", "make: app", "make: test", "make: check", "make: clean"], tasks.Select(t => t.Label));
        Assert.Equal("make", tasks[0].Command!.Value);
        Assert.Equal("make clean", tasks[4].Command!.Value);
        Assert.Equal(TaskGroup.Build, tasks[0].Group);
        Assert.Equal(TaskGroup.Test, tasks[2].Group);
        Assert.Equal("$gcc", Assert.Single(tasks[1].Matchers).Name);
    }

    [Fact]
    public void CargoGoAndCmakePresetsAreDetected()
    {
        using var box = new Sandbox();
        box.Write("Cargo.toml", "[package]\nname = \"d\"\n");
        box.Write("go.mod", "module example.com/d\n");
        box.Write("CMakePresets.json", """
            { "version": 3,
              "configurePresets": [ { "name": "dbg" }, { "name": "base", "hidden": true } ],
              "buildPresets": [ { "name": "dbg", "configurePreset": "dbg" } ],
              "testPresets": [ { "name": "dbg" } ] }
            """);
        var labels = TaskDetector.Detect(box.Root).Select(t => (t.Label, t.Command!.Value)).ToArray();
        Assert.Contains(("cargo: build", "cargo build"), labels);
        Assert.Contains(("cargo: test", "cargo test"), labels);
        Assert.Contains(("cargo: run", "cargo run"), labels);
        Assert.Contains(("go: build", "go build ./..."), labels);
        Assert.Contains(("go: test", "go test ./..."), labels);
        Assert.Contains(("cmake: configure dbg", "cmake --preset dbg"), labels);
        Assert.Contains(("cmake: build dbg", "cmake --build --preset dbg"), labels);
        Assert.Contains(("cmake: test dbg", "ctest --preset dbg"), labels);
        Assert.DoesNotContain(labels, l => l.Label.Contains("base", StringComparison.Ordinal));
    }

    [Fact]
    public void ASolutionGetsBuildAndTestAndAnExecutableProjectGetsRun()
    {
        using var box = new Sandbox();
        box.Write("App.slnx", "<Solution />");
        box.Write("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        box.Write(Path.Combine("lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var tasks = TaskDetector.Detect(box.Root);
        Assert.Equal(["dotnet: build App.slnx", "dotnet: test App.slnx", "dotnet: rebuild App.slnx", "dotnet: clean App.slnx", "dotnet: run App.csproj"], tasks.Select(t => t.Label));
        Assert.StartsWith("dotnet build App.slnx ", tasks[0].Command!.Value, StringComparison.Ordinal);
        Assert.Equal("$msCompile", Assert.Single(tasks[0].Matchers).Name);
        Assert.Equal(TaskGroup.Test, tasks[1].Group);
    }

    [Theory]
    [InlineData("App.sln", "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"App\", \"src\\App\\App.csproj\", \"{1}\"\nEndProject\nProject(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Lib\", \"src\\Lib\\Lib.csproj\", \"{2}\"\nEndProject\n")]
    [InlineData("App.slnx", "<Solution>\n  <Project Path=\"src/App/App.csproj\" />\n  <Project Path=\"src/Lib/Lib.csproj\" />\n</Solution>\n")]
    public void ASolutionsProgramsInFoldersBelowItAreOfferedToRun(string solution, string text)
    {
        using var box = new Sandbox();
        box.Write(solution, text);
        box.Write(Path.Combine("src", "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        box.Write(Path.Combine("src", "Lib", "Lib.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var run = Assert.Single(TaskDetector.Detect(box.Root), t => t.Role == TaskRole.Run);
        Assert.Equal("dotnet: run App.csproj", run.Label);
        Assert.StartsWith($"dotnet run --project {Path.Combine("src", "App", "App.csproj")} ", run.Command!.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectedTasksSayWhichRunTheProgramRebuildOrClean()
    {
        using var box = new Sandbox();
        box.Write("Cargo.toml", "[package]\nname = \"d\"\n");
        box.Write("Makefile", "all:\n\tcc main.c\nrun: all\n\t./a.out\nclean:\n\trm -f a.out\n");
        box.Write("package.json", """{ "scripts": { "start": "node .", "clean": "rimraf dist", "lint": "eslint ." } }""");
        var roles = TaskDetector.Detect(box.Root).ToDictionary(t => t.Label, t => t.Role);
        Assert.Equal(TaskRole.Run, roles["cargo: run"]);
        Assert.Equal(TaskRole.Clean, roles["cargo: clean"]);
        Assert.Equal(TaskRole.Rebuild, roles["cargo: rebuild"]);
        Assert.Equal(TaskRole.None, roles["cargo: build"]);
        Assert.Equal(TaskRole.Run, roles["make: run"]);
        Assert.Equal(TaskRole.Clean, roles["make: clean"]);
        Assert.Equal(TaskRole.None, roles["make: all"]);
        Assert.Equal(TaskRole.Run, roles["npm: start"]);
        Assert.Equal(TaskRole.Clean, roles["npm: clean"]);
        Assert.Equal(TaskRole.None, roles["npm: lint"]);
    }

    [Fact]
    public void AProjectOneFolderDownIsOfferedWithItsFolderInTheLabel()
    {
        using var box = new Sandbox();
        box.Write(Path.Combine("web", "package.json"), """{ "scripts": { "build": "vite build" } }""");
        box.Write(Path.Combine("node_modules", "x", "package.json"), """{ "scripts": { "build": "no" } }""");
        var task = Assert.Single(TaskDetector.Detect(box.Root));
        Assert.Equal("npm: build - web", task.Label);
        Assert.Equal(Path.Combine("${workspaceFolder}", "web"), task.Cwd);
    }

    [Fact]
    public async Task ADetectedTaskYieldsToATasksJsonTaskOfTheSameLabel()
    {
        using var box = new Sandbox();
        box.Write("package.json", """{ "scripts": { "build": "tsc", "lint": "eslint ." } }""");
        box.WriteTasks("""{ "version": "2.0.0", "tasks": [ { "label": "npm: build", "type": "shell", "command": "echo mine" } ] }""");
        using var service = new TaskService(new RecordingOutput(), new FakeEnvironment(box.Root)) { WorkspaceFolder = box.Root };
        var catalog = await service.LoadAsync(TestContext.Current.CancellationToken).Within();
        Assert.Equal("echo mine", catalog.Find("npm: build")!.Command!.Value);
        Assert.False(catalog.Find("npm: build")!.Detected);
        Assert.True(catalog.Find("npm: lint")!.Detected);
        Assert.Equal(2, catalog.All.Count());
    }
}
