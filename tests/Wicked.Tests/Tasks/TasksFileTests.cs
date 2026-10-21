using Wicked.Core.Tasks;

namespace Wicked.Tests.Tasks;

public sealed class TasksFileTests
{
    private const string Sample = """
        {
          // Comments and trailing commas are part of the format.
          "version": "2.0.0",
          "options": { "env": { "A": "1" }, },
          "tasks": [
            {
              "label": "build",
              "type": "shell",
              "command": "make",
              "args": ["-j4", "my target", { "value": "$HOME", "quoting": "strong" }],
              "options": { "cwd": "${workspaceFolder}/src", "env": { "B": "2" } },
              "group": { "kind": "build", "isDefault": true },
              "presentation": { "reveal": "silent", "clear": true, "focus": true },
              "problemMatcher": ["$gcc"],
            },
            {
              "label": "serve",
              "type": "process",
              "command": "node",
              "args": ["server.js"],
              "isBackground": true,
              "dependsOn": ["build"],
              "dependsOrder": "sequence",
              "linux": { "command": "nodejs", "args": ["linux.js"] },
              "windows": { "command": "node.exe" },
              "problemMatcher": {
                "owner": "srv",
                "fileLocation": ["relative", "${workspaceFolder}/web"],
                "pattern": { "regexp": "^(.*):(\\d+):(\\d+) (error|warning): (.*)$", "file": 1, "line": 2, "column": 3, "severity": 4, "message": 5 },
                "background": { "activeOnStart": true, "beginsPattern": "^starting", "endsPattern": "^listening" }
              }
            },
            { "label": "test", "type": "npm", "script": "test", "path": "web", "group": "test" },
            { "type": "shell", "command": "no label" },
            { "label": "odd", "type": "gulp" },
            { "label": "named", "type": "shell", "command": "x", "problemMatcher": "$nonesuch" }
          ],
          "inputs": [
            { "id": "who", "type": "promptString", "description": "Name?", "default": "me" },
            { "id": "mode", "type": "pickString", "description": "Mode", "options": ["fast", { "value": "slow", "label": "Slow one" }] },
            { "id": "bad", "type": "command" }
          ]
        }
        """;

    [Fact]
    public void ReadsTheTasksWithCommentsAndTrailingCommas()
    {
        var file = TasksFile.Parse(Sample, "linux");
        Assert.Equal(["build", "serve", "test", "named"], file.Tasks.Select(t => t.Label));

        var build = file.Tasks[0];
        Assert.Equal(TaskType.Shell, build.Type);
        Assert.Equal("make", build.Command!.Value);
        Assert.Equal(["-j4", "my target", "$HOME"], build.Args.Select(a => a.Value));
        Assert.Equal(QuoteMode.Strong, build.Args[2].Quoting);
        Assert.Equal("${workspaceFolder}/src", build.Cwd);
        Assert.Equal("1", build.Env["A"]);
        Assert.Equal("2", build.Env["B"]);
        Assert.Equal(TaskGroup.Build, build.Group);
        Assert.True(build.IsDefault);
        Assert.Equal(new TaskPresentation(RevealMode.Silent, true, true), build.Presentation);
        Assert.Equal("$gcc", Assert.Single(build.Matchers).Name);
    }

    [Fact]
    public void ThePlatformOverrideReplacesWhatItNames()
    {
        var linux = TasksFile.Parse(Sample, "linux").Tasks.First(t => t.Label == "serve");
        Assert.Equal("nodejs", linux.Command!.Value);
        Assert.Equal(["linux.js"], linux.Args.Select(a => a.Value));
        Assert.Equal(TaskType.Process, linux.Type);

        var windows = TasksFile.Parse(Sample, "windows").Tasks.First(t => t.Label == "serve");
        Assert.Equal("node.exe", windows.Command!.Value);
        Assert.Equal(["server.js"], windows.Args.Select(a => a.Value));

        var mac = TasksFile.Parse(Sample, "osx").Tasks.First(t => t.Label == "serve");
        Assert.Equal("node", mac.Command!.Value);
    }

    [Fact]
    public void DependenciesBackgroundAndAUserMatcherAreRead()
    {
        var serve = TasksFile.Parse(Sample, "linux").Tasks.First(t => t.Label == "serve");
        Assert.Equal(["build"], serve.DependsOn);
        Assert.Equal(DependsOrder.Sequence, serve.DependsOrder);
        Assert.True(serve.IsBackground);

        var matcher = Assert.Single(serve.Matchers);
        Assert.Equal("srv", matcher.Owner);
        Assert.Equal(FileLocationKind.Relative, matcher.FileLocation);
        Assert.Equal("${workspaceFolder}/web", matcher.FilePrefix);
        Assert.NotNull(matcher.Background);
        Assert.True(matcher.Background!.ActiveOnStart);
        Assert.Matches(matcher.Background.Begins, "starting up");
        Assert.Equal(4, matcher.Patterns[0].Severity);
    }

