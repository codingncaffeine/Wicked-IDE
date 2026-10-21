using System.Xml.Linq;
using Wicked.Core.Testing;

namespace Wicked.Tests.Tasks;

/// <summary>Each framework's own output, recorded, read into the tree and the results.</summary>
public sealed class TestAdapterParsingTests
{
    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    private static string Paths(TestNode node) => string.Join(" / ", node.Children.Select(c => c.Kind == TestKind.Test ? c.Name : $"{c.Name}[{Paths(c)}]"));

    [Fact]
    public void DotnetListBecomesProjectNamespaceClassTest()
    {
        var names = DotnetTestAdapter.ParseList(Lines("""
            Test run for /w/Tiny.Tests.dll (.NETCoreApp,Version=v10.0)
            The following Tests are available:
                Tiny.Tests.MathTests.Adds
                Tiny.Tests.MathTests.Positive(x: 1.5)
                Tiny.Tests.Deep.OtherTests.Works
                Tiny.Tests.MathTests.Adds
            Some other line
                not in the list
            """));
        Assert.Equal(3, names.Count);
        var project = new TestNode("p", "Tiny", TestKind.Project);
        DotnetTestAdapter.BuildTree(project, names);
        Assert.Equal("Tiny.Tests[MathTests[Adds / Positive(x: 1.5)]] / Tiny.Tests.Deep[OtherTests[Works]]", Paths(project));
    }

    [Fact]
    public void DotnetFilterTakesAWholeClassByPrefixAndOtherTestsByBaseName()
    {
        var project = new TestNode("p", "Tiny", TestKind.Project);
        DotnetTestAdapter.BuildTree(project, ["A.B.C1.One", "A.B.C1.Two", "A.B.C2.Only(x: 1)", "A.B.C2.Only(x: 2)", "A.B.C2.Other"]);
        var c1 = project.Children[0].Children[0];
        var c2 = project.Children[0].Children[1];
        Assert.Equal("FullyQualifiedName~A.B.C1.", DotnetTestAdapter.Filter([c1]));
        Assert.Equal("FullyQualifiedName=A.B.C2.Other", DotnetTestAdapter.Filter([c2.Children.Last()]));
        Assert.Equal("FullyQualifiedName=A.B.C2.Only", DotnetTestAdapter.Filter([c2.Children[0], c2.Children[1]]));
        Assert.Equal("FullyQualifiedName~A.B.C1.|FullyQualifiedName=A.B.C2.Other", DotnetTestAdapter.Filter([c1, c2.Children.Last()]));
    }

