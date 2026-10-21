using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Wicked.Core.Testing;

public enum TestState
{
    /// <summary>Not run in this session.</summary>
    Unknown,

    Queued,
    Running,
    Passed,
    Failed,
    Skipped,
}

public enum TestKind
{
    /// <summary>A test project (a csproj, a crate, a module, a package.json).</summary>
    Project,

    /// <summary>Namespace, class, module, package or file: something that holds tests.</summary>
    Group,

    Test,
}

/// <summary>
/// One row of the test tree. The tree is owned by the test service, which changes it under its lock; readers get it
/// flattened.
/// </summary>
public sealed class TestNode
{
    private readonly List<TestNode> _children = [];

    public TestNode(string id, string name, TestKind kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
    }

    /// <summary>Stable across discoveries, so results survive a refresh.</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>A quieter line beside the name (the namespace of a class).</summary>
    public string Detail { get; init; } = "";

    public TestKind Kind { get; }

    public TestNode? Parent { get; private set; }

    public IReadOnlyList<TestNode> Children => _children;

    /// <summary>What the framework calls this test on its command line (a full name, a node id).</summary>
    public string Key { get; init; } = "";

    /// <summary>The adapter that discovered and runs this project (set on project nodes).</summary>
    public ITestAdapter? Adapter { get; init; }

    /// <summary>The folder or file of a project, whatever its adapter needs to run it.</summary>
    public string ProjectPath { get; init; } = "";

    public string? File { get; set; }

    /// <summary>1-based; 0 when unknown.</summary>
    public int Line { get; set; }

    public TestState State { get; set; }

    public TimeSpan? Duration { get; set; }

    public string? Message { get; set; }

    public string? Stack { get; set; }

    public string? Output { get; set; }

    public TestNode Root => Parent is null ? this : Parent.Root;

    public IEnumerable<TestNode> Leaves => Kind == TestKind.Test && _children.Count == 0 ? [this] : _children.SelectMany(c => c.Leaves);

    public void Add(TestNode child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    public void SortChildren() => _children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The worst state among the tests below (a group fails when any test in it fails).</summary>
    public TestState Rollup()
    {
        if (Kind == TestKind.Test && _children.Count == 0) return State;
        var states = Leaves.Select(l => l.State).ToList();
        if (states.Count == 0) return State;
        if (states.Contains(TestState.Running)) return TestState.Running;
        if (states.Contains(TestState.Failed)) return TestState.Failed;
        if (states.Contains(TestState.Queued)) return TestState.Queued;
        if (states.All(s => s == TestState.Unknown)) return TestState.Unknown;
        if (states.Contains(TestState.Passed)) return states.Contains(TestState.Unknown) ? TestState.Unknown : TestState.Passed;
        return states.All(s => s is TestState.Skipped or TestState.Unknown) ? TestState.Skipped : TestState.Unknown;
    }
}

/// <summary>One test's result as an adapter reads it from the framework's output.</summary>
/// <param name="Id">The node's id.</param>
/// <param name="State">How the test ended.</param>
/// <param name="Duration">How long it took, when the framework says.</param>
/// <param name="Message">The failure message.</param>
/// <param name="Stack">Where it failed (a stack trace, a panic location).</param>
/// <param name="Output">What the test printed.</param>
/// <param name="ParentId">For a test the discovery did not list (a subtest, a jest case): the node it belongs under.</param>
/// <param name="Name">The new node's name, with <paramref name="ParentId"/>.</param>
public sealed record TestResult(string Id, TestState State, TimeSpan? Duration = null, string? Message = null, string? Stack = null, string? Output = null, string? ParentId = null, string? Name = null);

/// <summary>What a test run receives from an adapter as it reads.</summary>
public interface ITestRunSink
{
    void Result(TestResult result);

    /// <summary>A line of the tool's own output.</summary>
    void Output(string line);
}

/// <summary>What to run: the project, and the tests in it; an empty list is the whole project.</summary>
public sealed record TestRunRequest(TestNode Project, ImmutableArray<TestNode> Tests, bool All);

/// <summary>The folder the tests of a workspace are found in, the tools they run with, and a folder for their result files.</summary>
public sealed record TestWorkspace(string Root, ToolEnv Tools, string WorkFolder);

/// <summary>A test framework: finds its projects and tests, and runs them.</summary>
public interface ITestAdapter
{
    string Name { get; }

    /// <summary>The project nodes found under the workspace, each with its tests below it. Called on a worker.</summary>
    Task<IReadOnlyList<TestNode>> DiscoverAsync(TestWorkspace workspace, CancellationToken ct);

    Task RunAsync(TestWorkspace workspace, TestRunRequest request, ITestRunSink sink, CancellationToken ct);

    /// <summary>Where a node's code is, when the framework did not say: its file and 1-based line, or null.</summary>
    Task<(string File, int Line)?> LocateAsync(TestNode node, CancellationToken ct);
}

/// <summary>A place in a failure's text that names a source location.</summary>
public sealed record SourceLocation(string File, int Line, int Column);

/// <summary>Finds "file:line" in the text a failing test prints, so a stack trace can be clicked.</summary>
public static partial class TestLocations
{
    [GeneratedRegex(@"\sin\s(?<f>.+?):line\s(?<l>\d+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DotnetFrame();

    [GeneratedRegex(@"(?<f>(?:[A-Za-z]:)?[^\s:()'""<>|\[\]]+\.[A-Za-z0-9]+):(?<l>\d+)(?::(?<c>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex Generic();

    /// <summary>The first location in a line that names a file that exists (relative names are tried under each folder in order).</summary>
    public static SourceLocation? Find(string line, IEnumerable<string> folders)
    {
        var dotnet = DotnetFrame().Match(line);
        if (dotnet.Success && Resolve(dotnet.Groups["f"].Value, folders) is { } f) return new SourceLocation(f, int.Parse(dotnet.Groups["l"].Value, System.Globalization.CultureInfo.InvariantCulture), 1);
        foreach (Match m in Generic().Matches(line))
        {
            if (Resolve(m.Groups["f"].Value, folders) is not { } file) continue;
            var column = m.Groups["c"].Success ? int.Parse(m.Groups["c"].Value, System.Globalization.CultureInfo.InvariantCulture) : 1;
            return new SourceLocation(file, int.Parse(m.Groups["l"].Value, System.Globalization.CultureInfo.InvariantCulture), column);
        }

        return null;
    }

    private static string? Resolve(string name, IEnumerable<string> folders)
    {
        if (Path.IsPathRooted(name)) return System.IO.File.Exists(name) ? name : null;
        foreach (var folder in folders)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(folder, name));
                if (System.IO.File.Exists(full)) return full;
            }
            catch (ArgumentException)
            {
                // Not a path.
            }
        }

        return null;
    }
}
