using System.Text.Json;

namespace Wicked.Core.Testing;

/// <summary>
/// JavaScript and TypeScript projects that use Jest or Vitest (found in package.json, run from the project's own
/// node_modules/.bin or npx): Jest lists test files, Vitest lists tests; both run with a JSON report that names every test.
/// </summary>
public sealed class JsTestAdapter : ITestAdapter
{
    private static readonly string[] LocalBin = [Path.Combine("node_modules", ".bin")];

    public string Name => "js";

    private static string? Framework(string folder)
    {
        var package = Path.Combine(folder, "package.json");
        if (!File.Exists(package)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(package), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var section in new[] { "devDependencies", "dependencies" })
            {
                if (!doc.RootElement.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
                if (deps.TryGetProperty("vitest", out _)) return "vitest";
                if (deps.TryGetProperty("jest", out _)) return "jest";
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    /// <summary>The program and leading arguments that run a framework: its local binary, else npx.</summary>
    private static (string Program, string[] Lead)? Command(ToolEnv tools, string folder, string framework)
    {
        var exe = OperatingSystem.IsWindows() ? framework + ".cmd" : framework;
        if (tools.Find(exe, folder, LocalBin) is { } local && local.StartsWith(folder, StringComparison.Ordinal)) return (local, []);
        return tools.Find(OperatingSystem.IsWindows() ? "npx.cmd" : "npx", folder) is { } npx ? (npx, ["--no-install", framework]) : null;
    }

    public async Task<IReadOnlyList<TestNode>> DiscoverAsync(TestWorkspace ws, CancellationToken ct)
    {
        var nodes = new List<TestNode>();
        foreach (var folder in AdapterSupport.Folders(ws.Root, 2))
        {
            if (Framework(folder) is not { } framework) continue;
            var project = new TestNode("js:" + folder, Path.GetFileName(folder), TestKind.Project) { Adapter = this, ProjectPath = folder, Detail = framework };
            nodes.Add(project);
            if (Command(ws.Tools, folder, framework) is not { } cmd)
            {
                project.State = TestState.Failed;
                project.Message = $"{framework} could not be started: neither node_modules/.bin/{framework} nor npx was found";
                continue;
            }

            if (framework == "vitest")
            {
                var result = await ToolRunner.RunAsync(ws.Tools, cmd.Program, [.. cmd.Lead, "list", "--json"], folder, null, ct).ConfigureAwait(false);
                ParseVitestList(project, result.Lines);
            }
            else
            {
                var result = await ToolRunner.RunAsync(ws.Tools, cmd.Program, [.. cmd.Lead, "--listTests"], folder, null, ct).ConfigureAwait(false);
                ParseJestFiles(project, result.Lines);
            }
        }

        return nodes;
    }

    /// <summary>Jest's <c>--listTests</c>: one absolute file path a line.</summary>
    public static void ParseJestFiles(TestNode project, IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var path = line.Trim();
            if (path.Length == 0 || !Path.IsPathRooted(path)) continue;
            var relative = Path.GetRelativePath(project.ProjectPath, path);
            project.Add(new TestNode(project.Id + "|" + relative, relative, TestKind.Group) { Key = path, File = path, Line = 1 });
        }
    }

    /// <summary>Vitest's <c>list --json</c>: an array of { name, file } for every test.</summary>
    public static void ParseVitestList(TestNode project, IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        var start = text.IndexOf('[', StringComparison.Ordinal);
        if (start < 0) return;
        try
        {
            using var doc = JsonDocument.Parse(text[start..], new JsonDocumentOptions { AllowTrailingCommas = true });
            var byId = new Dictionary<string, TestNode>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                var file = item.TryGetProperty("file", out var f) ? f.GetString() : null;
                if (name is null || file is null) continue;
                var relative = Path.GetRelativePath(project.ProjectPath, file);
                var group = AdapterSupport.Group(project, byId, project.Id + "|" + relative, relative);
                group.Line = 1;
                var parts = name.Split(" > ");
                var parent = group;
                for (var i = 0; i < parts.Length - 1; i++)
                {
                    parent = AdapterSupport.Group(parent, byId, group.Id + "|" + string.Join(" > ", parts.Take(i + 1)), parts[i]);
                }

                parent.Add(new TestNode(group.Id + "|" + name, parts[^1], TestKind.Test) { Key = name, File = file });
            }
        }
        catch (JsonException)
        {
            // Output that is not the list: no tests found.
        }
    }

    public async Task RunAsync(TestWorkspace ws, TestRunRequest request, ITestRunSink sink, CancellationToken ct)
    {
        var project = request.Project;
        var framework = project.Detail;
        var tests = request.All ? project.Leaves.ToList() : [.. request.Tests.SelectMany(t => t.Leaves).DistinctBy(l => l.Id)];
        if (Command(ws.Tools, project.ProjectPath, framework) is not { } cmd)
        {
            foreach (var t in tests) sink.Result(new TestResult(t.Id, TestState.Failed, Message: $"{framework} could not be started"));
            return;
        }

        var report = Path.Combine(ws.WorkFolder, "js-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var args = new List<string>(cmd.Lead);
            if (framework == "vitest") args.AddRange(["run", "--reporter=json", "--outputFile=" + report]);
            else args.AddRange(["--json", "--outputFile=" + report]);
            if (!request.All)
            {
                var files = (request.Tests.Length == 0 ? tests : request.Tests.SelectMany(AncestorFiles)).Select(t => t.File).Where(f => f is not null).Distinct().ToList();
                if (framework == "jest") args.AddRange(["--runTestsByPath", .. files!]);
                else args.AddRange(files!);
            }

            var run = await ToolRunner.RunAsync(ws.Tools, cmd.Program, args, project.ProjectPath, sink.Output, ct).ConfigureAwait(false);
            if (!File.Exists(report))
            {
                var why = run.StartError ?? string.Join('\n', run.Lines.Where(l => l.Length > 0).TakeLast(12));
                foreach (var t in tests) sink.Result(new TestResult(t.Id, TestState.Failed, Message: why));
                return;
            }

            foreach (var r in ReadReport(File.ReadAllText(report), project)) sink.Result(r);
        }
        finally
        {
            try
            {
                File.Delete(report);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IEnumerable<TestNode> AncestorFiles(TestNode node)
    {
        // A node's file is the test file it lives in: the node's own, or the file group above it.
        for (var n = (TestNode?)node; n is not null; n = n.Parent)
        {
            if (n.File is not null) return [n];
        }

        return node.Leaves;
    }

    /// <summary>A Jest/Vitest JSON report as results; a case the listing did not know becomes a node under its file.</summary>
    public static IEnumerable<TestResult> ReadReport(string json, TestNode project)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("testResults", out var files)) yield break;
            foreach (var file in files.EnumerateArray())
            {
                var path = file.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (path is null) continue;
                var relative = Path.GetRelativePath(project.ProjectPath, path);
                var fileId = project.Id + "|" + relative;
                var any = false;
                if (file.TryGetProperty("assertionResults", out var cases))
                {
                    foreach (var c in cases.EnumerateArray())
                    {
                        any = true;
                        var full = c.TryGetProperty("fullName", out var fn) ? fn.GetString() : null;
                        var title = c.TryGetProperty("title", out var t) ? t.GetString() : null;
                        var ancestors = c.TryGetProperty("ancestorTitles", out var at) ? at.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [];
                        var name = full ?? title ?? "";
                        var state = (c.TryGetProperty("status", out var st) ? st.GetString() : null) switch
                        {
                            "passed" => TestState.Passed,
                            "failed" => TestState.Failed,
                            "pending" or "skipped" or "todo" or "disabled" => TestState.Skipped,
                            _ => TestState.Unknown,
                        };
                        TimeSpan? duration = c.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? TimeSpan.FromMilliseconds(d.GetDouble()) : null;
                        string? message = null;
                        if (c.TryGetProperty("failureMessages", out var fm) && fm.ValueKind == JsonValueKind.Array)
                        {
                            message = string.Join('\n', fm.EnumerateArray().Select(x => x.GetString()));
                        }

                        var stackStart = message?.IndexOf("\n    at ", StringComparison.Ordinal) ?? -1;
                        var shown = message is null ? null : stackStart > 0 ? message[..stackStart].Trim() : message.Trim();
                        var stack = message is null || stackStart <= 0 ? null : message[stackStart..].Trim();
                        yield return new TestResult(ResultId(project, relative, name, ancestors, title), state, duration, shown, stack ?? message, null, fileId, ancestors.Length > 0 ? string.Join(" > ", ancestors.Append(title ?? name)) : title ?? name);
                    }
                }

                var status = file.TryGetProperty("status", out var fs) ? fs.GetString() : null;
                if (!any && status == "failed")
                {
                    yield return new TestResult(fileId, TestState.Failed, null, file.TryGetProperty("message", out var msg) ? msg.GetString() : "The test file failed to run");
                }
            }
        }
    }

    private static string ResultId(TestNode project, string relativeFile, string fullName, string[] ancestors, string? title) =>
        // Vitest's list names a test "a > b > title"; Jest's report gives a fullName with spaces. Both ids are built from the same pieces.
        project.Id + "|" + relativeFile + "|" + (ancestors.Length > 0 ? string.Join(" > ", ancestors.Append(title ?? fullName)) : title ?? fullName);

    public Task<(string File, int Line)?> LocateAsync(TestNode node, CancellationToken ct)
    {
        var file = node.File;
        for (var n = node.Parent; file is null && n is not null; n = n.Parent) file = n.File;
        if (node.Kind == TestKind.Project) return Task.FromResult<(string File, int Line)?>((Path.Combine(node.ProjectPath, "package.json"), 1));
        return Task.FromResult<(string File, int Line)?>(file is null ? null : (file, Math.Max(1, node.Line)));
    }
}
