using Wicked.Core.Tasks;

namespace Wicked.Tests.Tasks;

public sealed class ProblemMatcherTests
{
    private static string W => Path.Combine(AppContext.BaseDirectory, "ws"); // a base for relative names; nothing is read from it

    private static List<TaskProblem> Run(string matcher, string output, string? workspace = null)
    {
        var collector = new ProblemCollector([BuiltInMatchers.Find(matcher)!], workspace ?? W, workspace ?? W);
        foreach (var line in output.ReplaceLineEndings("\n").Split('\n')) collector.Feed(AnsiText.Strip(line));
        return [.. collector.Problems];
    }

    [Fact]
    public void GccErrorsWarningsAndFatalErrorsAreFound()
    {
        // Recorded from gcc 14 building a small program; the note lines are not problems.
        const string Output = """
            main.c: In function 'main':
            main.c:5:12: error: 'x' undeclared (first use in this function)
                5 |     return x;
                  |            ^
            main.c:5:12: note: each undeclared identifier is reported only once for each function it appears in
            src/util.c:12:5: warning: implicit declaration of function 'foo' [-Wimplicit-function-declaration]
            src/util.h:1:10: fatal error: missing.h: No such file or directory
            """;
        var found = Run("$gcc", Output);
        Assert.Equal(3, found.Count);
        Assert.Equal((Path.Combine(W, "main.c"), 5, 12, ProblemSeverity.Error, "'x' undeclared (first use in this function)"), (found[0].File, found[0].Line, found[0].Column, found[0].Severity, found[0].Message));
        Assert.Equal((Path.Combine(W, "src", "util.c"), 12, 5, ProblemSeverity.Warning), (found[1].File, found[1].Line, found[1].Column, found[1].Severity));
        Assert.Equal(ProblemSeverity.Error, found[2].Severity);
        Assert.Equal("missing.h: No such file or directory", found[2].Message);
    }

    [Fact]
    public void TscFindsBothOfItsLayouts()
    {
        const string Output = """
            src/app.ts(10,5): error TS2322: Type 'string' is not assignable to type 'number'.
            src/lib/util.ts:3:14 - error TS2304: Cannot find name 'foo'.
            src/lib/util.ts:7:1 - warning TS6133: 'x' is declared but its value is never read.

            Found 2 errors in 2 files.
            """;
        var found = Run("$tsc", Output);
        Assert.Equal(3, found.Count);
        Assert.Equal((10, 5, "TS2322", ProblemSeverity.Error), (found[0].Line, found[0].Column, found[0].Code, found[0].Severity));
        Assert.Equal((3, 14, "TS2304"), (found[1].Line, found[1].Column, found[1].Code));
        Assert.Equal(ProblemSeverity.Warning, found[2].Severity);
        Assert.Equal(Path.Combine(W, "src", "lib", "util.ts"), found[1].File);
    }

    [Fact]
    public void MsBuildProblemsAreFoundOnceAndWithoutTheProjectSuffix()
    {
        // dotnet build prints each diagnostic where it happens and again in its summary.
        const string Output = """
            /w/src/Program.cs(5,13): error CS1002: ; expected [/w/src/App.csproj]
            /w/src/Program.cs(9,1): warning CS0168: The variable 'e' is declared but never used [/w/src/App.csproj]
            Build FAILED.
            /w/src/Program.cs(5,13): error CS1002: ; expected [/w/src/App.csproj]
            /w/src/Program.cs(9,1): warning CS0168: The variable 'e' is declared but never used [/w/src/App.csproj]
                1 Warning(s)
                1 Error(s)
            """;
        var found = Run("$msCompile", Output);
        Assert.Equal(2, found.Count);
        Assert.Equal(("/w/src/Program.cs", 5, 13, "CS1002", "; expected", ProblemSeverity.Error), (found[0].File, found[0].Line, found[0].Column, found[0].Code, found[0].Message, found[0].Severity));
        Assert.Equal("The variable 'e' is declared but never used", found[1].Message);
    }

    [Fact]
    public void RustcReadsTheHeadlineAndTheArrowLineTogether()
    {
        const string Output = """
            warning: unused variable: `y`
             --> src/main.rs:3:9
              |
            3 |     let y = 1;
              |         ^ help: if this is intentional, prefix it with an underscore: `_y`

            error[E0425]: cannot find value `z` in this scope
             --> src/lib.rs:12:20
              |
            12|     println!("{}", z);
              |                    ^ not found in this scope

            warning: `demo` (bin "demo") generated 1 warning
            error: could not compile `demo` (bin "demo") due to 1 previous error
            """;
        var found = Run("$rustc", Output);
        Assert.Equal(2, found.Count);
        Assert.Equal((ProblemSeverity.Warning, 3, 9, "unused variable: `y`"), (found[0].Severity, found[0].Line, found[0].Column, found[0].Message));
        Assert.Equal((ProblemSeverity.Error, 12, 20, "E0425"), (found[1].Severity, found[1].Line, found[1].Column, found[1].Code));
        Assert.Equal(Path.Combine(W, "src", "lib.rs"), found[1].File);
    }

