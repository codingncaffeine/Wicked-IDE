using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wicked.Core.Testing;

/// <summary>Go modules: tests listed with <c>go test -list</c>, run with <c>-json</c> and a <c>-run</c> pattern.</summary>
public sealed partial class GoTestAdapter : ITestAdapter
{
    [GeneratedRegex(@"^(?:ok|FAIL|\?)\s+(?<pkg>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex PackageLine();

    public string Name => "go";

    public async Task<IReadOnlyList<TestNode>> DiscoverAsync(TestWorkspace ws, CancellationToken ct)
    {
        var kept = new List<string>();
        foreach (var f in AdapterSupport.Files(ws.Root, "go.mod", 3).Select(f => Path.GetDirectoryName(f)!).OrderBy(f => f.Length))
        {
            if (!kept.Any(k => f.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.Ordinal))) kept.Add(f);
        }

        var nodes = new List<TestNode>();
        foreach (var folder in kept)
        {
            var result = await ToolRunner.RunAsync(ws.Tools, "go", ["test", "-list", ".", "./..."], folder, null, ct).ConfigureAwait(false);
            var project = new TestNode("go:" + folder, Path.GetFileName(folder), TestKind.Project) { Adapter = this, ProjectPath = folder };
            if (!result.Started)
            {
                project.State = TestState.Failed;
                project.Message = result.StartError;
            }
            else
            {
                ParseList(project, result.Lines);
                if (project.Children.Count == 0 && result.ExitCode != 0)
                {
                    project.State = TestState.Failed;
                    project.Message = string.Join('\n', result.Lines.Where(l => l.Length > 0).TakeLast(10));
                }
            }

            nodes.Add(project);
        }

        return nodes;
    }

    /// <summary>Names the listing prints, then the "ok  pkg  time" line that says which package they were in.</summary>
    public static void ParseList(TestNode project, IEnumerable<string> lines)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            var m = PackageLine().Match(line);
            if (m.Success)
            {
                if (names.Count > 0)
                {
                    var pkg = m.Groups["pkg"].Value;
                    var group = new TestNode(project.Id + "|" + pkg, pkg, TestKind.Group) { Key = pkg };
                    project.Add(group);
                    foreach (var n in names) group.Add(new TestNode(group.Id + "|" + n, n, TestKind.Test) { Key = n });
                }

                names.Clear();
                continue;
            }

            if (Regex.IsMatch(line, @"^(Test|Fuzz|Example)\w*$", RegexOptions.CultureInvariant)) names.Add(line);
        }
    }

    public async Task RunAsync(TestWorkspace ws, TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var tests = request.All ? project.Leaves.ToList() : [.. request.Tests.SelectMany(t => t.Leaves).DistinctBy(l => l.Id)];
        var byPackage = tests.GroupBy(t => PackageOf(t).Key).ToList();
        foreach (var package in byPackage)
        {
            var args = new List<string> { "test", "-json" };
            var group = PackageOf(package.First());
            // A subtest is run through its top-level test: go's -run matches a level at a time.
            var tops = package.Select(t => TopLevel(t.Key)).Distinct(StringComparer.Ordinal).ToList();
            if (!request.All && tops.Count < group.Children.Count)
            {
                args.Add("-run");
                args.Add("^(" + string.Join('|', tops.Select(Regex.Escape)) + ")$");
            }

            args.Add(package.Key);
            var run = await ToolRunner.RunAsync(ws.Tools, "go", args, project.ProjectPath, null, ct).ConfigureAwait(false);
            if (!run.Started)
            {
                foreach (var t in package) sink.Result(new TestResult(t.Id, TestState.Failed, Message: run.StartError));
                continue;
            }

            foreach (var line in run.Lines) sink.Output(Display(line));
            var parent = group;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in ReadJson(run.Lines, parent))
            {
                seen.Add(r.Id);
                sink.Result(r);
            }

            // A package that did not build reports on the package, not on its tests.
            if (run.ExitCode != 0)
            {
                var missing = package.Where(t => !seen.Contains(t.Id)).ToList();
                if (missing.Count > 0)
                {
                    var why = PackageOutput(run.Lines);
                    foreach (var t in missing) sink.Result(new TestResult(t.Id, TestState.Failed, Message: why));
                }
            }
        }
    }

    private static string TopLevel(string key) => key.Split('/')[0]; // not a path

    /// <summary>The package group a test sits under (a subtest sits under its parent test).</summary>
    private static TestNode PackageOf(TestNode test)
    {
        var node = test;
        while (node.Parent is { Kind: not TestKind.Project } parent) node = parent;
        return node;
    }

    private static string Display(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            return doc.RootElement.TryGetProperty("Output", out var o) ? o.GetString()?.TrimEnd('\n') ?? "" : "";
        }
        catch (JsonException)
        {
            return jsonLine;
        }
    }

    private static string PackageOutput(IEnumerable<string> lines)
    {
        var text = new StringBuilder();
        foreach (var line in lines)
        {
            var shown = Display(line);
            if (shown.Length > 0 && !shown.StartsWith("FAIL", StringComparison.Ordinal)) text.AppendLine(shown);
        }

        return text.ToString().Trim();
    }

    /// <summary>test2json events folded into one result per test; a subtest becomes a node under its parent.</summary>
    public static IEnumerable<TestResult> ReadJson(IEnumerable<string> lines, TestNode package)
    {
        var output = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var done = new List<(string Test, string Action, double? Elapsed)>();
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var e = doc.RootElement;
                if (!e.TryGetProperty("Test", out var t) || t.GetString() is not { } test) continue;
                var action = e.TryGetProperty("Action", out var a) ? a.GetString() : null;
                if (action == "output" && e.TryGetProperty("Output", out var o))
                {
                    if (!output.TryGetValue(test, out var sb)) output[test] = sb = new StringBuilder();
                    sb.Append(o.GetString());
                }
                else if (action is "pass" or "fail" or "skip")
                {
                    done.Add((test, action, e.TryGetProperty("Elapsed", out var el) ? el.GetDouble() : null));
                }
            }
        }

        foreach (var (test, action, elapsed) in done)
        {
            var state = action switch { "pass" => TestState.Passed, "fail" => TestState.Failed, _ => TestState.Skipped };
            var text = output.GetValueOrDefault(test)?.ToString() ?? "";
            string? message = null;
            string? stack = null;
            if (state == TestState.Failed)
            {
                var lines2 = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.StartsWith("=== ", StringComparison.Ordinal) && !l.TrimStart().StartsWith("--- ", StringComparison.Ordinal) && l.Length > 0).ToList();
                message = string.Join('\n', lines2.Select(l => l.TrimStart()));
                stack = lines2.FirstOrDefault(l => Regex.IsMatch(l, @"\.go:\d+")) is { } at ? at.Trim() : null;
            }

            TimeSpan? duration = elapsed is double s ? TimeSpan.FromSeconds(s) : null;
            var slash = test.IndexOf('/', StringComparison.Ordinal); // not a path
            if (slash < 0) yield return new TestResult(package.Id + "|" + test, state, duration, message, stack, text.Length > 0 ? text : null);
            else
            {
                var parentName = test[..test.LastIndexOf('/')]; // not a path
                var parentId = package.Id + "|" + parentName;
                yield return new TestResult(package.Id + "|" + test, state, duration, message, stack, null, parentId, test[(test.LastIndexOf('/') + 1)..]); // not a path
            }
        }
    }

    public Task<(string File, int Line)?> LocateAsync(TestNode node, CancellationToken ct) => Task.Run(() =>
    {
        if (node.Kind == TestKind.Project) return ((string File, int Line)?)(Path.Combine(node.ProjectPath, "go.mod"), 1);
        if (node.Kind != TestKind.Test) return null;
        var name = node.Name.Split('/')[0]; // not a path
        return AdapterSupport.Search(node.Root.ProjectPath, "*_test.go", new Regex($@"\bfunc\s+{Regex.Escape(name)}\b", RegexOptions.CultureInvariant), ct);
    }, ct);
}
