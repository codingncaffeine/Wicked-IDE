using System.Text;
using System.Text.RegularExpressions;

namespace Wicked.Core.Testing;

/// <summary>Rust crates: tests listed with <c>cargo test -- --list</c>, run with <c>--exact</c> names, results read from libtest's text output.</summary>
public sealed partial class CargoTestAdapter : ITestAdapter
{
    [GeneratedRegex(@"^\s*Running\s+(?<t>.+?)(?:\s+\(.*\))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RunningLine();

    [GeneratedRegex(@"^test\s+(?<n>\S+)\s+\.\.\.\s+(?<r>ok|FAILED|ignored)(?:,\s*(?<why>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex ResultLine();

    [GeneratedRegex(@"^---- (?<n>\S+) stdout ----$", RegexOptions.CultureInvariant)]
    private static partial Regex FailureHeader();

    public string Name => "cargo";

    public async Task<IReadOnlyList<TestNode>> DiscoverAsync(TestWorkspace ws, CancellationToken ct)
    {
        var roots = TopLevel(AdapterSupport.Files(ws.Root, "Cargo.toml", 3).Select(f => Path.GetDirectoryName(f)!)).ToList();
        var nodes = new List<TestNode>();
        foreach (var folder in roots)
        {
            var result = await ToolRunner.RunAsync(ws.Tools, "cargo", ["test", "--workspace", "--no-fail-fast", "--", "--list"], folder, null, ct).ConfigureAwait(false);
            var project = new TestNode("cargo:" + folder, Path.GetFileName(folder), TestKind.Project) { Adapter = this, ProjectPath = folder };
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
                    project.Message = AdapterSupport.FirstError(result.Lines) ?? string.Join('\n', result.Lines.TakeLast(10));
                }
            }

            nodes.Add(project);
        }

        return nodes;
    }

    private static IEnumerable<string> TopLevel(IEnumerable<string> folders)
    {
        var kept = new List<string>();
        foreach (var f in folders.OrderBy(f => f.Length))
        {
            if (!kept.Any(k => f.StartsWith(k + Path.DirectorySeparatorChar, StringComparison.Ordinal))) kept.Add(f);
        }

        return kept;
    }

    /// <summary>The "name: test" lines under each "Running target" line, as target → module → test.</summary>
    public static void ParseList(TestNode project, IEnumerable<string> lines)
    {
        var byId = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        TestNode? target = null;
        foreach (var line in lines)
        {
            var running = RunningLine().Match(line);
            if (running.Success)
            {
                var label = running.Groups["t"].Value;
                target = AdapterSupport.Group(project, byId, project.Id + "|target|" + label, label);
                continue;
            }

            if (!line.EndsWith(": test", StringComparison.Ordinal) || target is null) continue;
            var name = line[..^": test".Length];
            var parts = name.Split("::");
            var parent = target;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                parent = AdapterSupport.Group(parent, byId, target.Id + "|" + string.Join("::", parts.Take(i + 1)), parts[i]);
            }

            parent.Add(new TestNode(target.Id + "|" + name, parts[^1], TestKind.Test) { Key = name });
        }
    }

    public async Task RunAsync(TestWorkspace ws, TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var args = new List<string> { "test", "--workspace", "--no-fail-fast", "--" };
        var tests = request.All ? project.Leaves.ToList() : [.. request.Tests.SelectMany(t => t.Leaves).DistinctBy(l => l.Id)];
        if (!request.All)
        {
            args.Add("--exact");
            args.AddRange(tests.Select(t => t.Key).Distinct());
        }

        var run = await ToolRunner.RunAsync(ws.Tools, "cargo", args, project.ProjectPath, sink.Output, ct).ConfigureAwait(false);
        if (!run.Started)
        {
            foreach (var t in tests) sink.Result(new TestResult(t.Id, TestState.Failed, Message: run.StartError));
            return;
        }

        var byKey = tests.GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, result, detail) in ReadResults(run.Lines))
        {
            if (!byKey.TryGetValue(key, out var nodes)) continue;
            foreach (var node in nodes)
            {
                if (!seen.Add(node.Id)) continue;
                sink.Result(new TestResult(node.Id, result, null, detail.Message, detail.Stack));
            }
        }

        // The build failed: nothing ran, and the compiler said why.
        if (seen.Count == 0 && run.ExitCode != 0)
        {
            var why = AdapterSupport.FirstError(run.Lines) ?? "The test run produced no results";
            foreach (var t in tests) sink.Result(new TestResult(t.Id, TestState.Failed, Message: why));
        }
    }

    /// <summary>Each "test a::b ... ok" line with, for a failure, the message and panic location libtest printed after it.</summary>
    public static IEnumerable<(string Key, TestState State, (string? Message, string? Stack) Detail)> ReadResults(IEnumerable<string> lines)
    {
        var all = lines.ToList();
        var details = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        for (var i = 0; i < all.Count; i++)
        {
            var header = FailureHeader().Match(all[i]);
            if (!header.Success) continue;
            var body = new List<string>();
            for (i++; i < all.Count && !all[i].StartsWith("---- ", StringComparison.Ordinal) && all[i] != "failures:"; i++) body.Add(all[i]);
            i--;
            while (body.Count > 0 && body[^1].Length == 0) body.RemoveAt(body.Count - 1);
            var panic = body.FindIndex(l => l.Contains("panicked at", StringComparison.Ordinal));
            var message = new StringBuilder();
            string? stack = null;
            if (panic >= 0)
            {
                stack = body[panic].Trim();
                foreach (var l in body.Skip(panic + 1).Where(IsDetail)) message.AppendLine(l);
            }
            else
            {
                foreach (var l in body.Where(IsDetail)) message.AppendLine(l);
            }

            // An old-style panic line carries the message itself.
            var text = message.ToString().Trim();
            details[header.Groups["n"].Value] = (text.Length > 0 ? text : stack, stack);
        }

        foreach (var line in all)
        {
            var m = ResultLine().Match(line);
            if (!m.Success) continue;
            var key = m.Groups["n"].Value;
            var state = m.Groups["r"].Value switch { "ok" => TestState.Passed, "FAILED" => TestState.Failed, _ => TestState.Skipped };
            yield return (key, state, details.GetValueOrDefault(key));
        }
    }

    private static bool IsDetail(string line) => !line.StartsWith("note: run with `RUST_BACKTRACE", StringComparison.Ordinal);

    public Task<(string File, int Line)?> LocateAsync(TestNode node, CancellationToken ct) => Task.Run(() =>
    {
        if (node.Kind != TestKind.Test) return node.Kind == TestKind.Project ? (Path.Combine(node.ProjectPath, "Cargo.toml"), 1) : ((string File, int Line)?)null;
        return AdapterSupport.Search(node.Root.ProjectPath, "*.rs", new Regex($@"\bfn\s+{Regex.Escape(node.Name)}\b", RegexOptions.CultureInvariant), ct);
    }, ct);
}
