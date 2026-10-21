using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wicked.Core.Tasks;

/// <summary>
/// Tasks found by looking at a project rather than reading tasks.json: npm scripts, make targets, cargo, dotnet, go and
/// CMake presets. Reads small files only, so it runs in a blink, but it touches the disk: call it off the UI thread.
/// </summary>
public static partial class TaskDetector
{
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.Ordinal) { "node_modules", ".git", "bin", "obj", "target", "dist", "build", "out", ".venv", "venv", "vendor", ".vs", ".idea" };

    [GeneratedRegex(@"^([A-Za-z0-9_][A-Za-z0-9_.\-/]*)(?:\s+[A-Za-z0-9_.\-/]+)*\s*:(?![=:])", RegexOptions.CultureInvariant)]
    private static partial Regex MakeTarget();

    public static Task<ImmutableArray<TaskDefinition>> DetectAsync(string root, CancellationToken ct) =>
        Task.Run(() => DetectCore(root, ct), ct);

    public static ImmutableArray<TaskDefinition> Detect(string root) => DetectCore(root, CancellationToken.None);

    private static ImmutableArray<TaskDefinition> DetectCore(string root, CancellationToken ct)
    {
        var found = ImmutableArray.CreateBuilder<TaskDefinition>();
        if (!Directory.Exists(root)) return [];
        foreach (var folder in Folders(root))
        {
            ct.ThrowIfCancellationRequested();
            var suffix = string.Equals(folder, root, StringComparison.Ordinal) ? "" : " - " + Path.GetRelativePath(root, folder);
            var cwd = string.Equals(folder, root, StringComparison.Ordinal) ? null : Path.Combine("${workspaceFolder}", Path.GetRelativePath(root, folder));
            Npm(folder, suffix, cwd, found);
            Make(folder, suffix, cwd, found);
            Cargo(folder, suffix, cwd, found);
            Dotnet(root, folder, suffix, cwd, found);
            Go(folder, suffix, cwd, found);
            CMake(folder, suffix, cwd, found);
        }

        return found.ToImmutable();
    }

    /// <summary>The folder itself and the ones directly under it (a monorepo's packages are one level down).</summary>
    private static IEnumerable<string> Folders(string root)
    {
        yield return root;
        string[] children;
        try
        {
            children = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        Array.Sort(children, StringComparer.Ordinal);
        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (name.StartsWith('.') || SkippedFolders.Contains(name)) continue;
            yield return child;
        }
    }

    private static TaskDefinition Make(string label, string source, string detail, string command, string? cwd, TaskGroup group, ImmutableArray<ProblemMatcher> matchers, bool background = false) => new()
    {
        Label = label,
        Command = new TaskArgument(command),
        Cwd = cwd,
        Group = group,
        Source = source,
        Detail = detail,
        Detected = true,
        Matchers = matchers,
        IsBackground = background,
    };

    private static ImmutableArray<ProblemMatcher> Matcher(string name) => BuiltInMatchers.Find(name) is { } m ? [m] : [];

    // ── npm ──────────────────────────────────────────────────────────────────────────────────────

    private static void Npm(string folder, string suffix, string? cwd, ImmutableArray<TaskDefinition>.Builder into)
    {
        var path = Path.Combine(folder, "package.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object) return;
            var manager = File.Exists(Path.Combine(folder, "pnpm-lock.yaml")) ? "pnpm"
                : File.Exists(Path.Combine(folder, "yarn.lock")) ? "yarn"
                : File.Exists(Path.Combine(folder, "bun.lockb")) || File.Exists(Path.Combine(folder, "bun.lock")) ? "bun"
                : "npm";
            foreach (var script in scripts.EnumerateObject())
            {
                var body = script.Value.ValueKind == JsonValueKind.String ? script.Value.GetString() ?? "" : "";
                var group = script.Name switch
                {
                    "build" or "compile" => TaskGroup.Build,
                    "test" => TaskGroup.Test,
                    _ => TaskGroup.None,
                };
                var matchers = body.Contains("tsc", StringComparison.Ordinal) ? Matcher("$tsc") : body.Contains("eslint", StringComparison.Ordinal) ? Matcher("$eslint-stylish") : [];
                into.Add(Make($"npm: {script.Name}{suffix}", "npm", body, $"{manager} run {script.Name}", cwd, group, matchers));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A package.json that does not parse offers no scripts; the editor's own problems show why.
        }
    }

    // ── make ─────────────────────────────────────────────────────────────────────────────────────

    private static void Make(string folder, string suffix, string? cwd, ImmutableArray<TaskDefinition>.Builder into)
    {
        var file = new[] { "Makefile", "makefile", "GNUmakefile" }.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
        if (file is null) return;
        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] is '\t' or ' ' or '#' or '.' || line.Contains('%', StringComparison.Ordinal) || line.Contains('$', StringComparison.Ordinal) && line.IndexOf('$', StringComparison.Ordinal) < line.IndexOf(':', StringComparison.Ordinal)) continue;
            var m = MakeTarget().Match(line);
            if (!m.Success) continue;
            // "a b c: deps" names three targets.
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            foreach (var target in line[..colon].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (target.StartsWith('.') || target.Contains('$', StringComparison.Ordinal) || target.Contains('%', StringComparison.Ordinal) || !seen.Add(target)) continue;
                var group = target is "all" or "build" ? TaskGroup.Build : target is "test" or "check" ? TaskGroup.Test : TaskGroup.None;
                into.Add(Make($"make: {target}{suffix}", "make", Path.GetFileName(file), target == "all" ? "make" : $"make {target}", cwd, group, Matcher("$gcc")));
            }
        }
    }

    // ── cargo ────────────────────────────────────────────────────────────────────────────────────

    private static void Cargo(string folder, string suffix, string? cwd, ImmutableArray<TaskDefinition>.Builder into)
    {
        if (!File.Exists(Path.Combine(folder, "Cargo.toml"))) return;
        foreach (var (verb, group) in new[] { ("build", TaskGroup.Build), ("test", TaskGroup.Test), ("run", TaskGroup.None), ("check", TaskGroup.None) })
        {
            into.Add(Make($"cargo: {verb}{suffix}", "cargo", "Cargo.toml", $"cargo {verb}", cwd, group, Matcher("$rustc")));
        }
    }

    // ── dotnet ───────────────────────────────────────────────────────────────────────────────────

    private static void Dotnet(string root, string folder, string suffix, string? cwd, ImmutableArray<TaskDefinition>.Builder into)
    {
        string[] solutions = [.. Safe(folder, "*.sln"), .. Safe(folder, "*.slnx")];
        var projects = Safe(folder, "*.csproj").Concat(Safe(folder, "*.fsproj")).Concat(Safe(folder, "*.vbproj")).ToArray();
        // A solution at the root covers the projects under it; a project folder below a root with its own solution adds nothing.
        if (!string.Equals(folder, root, StringComparison.Ordinal) && Safe(root, "*.sln").Concat(Safe(root, "*.slnx")).Any()) return;
        const string Flags = "/property:GenerateFullPaths=true /consoleloggerparameters:NoSummary";
        foreach (var target in solutions.Length > 0 ? solutions : projects)
        {
            var name = Path.GetFileName(target);
            var quoted = name.Contains(' ', StringComparison.Ordinal) ? $"'{name}'" : name;
            into.Add(Make($"dotnet: build {name}{suffix}", "dotnet", name, $"dotnet build {quoted} {Flags}", cwd, TaskGroup.Build, Matcher("$msCompile")));
            into.Add(Make($"dotnet: test {name}{suffix}", "dotnet", name, $"dotnet test {quoted} {Flags}", cwd, TaskGroup.Test, Matcher("$msCompile")));
        }

        foreach (var project in projects.Where(IsRunnable))
        {
            var name = Path.GetFileName(project);
            var quoted = name.Contains(' ', StringComparison.Ordinal) ? $"'{name}'" : name;
            into.Add(Make($"dotnet: run {name}{suffix}", "dotnet", name, $"dotnet run --project {quoted} {Flags}", cwd, TaskGroup.None, Matcher("$msCompile")));
        }
    }

    /// <summary>Whether a project builds a program (an Exe, a web app or a worker) rather than a library.</summary>
    private static bool IsRunnable(string project)
    {
        try
        {
            var text = File.ReadAllText(project);
            return text.Contains("<OutputType>Exe", StringComparison.OrdinalIgnoreCase) || text.Contains("<OutputType>WinExe", StringComparison.OrdinalIgnoreCase)
                || text.Contains("Sdk.Web", StringComparison.Ordinal) || text.Contains("Sdk.Worker", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string[] Safe(string folder, string pattern)
    {
        try
        {
            var files = Directory.GetFiles(folder, pattern);
            Array.Sort(files, StringComparer.Ordinal);
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // ── go ───────────────────────────────────────────────────────────────────────────────────────

    private static void Go(string folder, string suffix, string? cwd, ImmutableArray<TaskDefinition>.Builder into)
    {
        if (!File.Exists(Path.Combine(folder, "go.mod"))) return;
        into.Add(Make($"go: build{suffix}", "go", "go.mod", "go build ./...", cwd, TaskGroup.Build, Matcher("$go")));
        into.Add(Make($"go: test{suffix}", "go", "go.mod", "go test ./...", cwd, TaskGroup.Test, Matcher("$go")));
    }

    // ── CMake presets ────────────────────────────────────────────────────────────────────────────

    private static void CMake(string folder, string suffix, string? cwd, ImmutableArray<TaskDefinition>.Builder into)
    {
        foreach (var name in new[] { "CMakePresets.json", "CMakeUserPresets.json" })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                foreach (var (list, verb, command, group) in new[]
                {
                    ("configurePresets", "configure", "cmake --preset", TaskGroup.None),
                    ("buildPresets", "build", "cmake --build --preset", TaskGroup.Build),
                    ("testPresets", "test", "ctest --preset", TaskGroup.Test),
                })
                {
                    if (!doc.RootElement.TryGetProperty(list, out var presets) || presets.ValueKind != JsonValueKind.Array) continue;
                    foreach (var preset in presets.EnumerateArray())
                    {
                        if (preset.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True) continue;
                        if (!preset.TryGetProperty("name", out var n) || n.GetString() is not { Length: > 0 } presetName) continue;
                        into.Add(Make($"cmake: {verb} {presetName}{suffix}", "cmake", name, $"{command} {presetName}", cwd, group, Matcher("$gcc")));
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Presets that do not parse offer nothing.
            }
        }
    }
}
