using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Wicked.Core.Testing;

/// <summary>
/// .NET test projects: tests are listed with <c>dotnet test --list-tests</c>, run with a <c>--filter</c>, and their
/// results read from the TRX file the run writes.
/// </summary>
public sealed class DotnetTestAdapter : ITestAdapter
{
    private static readonly XNamespace Trx = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

    public string Name => "dotnet";

    public async Task<IReadOnlyList<TestNode>> DiscoverAsync(TestWorkspace ws, CancellationToken ct)
    {
        var projects = new List<string>();
        foreach (var pattern in new[] { "*.csproj", "*.fsproj", "*.vbproj" })
        {
            foreach (var file in AdapterSupport.Files(ws.Root, pattern, 5))
            {
                if (IsTestProject(file)) projects.Add(file);
            }
        }

        var nodes = new TestNode?[projects.Count];
        using var gate = new SemaphoreSlim(2);
        await Task.WhenAll(projects.Select(async (project, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                nodes[i] = await ListAsync(ws, project, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
        return [.. nodes.OfType<TestNode>()];
    }

    private static bool IsTestProject(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.Ordinal) || Regex.IsMatch(text, @"<IsTestProject>\s*true", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<TestNode> ListAsync(TestWorkspace ws, string project, CancellationToken ct)
    {
        var name = Path.GetFileNameWithoutExtension(project);
        var folder = Path.GetDirectoryName(project)!;
        var result = await ToolRunner.RunAsync(ws.Tools, "dotnet", ["test", project, "--list-tests", "--nologo"], folder, null, ct).ConfigureAwait(false);
        var root = new TestNode("dotnet:" + project, name, TestKind.Project) { Adapter = this, ProjectPath = project, Detail = Path.GetRelativePath(ws.Root, folder) };
        if (!result.Started)
        {
            root.State = TestState.Failed;
            root.Message = result.StartError;
            return root;
        }

        var names = ParseList(result.Lines);
        if (names.Count == 0)
        {
            if (result.ExitCode != 0)
            {
                root.State = TestState.Failed;
                root.Message = AdapterSupport.FirstError(result.Lines) ?? string.Join('\n', result.Lines.TakeLast(10));
            }

            return root;
        }

        BuildTree(root, names);
        return root;
    }

    /// <summary>The names under "The following Tests are available:", once each (a multi-target project lists them per target).</summary>
    public static List<string> ParseList(IEnumerable<string> lines)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var inList = false;
        foreach (var raw in lines)
        {
            if (raw.Contains("The following Tests are available:", StringComparison.Ordinal))
            {
                inList = true;
                continue;
            }

            if (!inList) continue;
            if (raw.Length == 0 || !char.IsWhiteSpace(raw[0]))
            {
                inList = false;
                continue;
            }

            var name = raw.Trim();
            if (name.Length > 0 && seen.Add(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>project → namespace → class → test, from names like "Ns.Class.Method" or "Ns.Class.Method(x: 1)".</summary>
    public static void BuildTree(TestNode project, IEnumerable<string> names)
    {
        var byId = new Dictionary<string, TestNode>(StringComparer.Ordinal);
        foreach (var full in names)
        {
            var paren = full.IndexOf('(', StringComparison.Ordinal);
            var head = paren < 0 ? full : full[..paren];
            var dot = head.LastIndexOf('.');
            var classFull = dot < 0 ? "" : head[..dot];
            var method = dot < 0 ? full : full[(dot + 1)..];
            var cdot = classFull.LastIndexOf('.');
            var ns = cdot < 0 ? "" : classFull[..cdot];
            var cls = cdot < 0 ? classFull : classFull[(cdot + 1)..];
            var parent = project;
            if (ns.Length > 0) parent = AdapterSupport.Group(parent, byId, project.Id + "|ns|" + ns, ns);
            if (cls.Length > 0) parent = AdapterSupport.Group(parent, byId, project.Id + "|cls|" + classFull, cls, ns);
            parent.Add(new TestNode(project.Id + "|" + full, method, TestKind.Test) { Key = full });
        }
    }

    public async Task RunAsync(TestWorkspace ws, TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var folder = Path.GetDirectoryName(project.ProjectPath)!;
        var results = Path.Combine(ws.WorkFolder, "trx-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(results);
        try
        {
            var args = new List<string> { "test", project.ProjectPath, "--nologo", "--logger", "trx;LogFileName=results.trx", "--results-directory", results };
            if (!request.All && Filter(request.Tests) is { Length: > 0 } filter)
            {
                args.Add("--filter");
                args.Add(filter);
            }

            var run = await ToolRunner.RunAsync(ws.Tools, "dotnet", args, folder, sink.Output, ct).ConfigureAwait(false);
            var trx = Path.Combine(results, "results.trx");
            if (!File.Exists(trx))
            {
                // The run never got to run tests: the build failed, or dotnet was not there.
                var why = run.StartError ?? AdapterSupport.FirstError(run.Lines) ?? "The test run produced no results";
                foreach (var t in request.All ? project.Leaves : request.Tests.SelectMany(x => x.Leaves))
                {
                    sink.Result(new TestResult(t.Id, TestState.Failed, Message: why));
                }

                return;
            }

            foreach (var r in ReadTrx(XDocument.Load(trx), project.Id)) sink.Result(r);
        }
        finally
        {
            try
            {
                Directory.Delete(results, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The VSTest filter for a set of tests: a class whole by prefix, other tests by name.</summary>
    public static string Filter(ImmutableArray<TestNode> tests)
    {
        var parts = new List<string>();
        var leaves = tests.SelectMany(t => t.Leaves).DistinctBy(l => l.Id).ToList();
        foreach (var byClass in leaves.GroupBy(l => ClassOf(l.Key)))
        {
            var cls = byClass.Key;
            var classNode = byClass.First().Parent;
            var whole = classNode is not null && classNode.Id.EndsWith("|cls|" + cls, StringComparison.Ordinal) && classNode.Leaves.All(l => byClass.Any(b => b.Id == l.Id));
            if (whole && cls.Length > 0) parts.Add("FullyQualifiedName~" + Escape(cls + "."));
            else parts.AddRange(byClass.Select(l => "FullyQualifiedName=" + Escape(BaseName(l.Key))).Distinct());
        }

        return string.Join('|', parts);
    }

    private static string BaseName(string full) => full.IndexOf('(', StringComparison.Ordinal) is var p and >= 0 ? full[..p] : full;

    private static string ClassOf(string full)
    {
        var head = BaseName(full);
        var dot = head.LastIndexOf('.');
        return dot < 0 ? "" : head[..dot];
    }

    private static string Escape(string s) => Regex.Replace(s, @"([\\()&|=!~])", @"\$1");

    /// <summary>The results in a TRX document, one per test, for the project whose node ids start with <paramref name="projectId"/>.</summary>
    public static IEnumerable<TestResult> ReadTrx(XDocument doc, string projectId)
    {
        foreach (var r in doc.Descendants(Trx + "UnitTestResult"))
        {
            var name = (string?)r.Attribute("testName");
            if (name is null) continue;
            var outcome = (string?)r.Attribute("outcome");
            var state = outcome switch
            {
                "Passed" => TestState.Passed,
                "Failed" or "Error" or "Timeout" or "Aborted" => TestState.Failed,
                "NotExecuted" or "Inconclusive" or "Skipped" => TestState.Skipped,
                _ => TestState.Unknown,
            };
            TimeSpan? duration = TimeSpan.TryParse((string?)r.Attribute("duration"), CultureInfo.InvariantCulture, out var d) ? d : null;
            var info = r.Element(Trx + "Output");
            var error = info?.Element(Trx + "ErrorInfo");
            var message = (string?)error?.Element(Trx + "Message");
            var stack = (string?)error?.Element(Trx + "StackTrace");
            var stdout = (string?)info?.Element(Trx + "StdOut");
            yield return new TestResult(projectId + "|" + name, state, duration, message?.Trim(), stack?.Trim(), stdout?.Trim());
        }
    }

    public Task<(string File, int Line)?> LocateAsync(TestNode node, CancellationToken ct) => Task.Run(() =>
    {
        var folder = Path.GetDirectoryName(node.Root.ProjectPath)!;
        if (node.Kind == TestKind.Test)
        {
            var cls = node.Parent;
            var method = BaseName(node.Key).Split('.')[^1];
            var inClass = cls is null ? null : AdapterSupport.Search(folder, "*.cs", new Regex($@"\bclass\s+{Regex.Escape(cls.Name)}\b", RegexOptions.CultureInvariant), ct);
            if (inClass is { } c)
            {
                var lines = File.ReadAllLines(c.File);
                var rx = new Regex($@"\b{Regex.Escape(method)}\s*\(", RegexOptions.CultureInvariant);
                for (var i = c.Line; i < lines.Length; i++)
                {
                    if (rx.IsMatch(lines[i])) return ((string File, int Line)?)(c.File, i + 1);
                }

                return c;
            }

            return AdapterSupport.Search(folder, "*.cs", new Regex($@"\b{Regex.Escape(method)}\s*\(", RegexOptions.CultureInvariant), ct);
        }

        if (node.Kind == TestKind.Group && node.Parent is not null && node.Children.Any(c => c.Kind == TestKind.Test))
        {
            return AdapterSupport.Search(folder, "*.cs", new Regex($@"\bclass\s+{Regex.Escape(node.Name)}\b", RegexOptions.CultureInvariant), ct);
        }

        return node.Kind == TestKind.Project ? (node.ProjectPath, 1) : null;
    }, ct);
}
