using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wicked.Core.Testing;

/// <summary>
/// Python projects that use pytest, when pytest is installed (a virtual environment in the folder, or on the PATH): tests
/// listed with <c>pytest --collect-only -q</c>, run by node id, results read from the JUnit XML file pytest writes.
/// </summary>
public sealed partial class PytestAdapter : ITestAdapter
{
    private static readonly string[] Local = [Path.Combine(".venv", "bin"), Path.Combine("venv", "bin"), Path.Combine(".venv", "Scripts"), Path.Combine("venv", "Scripts")];

    [GeneratedRegex(@"^(?<f>[^\s:]+\.py)::(?<rest>\S.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex NodeId();

    public string Name => "pytest";

    private static string? Pytest(ToolEnv tools, string folder) =>
        tools.Find(OperatingSystem.IsWindows() ? "pytest.exe" : "pytest", folder, Local);

    public async Task<IReadOnlyList<TestNode>> DiscoverAsync(TestWorkspace ws, CancellationToken ct)
    {
        var roots = new List<string>();
        foreach (var folder in AdapterSupport.Folders(ws.Root, 2))
        {
            var marker = new[] { "pytest.ini", "tox.ini", "setup.cfg", "conftest.py" }.Any(m => File.Exists(Path.Combine(folder, m)))
                || (File.Exists(Path.Combine(folder, "pyproject.toml")) && File.ReadAllText(Path.Combine(folder, "pyproject.toml")).Contains("pytest", StringComparison.Ordinal))
                || Directory.GetFiles(folder, "test_*.py").Length > 0;
            if (marker && !roots.Any(r => folder.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.Ordinal))) roots.Add(folder);
        }

        var nodes = new List<TestNode>();
        foreach (var folder in roots)
        {
            if (Pytest(ws.Tools, folder) is not { } pytest) continue;
            var result = await ToolRunner.RunAsync(ws.Tools, pytest, ["--collect-only", "-q", "-p", "no:cacheprovider"], folder, null, ct).ConfigureAwait(false);
            var project = new TestNode("pytest:" + folder, Path.GetFileName(folder), TestKind.Project) { Adapter = this, ProjectPath = folder };
            ParseCollect(project, result.Lines);
            if (project.Children.Count == 0 && result.ExitCode is not (0 or 5))
            {
                project.State = TestState.Failed;
                project.Message = string.Join('\n', result.Lines.Where(l => l.Length > 0).TakeLast(12));
            }

            nodes.Add(project);
        }

        return nodes;
    }

    /// <summary>"tests/test_a.py::TestC::test_x[1]" lines as project → file → class → test.</summary>
    public static void ParseCollect(TestNode project, IEnumerable<string> lines)
    {
        var byId = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var m = NodeId().Match(line.Trim());
            if (!m.Success) continue;
            var file = m.Groups["f"].Value;
            var parts = m.Groups["rest"].Value.Split("::");
            var parent = AdapterSupport.Group(project, byId, project.Id + "|" + file, file);
            for (var i = 0; i < parts.Length - 1; i++)
            {
                parent = AdapterSupport.Group(parent, byId, project.Id + "|" + file + "::" + string.Join("::", parts.Take(i + 1)), parts[i]);
            }

            parent.Add(new TestNode(project.Id + "|" + line.Trim(), parts[^1], TestKind.Test) { Key = line.Trim(), File = Path.Combine(project.ProjectPath, file) });
        }
    }

    public async Task RunAsync(TestWorkspace ws, TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var tests = request.All ? project.Leaves.ToList() : [.. request.Tests.SelectMany(t => t.Leaves).DistinctBy(l => l.Id)];
        var pytest = Pytest(ws.Tools, project.ProjectPath);
        if (pytest is null)
        {
            foreach (var t in tests) sink.Result(new TestResult(t.Id, TestState.Failed, Message: "pytest is not installed"));
            return;
        }

        var xml = Path.Combine(ws.WorkFolder, "junit-" + Guid.NewGuid().ToString("N")[..8] + ".xml");
        try
        {
            var args = new List<string> { "-p", "no:cacheprovider", "--junitxml=" + xml, "-q" };
            if (!request.All) args.AddRange(tests.Select(t => t.Key));
            var run = await ToolRunner.RunAsync(ws.Tools, pytest, args, project.ProjectPath, sink.Output, ct).ConfigureAwait(false);
            if (!File.Exists(xml))
            {
                var why = run.StartError ?? string.Join('\n', run.Lines.Where(l => l.Length > 0).TakeLast(12));
                foreach (var t in tests) sink.Result(new TestResult(t.Id, TestState.Failed, Message: why));
                return;
            }

            foreach (var r in ReadJUnit(XDocument.Load(xml), tests)) sink.Result(r);
        }
        finally
        {
            try
            {
                File.Delete(xml);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The key pytest's JUnit file uses for a test: its module path, classes and name, dotted.</summary>
    public static string JUnitKey(string nodeId)
    {
        var parts = nodeId.Split("::");
        var module = Regex.Replace(parts[0], @"\.py$", "", RegexOptions.CultureInvariant).Replace('/', '.').Replace('\\', '.'); // not a path
        return string.Join('.', new[] { module }.Concat(parts.Skip(1)));
    }

    public static IEnumerable<TestResult> ReadJUnit(XDocument doc, IEnumerable<TestNode> tests)
    {
        var byKey = tests.GroupBy(t => JUnitKey(t.Key)).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var c in doc.Descendants("testcase"))
        {
            var key = ((string?)c.Attribute("classname") ?? "") + "." + (string?)c.Attribute("name");
            if (!byKey.TryGetValue(key, out var node)) continue;
            TimeSpan? duration = double.TryParse((string?)c.Attribute("time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? TimeSpan.FromSeconds(s) : null;
            var failure = c.Element("failure") ?? c.Element("error");
            var skipped = c.Element("skipped");
            if (failure is not null)
            {
                var text = failure.Value.Trim();
                var lines = text.Split('\n');
                var location = lines.LastOrDefault(l => Regex.IsMatch(l, @"^\S+\.py:\d+")) ?? lines.FirstOrDefault(l => Regex.IsMatch(l, @"\.py:\d+"));
                yield return new TestResult(node.Id, TestState.Failed, duration, (string?)failure.Attribute("message") ?? lines.FirstOrDefault(), text.Length == 0 ? location : text);
            }
            else
            {
                yield return new TestResult(node.Id, skipped is not null ? TestState.Skipped : TestState.Passed, duration, (string?)skipped?.Attribute("message"));
            }
        }
    }

    public Task<(string File, int Line)?> LocateAsync(TestNode node, CancellationToken ct) => Task.Run(() =>
    {
        if (node.Kind == TestKind.Project) return ((string File, int Line)?)null;
        var root = node.Root.ProjectPath;
        if (node.Kind == TestKind.Group && node.Parent?.Kind == TestKind.Project) return (Path.Combine(root, node.Name), 1);
        var file = node.Kind == TestKind.Test ? node.File : null;
        if (file is null || !File.Exists(file)) return null;
        var name = Regex.Replace(node.Name, @"\[.*\]$", "", RegexOptions.CultureInvariant);
        var lines = File.ReadAllLines(file);
        var rx = new Regex($@"\bdef\s+{Regex.Escape(name)}\b", RegexOptions.CultureInvariant);
        for (var i = 0; i < lines.Length; i++)
        {
            if (rx.IsMatch(lines[i])) return (file, i + 1);
        }

        return (file, 1);
    }, ct);
}
