using Wicked.App.Tasks;
using Wicked.Core.Debug;
using Wicked.Core.Run;
using Wicked.Core.Tasks;
using Wicked.Core.Terminal;
using Wicked.Tests.Tasks;

namespace Wicked.Tests.Run;

/// <summary>"Run Current File": the command line for each kind of file, and how each shell is given it.</summary>
public sealed class FileRunnerTests
{
    private static string[][] Steps(FileRunPlan plan) => [.. plan.Steps.Select(s => s.ToArray())];

    [Theory]
    [InlineData("hello.py", "python", false, "python3")]
    [InlineData("hello.py", "python", true, "python")]
    [InlineData("hello.js", "javascript", false, "node")]
    [InlineData("hello.ts", "typescript", false, "node")]
    [InlineData("hello.sh", "shellscript", false, "bash")]
    [InlineData("hello.rb", null, false, "ruby")]
    [InlineData("Hello.java", null, false, "java")]
    public void AScriptRunsWithItsInterpreter(string name, string? language, bool windows, string program)
    {
        using var box = new Sandbox();
        var file = box.Write(name, "");
        var plan = FileRunner.Plan(file, language, box.Root, windows)!;
        Assert.Equal([[program, file]], Steps(plan));
    }

    [Fact]
    public void ACSharpFileRunsItsProjectOrOnItsOwn()
    {
        using var box = new Sandbox();
        var project = box.Write(Path.Combine("app", "App.csproj"), "<Project />");
        var inProject = box.Write(Path.Combine("app", "src", "Program.cs"), "");
        var alone = box.Write(Path.Combine("scripts", "tool.cs"), "");
        Assert.Equal([["dotnet", "run", "--project", project]], Steps(FileRunner.Plan(inProject, "csharp", box.Root, false)!));
        // .NET 10 runs a lone .cs file (dotnet run app.cs).
        Assert.Equal([["dotnet", "run", alone]], Steps(FileRunner.Plan(alone, "csharp", box.Root, false)!));
    }

    [Fact]
    public void ARustFileRunsItsCargoProjectAndAloneHasNoWay()
    {
        using var box = new Sandbox();
        var manifest = box.Write(Path.Combine("crate", "Cargo.toml"), "[package]\nname = \"c\"\n");
        var main = box.Write(Path.Combine("crate", "src", "main.rs"), "");
        var alone = box.Write("loose.rs", "");
        Assert.Equal([["cargo", "run", "--manifest-path", manifest]], Steps(FileRunner.Plan(main, "rust", box.Root, false)!));
        Assert.Null(FileRunner.Plan(alone, "rust", box.Root, false));
    }

    [Fact]
    public void ACFileIsCompiledThenRun()
    {
        using var box = new Sandbox();
        var file = box.Write("main.c", "");
        var output = Path.Combine(box.Root, "main");
        Assert.Equal([["cc", "-g", file, "-o", output], [output]], Steps(FileRunner.Plan(file, "c", box.Root, false)!));
    }

    [Fact]
    public void AFileWickedCannotRunHasNoPlan()
    {
        using var box = new Sandbox();
        Assert.Null(FileRunner.Plan(box.Write("notes.txt", ""), "plaintext", box.Root, false));
        Assert.Null(FileRunner.Plan(box.Write("data.json", ""), "json", box.Root, false));
    }

    private static readonly FileRunPlan Spaced = new([["python3", "/home/a b/it's.py"]], "python3");

    [Theory]
    [InlineData(ShellKind.Bash, "python3 '/home/a b/it'\\''s.py'")]
    [InlineData(ShellKind.Zsh, "python3 '/home/a b/it'\\''s.py'")]
    [InlineData(ShellKind.Fish, "python3 '/home/a b/it\\'s.py'")]
    [InlineData(ShellKind.PowerShell, "python3 '/home/a b/it''s.py'")]
    [InlineData(ShellKind.Cmd, "python3 \"/home/a b/it's.py\"")]
    public void EachShellGetsTheLineQuotedItsOwnWay(ShellKind shell, string expected) =>
        Assert.Equal(expected, FileRunner.Format(Spaced, shell));

    [Fact]
    public void ALaterStepRunsOnlyWhenTheOneBeforeSucceeded()
    {
        var plan = new FileRunPlan([["cc", "/src/my app.c", "-o", "/src/my app"], ["/src/my app"]], "cc");
        Assert.Equal("cc '/src/my app.c' -o '/src/my app' && '/src/my app'", FileRunner.Format(plan, ShellKind.Bash));
        // Windows PowerShell 5 has no &&; a quoted program needs the call operator.
        Assert.Equal("cc '/src/my app.c' -o '/src/my app'; if ($?) { & '/src/my app' }", FileRunner.Format(plan, ShellKind.PowerShell));
    }

    [Theory]
    [InlineData("python", "debugpy")]
    [InlineData("javascript", "pwa-node")]
    [InlineData("typescript", "pwa-node")]
    public void PythonAndJavaScriptDebugTheCurrentFileWithNoLaunchJson(string language, string type)
    {
        var file = LaunchFile.Parse(LaunchStarter.CurrentFile(language)!);
        var configuration = Assert.Single(file.Configurations);
        Assert.Equal(("Current File", type, "launch"), (configuration.Name, configuration.Type, configuration.Request));
        Assert.Equal("${file}", configuration.Properties["program"]!.GetValue<string>());
        Assert.Null(LaunchStarter.CurrentFile("rust"));
    }

    [Fact]
    public void TheLastResultSaysHowATaskEnded()
    {
        TaskProblem Error() => new("a.c", 1, 1, 1, 1, ProblemSeverity.Error, null, "bad", "cc");
        Assert.Equal("✓ build · 4.2 s", TasksHost.ResultText("build", new TaskRunResult(0, false, null, []), TimeSpan.FromSeconds(4.2)));
        Assert.Equal("✓ build · 12 s", TasksHost.ResultText("build", new TaskRunResult(0, false, null, []), TimeSpan.FromSeconds(12.7)));
        Assert.Equal("✗ build · 2 errors", TasksHost.ResultText("build", new TaskRunResult(1, false, null, [Error(), Error()]), TimeSpan.FromSeconds(1)));
        Assert.Equal("✗ build · exit code 3", TasksHost.ResultText("build", new TaskRunResult(3, false, null, []), TimeSpan.FromSeconds(1)));
        Assert.Equal("✗ build · could not run", TasksHost.ResultText("build", new TaskRunResult(null, false, "not found", []), TimeSpan.Zero));
        Assert.Null(TasksHost.ResultText("build", new TaskRunResult(null, true, null, []), TimeSpan.Zero));
    }
}