    [Fact]
    public void ATrxFileGivesStatesDurationsAndTheFailureWithItsStack()
    {
        var doc = XDocument.Parse("""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
              <UnitTestResult testName="N.C.Ok" outcome="Passed" duration="00:00:00.0123000" />
              <UnitTestResult testName="N.C.Bad" outcome="Failed" duration="00:00:01.5">
                <Output><ErrorInfo><Message>Assert.Equal() Failure
            Expected: 5
            Actual:   4</Message><StackTrace>   at N.C.Bad() in /w/C.cs:line 9</StackTrace></ErrorInfo></Output>
              </UnitTestResult>
              <UnitTestResult testName="N.C.Skip" outcome="NotExecuted"><Output><ErrorInfo><Message>not now</Message></ErrorInfo></Output></UnitTestResult>
            </Results></TestRun>
            """);
        var results = DotnetTestAdapter.ReadTrx(doc, "proj").ToList();
        Assert.Equal([("proj|N.C.Ok", TestState.Passed), ("proj|N.C.Bad", TestState.Failed), ("proj|N.C.Skip", TestState.Skipped)], results.Select(r => (r.Id, r.State)));
        Assert.Equal(TimeSpan.FromMilliseconds(12.3), results[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(1.5), results[1].Duration);
        Assert.StartsWith("Assert.Equal() Failure", results[1].Message, StringComparison.Ordinal);
        Assert.Contains("C.cs:line 9", results[1].Stack, StringComparison.Ordinal);
        Assert.Equal("not now", results[2].Message);
    }

    private const string CargoList = """
           Compiling demo v0.1.0 (/w/demo)
            Finished `test` profile [unoptimized + debuginfo] target(s) in 0.52s
             Running unittests src/lib.rs (target/debug/deps/demo-0123456789abcdef)
        tests::adds: test
        tests::subtracts: test
        util::parse::trims: test

        3 tests, 0 benchmarks
             Running tests/integration.rs (target/debug/deps/integration-fedcba9876543210)
        full_flow: test

        1 test, 0 benchmarks
           Doc-tests demo

        0 tests, 0 benchmarks
        """;

    [Fact]
    public void CargoListBecomesTargetModuleTest()
    {
        var project = new TestNode("c", "demo", TestKind.Project);
        CargoTestAdapter.ParseList(project, Lines(CargoList));
        Assert.Equal("unittests src/lib.rs[tests[adds / subtracts] / util[parse[trims]]] / tests/integration.rs[full_flow]", Paths(project));
        Assert.Equal("tests::subtracts", project.Leaves.ElementAt(1).Key);
    }

    [Fact]
    public void CargoResultsCarryThePanicAsMessageAndLocation()
    {
        var results = CargoTestAdapter.ReadResults(Lines("""
            running 3 tests
            test tests::adds ... ok
            test tests::subtracts ... FAILED
            test util::parse::trims ... ignored

            failures:

            ---- tests::subtracts stdout ----

            thread 'tests::subtracts' panicked at src/lib.rs:14:9:
            assertion `left == right` failed
              left: 1
             right: 2
            note: run with `RUST_BACKTRACE=1` environment variable to display a backtrace


            failures:
                tests::subtracts

            test result: FAILED. 1 passed; 1 failed; 1 ignored; 0 measured; 0 filtered out; finished in 0.00s
            """)).ToList();
        Assert.Equal([("tests::adds", TestState.Passed), ("tests::subtracts", TestState.Failed), ("util::parse::trims", TestState.Skipped)], results.Select(r => (r.Key, r.State)));
        Assert.Contains("src/lib.rs:14:9", results[1].Detail.Stack, StringComparison.Ordinal);
        Assert.Contains("left: 1", results[1].Detail.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("RUST_BACKTRACE", results[1].Detail.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GoListAndJsonEventsBecomePackagesTestsAndSubtests()
    {
        var project = new TestNode("g", "demo", TestKind.Project);
        GoTestAdapter.ParseList(project, Lines("""
            TestAdd
            TestSub
            ok  	example.com/demo/calc	0.002s
            TestParse
            ok  	example.com/demo/parse	0.002s
            ?   	example.com/demo/cmd	[no test files]
            """));
        Assert.Equal("example.com/demo/calc[TestAdd / TestSub] / example.com/demo/parse[TestParse]", Paths(project));

        var calc = project.Children[0];
        var results = GoTestAdapter.ReadJson(Lines("""
            {"Action":"run","Package":"example.com/demo/calc","Test":"TestAdd"}
            {"Action":"output","Package":"example.com/demo/calc","Test":"TestAdd","Output":"=== RUN   TestAdd\n"}
            {"Action":"pass","Package":"example.com/demo/calc","Test":"TestAdd","Elapsed":0.01}
            {"Action":"run","Package":"example.com/demo/calc","Test":"TestSub"}
            {"Action":"output","Package":"example.com/demo/calc","Test":"TestSub","Output":"    calc_test.go:21: got 1, want 2\n"}
            {"Action":"output","Package":"example.com/demo/calc","Test":"TestSub","Output":"--- FAIL: TestSub (0.00s)\n"}
            {"Action":"fail","Package":"example.com/demo/calc","Test":"TestSub","Elapsed":0}
            {"Action":"fail","Package":"example.com/demo/calc","Test":"TestSub/neg","Elapsed":0.5}
            {"Action":"fail","Package":"example.com/demo/calc","Elapsed":0.02}
            """), calc).ToList();
        Assert.Equal(3, results.Count);
        Assert.Equal((calc.Id + "|TestAdd", TestState.Passed, TimeSpan.FromSeconds(0.01)), (results[0].Id, results[0].State, results[0].Duration));
        Assert.Equal("calc_test.go:21: got 1, want 2", results[1].Message);
        Assert.Equal("calc_test.go:21: got 1, want 2", results[1].Stack);
        Assert.Equal((calc.Id + "|TestSub/neg", calc.Id + "|TestSub", "neg"), (results[2].Id, results[2].ParentId, results[2].Name));
    }

    [Fact]
    public void PytestCollectionAndJUnitAreMatchedByModuleClassAndName()
    {
        var project = new TestNode("py:/w", "w", TestKind.Project) { ProjectPath = "/w" };
        PytestAdapter.ParseCollect(project, Lines("""
            tests/test_math.py::test_add
            tests/test_math.py::TestOps::test_mul
            tests/test_math.py::test_param[1-2]

            3 tests collected in 0.01s
            """));
        Assert.Equal("tests/test_math.py[test_add / TestOps[test_mul] / test_param[1-2]]", Paths(project));

        var junit = XDocument.Parse("""
            <testsuites><testsuite name="pytest" tests="3">
              <testcase classname="tests.test_math" name="test_add" time="0.001" />
              <testcase classname="tests.test_math.TestOps" name="test_mul" time="0.002"><failure message="assert 6 == 7">self = x
            >       assert 2*3 == 7
            E       assert 6 == 7

            tests/test_math.py:9: AssertionError</failure></testcase>
              <testcase classname="tests.test_math" name="test_param[1-2]" time="0.001"><skipped message="not on linux" /></testcase>
            </testsuite></testsuites>
            """);
        var results = PytestAdapter.ReadJUnit(junit, project.Leaves).ToList();
        Assert.Equal([TestState.Passed, TestState.Failed, TestState.Skipped], results.Select(r => r.State));
        Assert.Equal("assert 6 == 7", results[1].Message);
        Assert.Contains("tests/test_math.py:9", results[1].Stack, StringComparison.Ordinal);
        Assert.Equal("tests.test_math.TestOps.test_mul", PytestAdapter.JUnitKey("tests/test_math.py::TestOps::test_mul"));
    }

    [Fact]
    public void JestAndVitestReportsAreReadAndUnlistedCasesGetANodeUnderTheirFile()
    {
        var project = new TestNode("js:/w/web", "web", TestKind.Project) { ProjectPath = "/w/web" };
        JsTestAdapter.ParseJestFiles(project, ["/w/web/src/sum.test.js", "not a path"]);
        var file = Assert.Single(project.Children);
        Assert.Equal(Path.Combine("src", "sum.test.js"), file.Name);

        const string Report = """
            {"testResults":[{"name":"/w/web/src/sum.test.js","status":"failed","assertionResults":[
              {"ancestorTitles":["sum"],"title":"adds","fullName":"sum adds","status":"passed","duration":3,"failureMessages":[]},
              {"ancestorTitles":["sum"],"title":"subtracts","fullName":"sum subtracts","status":"failed","duration":5,
               "failureMessages":["Error: expect(received).toBe(expected)\n\nExpected: 1\nReceived: 2\n    at Object.<anonymous> (/w/web/src/sum.test.js:9:21)\n    at node:internal"]},
              {"ancestorTitles":[],"title":"later","fullName":"later","status":"pending","duration":null,"failureMessages":[]}]},
              {"name":"/w/web/src/broken.test.js","status":"failed","message":"SyntaxError: Unexpected token","assertionResults":[]}]}
            """;
        var results = JsTestAdapter.ReadReport(Report, project).ToList();
        Assert.Equal(4, results.Count);
        Assert.Equal((TestState.Passed, TimeSpan.FromMilliseconds(3), file.Id, "sum > adds"), (results[0].State, results[0].Duration, results[0].ParentId, results[0].Name));
        Assert.Equal("Error: expect(received).toBe(expected)\n\nExpected: 1\nReceived: 2", results[1].Message);
        Assert.Contains("sum.test.js:9:21", results[1].Stack, StringComparison.Ordinal);
        Assert.Equal(TestState.Skipped, results[2].State);
        Assert.Equal("SyntaxError: Unexpected token", results[3].Message);

        var vitest = new TestNode("js:/w/web", "web", TestKind.Project) { ProjectPath = "/w/web" };
        JsTestAdapter.ParseVitestList(vitest, Lines("""
            some banner
            [{"name":"sum > adds","file":"/w/web/src/sum.test.ts"},{"name":"sum > subtracts","file":"/w/web/src/sum.test.ts"},{"name":"lonely","file":"/w/web/src/b.test.ts"}]
            """));
        Assert.Equal($"{Path.Combine("src", "sum.test.ts")}[sum[adds / subtracts]] / {Path.Combine("src", "b.test.ts")}[lonely]", Paths(vitest));
    }

    [Fact]
    public void ALocationIsFoundInADotnetFrameAGoLineAndAPanic()
    {
        using var box = new Sandbox();
        var file = box.Write("src/lib.rs", "x");
        var dotnetFile = box.Write("C.cs", "x");
        Assert.Equal(new SourceLocation(dotnetFile, 9, 1), TestLocations.Find($"   at N.C.Bad() in {dotnetFile}:line 9", [box.Root]));
        Assert.Equal(new SourceLocation(Path.GetFullPath(file), 14, 9), TestLocations.Find("thread 'x' panicked at src/lib.rs:14:9:", [box.Root]));
        Assert.Null(TestLocations.Find("thread 'x' panicked at missing.rs:14:9:", [box.Root]));
        Assert.Null(TestLocations.Find("no location here", [box.Root]));
    }
}