    [Fact]
    public void EslintStylishLoopsOverAFilesProblems()
    {
        const string Output = """
            /w/src/a.js
              1:10  error    'x' is defined but never used       no-unused-vars
              4:1   warning  Unexpected console statement        no-console

            /w/src/b.js
              2:3  error  Parsing error: Unexpected token

            ✖ 3 problems (2 errors, 1 warning)
            """;
        var found = Run("$eslint-stylish", Output);
        Assert.Equal(3, found.Count);
        Assert.Equal(("/w/src/a.js", 1, 10, ProblemSeverity.Error, "no-unused-vars"), (found[0].File, found[0].Line, found[0].Column, found[0].Severity, found[0].Code));
        Assert.Equal(("/w/src/a.js", 4, ProblemSeverity.Warning), (found[1].File, found[1].Line, found[1].Severity));
        Assert.Equal(("/w/src/b.js", 2, 3, "Parsing error: Unexpected token"), (found[2].File, found[2].Line, found[2].Column, found[2].Message));
    }

    [Fact]
    public void GoReadsTheCompilersAndVetsLines()
    {
        const string Output = """
            # example.com/demo
            ./main.go:7:2: undefined: fmtt
            ./util/util.go:20:9: cannot use x (variable of type int) as string value in return statement
            vet: ./main.go:12:3: unreachable code
            """;
        var found = Run("$go", Output);
        Assert.Equal(3, found.Count);
        Assert.Equal((7, 2, "undefined: fmtt"), (found[0].Line, found[0].Column, found[0].Message));
        Assert.EndsWith(Path.Combine("util", "util.go"), found[1].File, StringComparison.Ordinal);
    }

    [Fact]
    public void ColourCodesDoNotHideProblems()
    {
        var found = Run("$gcc", "\u001b[1mmain.c:5:12:\u001b[0m \u001b[1;31merror:\u001b[0m 'x' undeclared");
        Assert.Equal("'x' undeclared", Assert.Single(found).Message);
        Assert.Equal("plain", AnsiText.Strip("\u001b[32mplain\u001b[0m\u001b]8;;http://x\u001b\\"));
    }

    [Fact]
    public void AUserPatternWithASeverityCapturesAndAFileThatOnlyTheTaskFolderHas()
    {
        var file = TasksFile.Parse("""
            { "tasks": [ { "label": "t", "command": "x", "problemMatcher": {
              "owner": "mine", "fileLocation": "autoDetect",
              "pattern": { "regexp": "^(\\w+): (.+?) at (.+):(\\d+)$", "severity": 1, "message": 2, "file": 3, "line": 4 } } } ] }
            """, "linux");
        using var box = new Sandbox();
        box.Write(Path.Combine("sub", "f.txt"), "x");
        var collector = new ProblemCollector(file.Tasks[0].Matchers, box.Root, box.Path_("sub"));
        collector.Feed("warning: careful at f.txt:3");
        var problem = Assert.Single(collector.Problems);
        Assert.Equal(box.Path_(Path.Combine("sub", "f.txt")), problem.File);
        Assert.Equal((3, ProblemSeverity.Warning, "mine"), (problem.Line, problem.Severity, problem.Owner));
    }

    [Fact]
    public void ABackgroundMatcherTellsCyclesApartAndClearsOnTheNext()
    {
        var matcher = BuiltInMatchers.Find("$tsc-watch")!;
        var collector = new ProblemCollector([matcher], W, W);
        Assert.True(collector.HasBackground);
        Assert.False(collector.Feed("[10:00:01 AM] - Starting compilation in watch mode...").Began);
        var problem = collector.Feed("a.ts(1,1): error TS1005: ';' expected.");
        Assert.NotNull(problem.Problem);
        Assert.True(collector.Feed("[10:00:05 AM] - Found 1 error. Watching for file changes.").Ended);
        Assert.Single(collector.Problems);
        Assert.True(collector.Feed("[10:00:09 AM] - File change detected. Starting incremental compilation...").Began);
        Assert.Empty(collector.Problems);
    }

    [Fact]
    public void ALineThatBreaksAMultiLinePatternStartsOverAtTheFirstPattern()
    {
        // rustc's headline is followed by something other than an arrow line, then a real problem.
        const string Output = """
            warning: something general
            error[E1]: real
             --> a.rs:1:1
            """;
        var found = Run("$rustc", Output);
        var one = Assert.Single(found);
        Assert.Equal("real", one.Message);
    }
}