    [Fact]
    public void AnNpmTaskRunsItsScriptInItsFolder()
    {
        var test = TasksFile.Parse(Sample, "linux").Tasks.First(t => t.Label == "test");
        Assert.Equal("npm run test", test.Command!.Value);
        Assert.Equal(Path.Combine("${workspaceFolder}", "web"), test.Cwd);
        Assert.Equal(TaskGroup.Test, test.Group);
    }

    [Fact]
    public void InputsAreReadAndWhatCannotBeUsedIsReportedWithoutLosingTheRest()
    {
        var file = TasksFile.Parse(Sample, "linux");
        Assert.Equal("me", file.Inputs["who"].Default);
        Assert.Equal([("fast", "fast"), ("slow", "Slow one")], file.Inputs["mode"].Options);
        Assert.DoesNotContain("bad", file.Inputs.Keys);

        Assert.Contains(file.Problems, p => p.Contains("without a \"label\"", StringComparison.Ordinal));
        Assert.Contains(file.Problems, p => p.Contains("'gulp'", StringComparison.Ordinal));
        Assert.Contains(file.Problems, p => p.Contains("$nonesuch", StringComparison.Ordinal));
        Assert.Contains(file.Problems, p => p.Contains("'bad'", StringComparison.Ordinal));
    }

    [Fact]
    public void AFileThatIsNotJsonReportsItAndHoldsNoTasks()
    {
        var file = TasksFile.Parse("{ \"tasks\": [ ", "linux");
        Assert.Empty(file.Tasks);
        Assert.Single(file.Problems);
    }

    [Fact]
    public void ABaseMatcherIsExtendedAndAPatternNameIsResolved()
    {
        var file = TasksFile.Parse("""
            { "tasks": [
              { "label": "a", "command": "x", "problemMatcher": { "base": "$gcc", "owner": "mine", "severity": "warning" } },
              { "label": "b", "command": "x", "problemMatcher": { "owner": "o", "pattern": "$msCompile" } }
            ] }
            """, "linux");
        var a = Assert.Single(file.Tasks[0].Matchers);
        Assert.Equal("mine", a.Owner);
        Assert.Equal(ProblemSeverity.Warning, a.DefaultSeverity);
        Assert.Equal(BuiltInMatchers.Find("$gcc")!.Patterns, a.Patterns);
        Assert.Equal(BuiltInMatchers.Find("$msCompile")!.Patterns, Assert.Single(file.Tasks[1].Matchers).Patterns);
    }

    [Fact]
    public void ATaskIsAddedToAnExistingFileWithItsCommentsKept()
    {
        var task = new TaskDefinition { Label = "npm: build", Command = new TaskArgument("npm run build"), Group = TaskGroup.Build };
        var existing = "{\n  // keep me\n  \"version\": \"2.0.0\",\n  \"tasks\": [\n    { \"label\": \"one\", \"command\": \"a\" } // trailing\n  ]\n}\n";
        var added = TasksFile.AddTask(existing, task)!;
        Assert.Contains("// keep me", added, StringComparison.Ordinal);
        Assert.Contains("// trailing", added, StringComparison.Ordinal);
        Assert.Equal(["one", "npm: build"], TasksFile.Parse(added, "linux").Tasks.Select(t => t.Label));

        var emptyList = TasksFile.AddTask("{ \"version\": \"2.0.0\", \"tasks\": [] }", task)!;
        Assert.Equal(["npm: build"], TasksFile.Parse(emptyList, "linux").Tasks.Select(t => t.Label));

        var trailingComma = TasksFile.AddTask("{ \"tasks\": [ { \"label\": \"one\", \"command\": \"a\" }, ] }", task)!;
        Assert.Equal(["one", "npm: build"], TasksFile.Parse(trailingComma, "linux").Tasks.Select(t => t.Label));

        var noList = TasksFile.AddTask("{ \"version\": \"2.0.0\" }", task)!;
        Assert.Equal(["npm: build"], TasksFile.Parse(noList, "linux").Tasks.Select(t => t.Label));

        var starter = TasksFile.Parse(TasksFile.Starter(task), "linux");
        Assert.Equal(TaskGroup.Build, Assert.Single(starter.Tasks).Group);
    }
}
