using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wicked.Core.Tasks;

/// <summary>
/// .vscode/tasks.json in VS Code's 2.0.0 shape (comments and trailing commas allowed), read into
/// <see cref="TaskDefinition"/>s with the current platform's overrides applied. Anything the file says that cannot be used
/// is reported in <see cref="TasksFileContent.Problems"/> and the rest of the file still loads.
/// </summary>
public static class TasksFile
{
    private static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string CurrentPlatform => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string PathIn(string workspaceFolder) => Path.Combine(workspaceFolder, ".vscode", "tasks.json");

    public static async Task<TasksFileContent> ReadAsync(string workspaceFolder, CancellationToken ct)
    {
        var path = PathIn(workspaceFolder);
        if (!File.Exists(path)) return TasksFileContent.Empty;
        try
        {
            return Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TasksFileContent.Empty with { Problems = [$"tasks.json could not be read: {ex.Message}"] };
        }
    }

    public static TasksFileContent Parse(string json, string? platform = null)
    {
        platform ??= CurrentPlatform;
        if (string.IsNullOrWhiteSpace(json)) return TasksFileContent.Empty;
        var problems = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, Options);
        }
        catch (JsonException ex)
        {
            return TasksFileContent.Empty with { Problems = [$"tasks.json could not be read: {ex.Message}"] };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return TasksFileContent.Empty with { Problems = ["tasks.json must hold an object"] };
            if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "0.1.0")
            {
                problems.Add("tasks.json is in the old 0.1.0 shape; change \"version\" to \"2.0.0\" and list the tasks under \"tasks\"");
            }

            var inputs = ReadInputs(root, problems);
            var globalOptions = new Layered(root, root.TryGetProperty(platform, out var gp) && gp.ValueKind == JsonValueKind.Object ? gp : null);
            var tasks = ImmutableArray.CreateBuilder<TaskDefinition>();
            if (root.TryGetProperty("tasks", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in list.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object) continue;
                    var layered = new Layered(element, element.TryGetProperty(platform, out var p) && p.ValueKind == JsonValueKind.Object ? p : null);
                    if (ReadTask(layered, globalOptions, problems) is { } task) tasks.Add(task);
                }
            }

            return new TasksFileContent(tasks.ToImmutable(), inputs, [.. problems]);
        }
    }

    /// <summary>A task object and its platform override: a property is read from the override first.</summary>
    private readonly struct Layered(JsonElement baseElement, JsonElement? over)
    {
        public bool TryGet(string name, out JsonElement value)
        {
            if (over is { } o && o.TryGetProperty(name, out value)) return true;
            return baseElement.TryGetProperty(name, out value);
        }

        public string? String(string name) => TryGet(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public bool? Bool(string name) => TryGet(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
    }

    private static ImmutableDictionary<string, TaskInput> ReadInputs(JsonElement root, List<string> problems)
    {
        var inputs = ImmutableDictionary.CreateBuilder<string, TaskInput>(StringComparer.Ordinal);
        if (!root.TryGetProperty("inputs", out var list) || list.ValueKind != JsonValueKind.Array) return inputs.ToImmutable();
        foreach (var item in list.EnumerateArray())
        {
            var id = Str(item, "id");
            var type = Str(item, "type");
            if (id is null || type is null)
            {
                problems.Add("An input needs an \"id\" and a \"type\"");
                continue;
            }

            if (type is not ("promptString" or "pickString"))
            {
                problems.Add($"Input '{id}' has type '{type}', which is not supported (promptString and pickString are)");
                continue;
            }

            var options = ImmutableArray.CreateBuilder<(string, string)>();
            if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray())
                {
                    if (o.ValueKind == JsonValueKind.String) options.Add((o.GetString()!, o.GetString()!));
                    else if (o.ValueKind == JsonValueKind.Object && Str(o, "value") is { } v) options.Add((v, Str(o, "label") ?? v));
                }
            }

            inputs[id] = new TaskInput(id, type, Str(item, "description") ?? id, Str(item, "default"), options.ToImmutable(), item.TryGetProperty("password", out var pw) && pw.ValueKind == JsonValueKind.True);
        }

        return inputs.ToImmutable();
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static TaskDefinition? ReadTask(Layered t, Layered global, List<string> problems)
    {
        var type = t.String("type") ?? "shell";
        var label = t.String("label");
        if (label is null && type == "npm" && t.String("script") is { } named) label = "npm: " + named;
        if (string.IsNullOrWhiteSpace(label))
        {
            problems.Add("A task without a \"label\" was skipped");
            return null;
        }

        TaskArgument? command = null;
        ImmutableArray<TaskArgument> args = [];
        string? cwd = null;
        var kind = TaskType.Shell;
        if (type == "npm")
        {
            var script = t.String("script");
            if (script is null)
            {
                problems.Add($"Task '{label}' has type npm but no \"script\"");
                return null;
            }

            command = new TaskArgument("npm run " + script, QuoteMode.Escape);
            cwd = t.String("path");
            if (cwd is not null) cwd = Path.Combine("${workspaceFolder}", cwd);
        }
        else if (type is "shell" or "process")
        {
            kind = type == "process" ? TaskType.Process : TaskType.Shell;
            command = ReadCommand(t);
            if (command is null)
            {
                problems.Add($"Task '{label}' has no \"command\"");
                return null;
            }

            args = ReadArgs(t);
        }
        else
        {
            problems.Add($"Task '{label}' has type '{type}', which this build does not provide (use \"shell\" or \"process\")");
            return null;
        }

        var env = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        ShellOptions? shell = null;
        foreach (var source in new[] { global, t })
        {
            if (!source.TryGet("options", out var o) || o.ValueKind != JsonValueKind.Object) continue;
            if (Str(o, "cwd") is { } c) cwd = c;
            if (o.TryGetProperty("env", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String) env[p.Name] = p.Value.GetString()!;
                    else if (p.Value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) env[p.Name] = p.Value.GetRawText();
                }
            }

            if (o.TryGetProperty("shell", out var s) && s.ValueKind == JsonValueKind.Object && Str(s, "executable") is { } exe)
            {
                var shellArgs = s.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                    ? [.. a.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)]
                    : ImmutableArray<string>.Empty;
                shell = new ShellOptions(exe, shellArgs);
            }
        }

        var group = TaskGroup.None;
        var isDefault = false;
        if (t.TryGet("group", out var g))
        {
            var name = g.ValueKind == JsonValueKind.String ? g.GetString() : g.ValueKind == JsonValueKind.Object ? Str(g, "kind") : null;
            group = name switch { "build" => TaskGroup.Build, "test" => TaskGroup.Test, _ => TaskGroup.None };
            if (g.ValueKind == JsonValueKind.Object && g.TryGetProperty("isDefault", out var d)) isDefault = d.ValueKind == JsonValueKind.True;
        }

        var depends = ImmutableArray.CreateBuilder<string>();
        if (t.TryGet("dependsOn", out var dep))
        {
            if (dep.ValueKind == JsonValueKind.String) depends.Add(dep.GetString()!);
            else if (dep.ValueKind == JsonValueKind.Array)
            {
                foreach (var x in dep.EnumerateArray())
                {
                    if (x.ValueKind == JsonValueKind.String) depends.Add(x.GetString()!);
                }
            }
        }

        var presentation = new TaskPresentation();
        if (t.TryGet("presentation", out var pr) && pr.ValueKind == JsonValueKind.Object)
        {
            presentation = new TaskPresentation(
                Str(pr, "reveal") switch { "silent" => RevealMode.Silent, "never" => RevealMode.Never, _ => RevealMode.Always },
                pr.TryGetProperty("clear", out var cl) && cl.ValueKind == JsonValueKind.True,
                pr.TryGetProperty("focus", out var fo) && fo.ValueKind == JsonValueKind.True,
                !(pr.TryGetProperty("echo", out var ec) && ec.ValueKind == JsonValueKind.False));
        }

        var matchers = ImmutableArray.CreateBuilder<ProblemMatcher>();
        if (t.TryGet("problemMatcher", out var pm)) ReadMatchers(pm, label, matchers, problems);
        var runOnOpen = t.TryGet("runOptions", out var ro) && ro.ValueKind == JsonValueKind.Object && Str(ro, "runOn") == "folderOpen";

        return new TaskDefinition
        {
            Label = label,
            Type = kind,
            Command = command,
            Args = args,
            Cwd = cwd,
            Env = env.ToImmutable(),
            Shell = shell,
            Group = group,
            IsDefault = isDefault,
            DependsOn = depends.ToImmutable(),
            DependsOrder = t.String("dependsOrder") == "sequence" ? DependsOrder.Sequence : DependsOrder.Parallel,
            Presentation = presentation,
            IsBackground = t.Bool("isBackground") ?? false,
            Matchers = matchers.ToImmutable(),
            RunOnFolderOpen = runOnOpen,
            Detail = type == "npm" ? "npm" : "",
        };
    }

    private static TaskArgument? ReadCommand(Layered t)
    {
        if (!t.TryGet("command", out var c)) return null;
        return c.ValueKind switch
        {
            JsonValueKind.String => new TaskArgument(c.GetString()!),
            JsonValueKind.Object when Str(c, "value") is { } v => new TaskArgument(v, Quoting(Str(c, "quoting"))),
            JsonValueKind.Array => new TaskArgument(string.Join(' ', c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())), QuoteMode.Escape),
            _ => null,
        };
    }

    private static ImmutableArray<TaskArgument> ReadArgs(Layered t)
    {
        if (!t.TryGet("args", out var a) || a.ValueKind != JsonValueKind.Array) return [];
        var list = ImmutableArray.CreateBuilder<TaskArgument>();
        foreach (var x in a.EnumerateArray())
        {
            if (x.ValueKind == JsonValueKind.String) list.Add(new TaskArgument(x.GetString()!));
            else if (x.ValueKind == JsonValueKind.Object && Str(x, "value") is { } v) list.Add(new TaskArgument(v, Quoting(Str(x, "quoting"))));
            else if (x.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) list.Add(new TaskArgument(x.GetRawText()));
        }

        return list.ToImmutable();
    }

    private static QuoteMode Quoting(string? name) => name switch { "strong" => QuoteMode.Strong, "weak" => QuoteMode.Weak, _ => QuoteMode.Escape };

    // ── Problem matchers ─────────────────────────────────────────────────────────────────────────

    private static void ReadMatchers(JsonElement e, string label, ImmutableArray<ProblemMatcher>.Builder into, List<string> problems)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                {
                    var name = e.GetString()!;
                    if (BuiltInMatchers.Find(name) is { } found) into.Add(found);
                    else problems.Add($"Task '{label}' names the problem matcher '{name}', which is not known");
                    break;
                }

            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) ReadMatchers(x, label, into, problems);
                break;
            case JsonValueKind.Object:
                if (ReadMatcher(e, label, problems) is { } m) into.Add(m);
                break;
        }
    }

    private static ProblemMatcher? ReadMatcher(JsonElement e, string label, List<string> problems)
    {
        ProblemMatcher? basis = null;
        if (Str(e, "base") is { } baseName)
        {
            basis = BuiltInMatchers.Find(baseName);
            if (basis is null)
            {
                problems.Add($"Task '{label}': the problem matcher's base '{baseName}' is not known");
                return null;
            }
        }

        var owner = Str(e, "owner") ?? basis?.Owner ?? "external";
        var severity = Str(e, "severity") switch
        {
            "error" => ProblemSeverity.Error,
            "warning" => ProblemSeverity.Warning,
            "info" => ProblemSeverity.Info,
            _ => basis?.DefaultSeverity ?? ProblemSeverity.Error,
        };
        var location = basis?.FileLocation ?? FileLocationKind.Relative;
        var prefix = basis?.FilePrefix;
        if (e.TryGetProperty("fileLocation", out var fl))
        {
            var kind = fl.ValueKind == JsonValueKind.String ? fl.GetString() : fl.ValueKind == JsonValueKind.Array && fl.GetArrayLength() > 0 ? fl[0].GetString() : null;
            location = kind switch
            {
                "absolute" => FileLocationKind.Absolute,
                "autoDetect" or "search" => FileLocationKind.AutoDetect,
                "relative" => FileLocationKind.Relative,
                _ => location,
            };
            if (fl.ValueKind == JsonValueKind.Array && fl.GetArrayLength() > 1) prefix = fl[1].GetString();
        }

        var patterns = basis?.Patterns ?? [];
        if (e.TryGetProperty("pattern", out var pat))
        {
            var read = ReadPatterns(pat, label, problems);
            if (read.IsDefaultOrEmpty) return null;
            patterns = read;
        }

        if (patterns.IsDefaultOrEmpty)
        {
            problems.Add($"Task '{label}': a problem matcher needs a \"pattern\"");
            return null;
        }

        var background = basis?.Background;
        var bgKey = e.TryGetProperty("background", out var bg) ? bg : e.TryGetProperty("watching", out var wt) ? wt : (JsonElement?)null;
        if (bgKey is { ValueKind: JsonValueKind.Object } b)
        {
            try
            {
                var begins = PatternText(b, "beginsPattern");
                var ends = PatternText(b, "endsPattern");
                if (begins is not null && ends is not null)
                {
                    background = new BackgroundSpec(b.TryGetProperty("activeOnStart", out var ao) && ao.ValueKind == JsonValueKind.True, ProblemMatcher.Rx(begins), ProblemMatcher.Rx(ends));
                }
            }
            catch (ArgumentException ex)
            {
                problems.Add($"Task '{label}': a background pattern is not a valid regular expression: {ex.Message}");
            }
        }

        return new ProblemMatcher("", owner, severity, location, prefix, patterns, background);
    }

    private static string? PatternText(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var p)) return null;
        return p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind == JsonValueKind.Object ? Str(p, "regexp") : null;
    }

    private static ImmutableArray<MatcherPattern> ReadPatterns(JsonElement e, string label, List<string> problems)
    {
        if (e.ValueKind == JsonValueKind.String)
        {
            var name = e.GetString()!;
            if (BuiltInMatchers.Find(name) is { } known) return known.Patterns;
            problems.Add($"Task '{label}': the problem pattern '{name}' is not known");
            return [];
        }

        var items = e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToList() : [e];
        var list = ImmutableArray.CreateBuilder<MatcherPattern>();
        foreach (var item in items)
        {
            if (item.ValueKind != JsonValueKind.Object || Str(item, "regexp") is not { } regexp)
            {
                problems.Add($"Task '{label}': a problem pattern needs a \"regexp\"");
                return [];
            }

            Regex rx;
            try
            {
                rx = ProblemMatcher.Rx(regexp);
            }
            catch (ArgumentException ex)
            {
                problems.Add($"Task '{label}': the pattern '{regexp}' is not a valid regular expression: {ex.Message}");
                return [];
            }

            int Group(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            list.Add(new MatcherPattern(rx, Group("file"), Group("location"), Group("line"), Group("column"), Group("endLine"), Group("endColumn"), Group("severity"), Group("code"), Group("message"),
                item.TryGetProperty("loop", out var loop) && loop.ValueKind == JsonValueKind.True));
        }

        return list.ToImmutable();
    }

    // ── Writing ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A task as the text of its tasks.json entry, at the indent it sits at in the file.</summary>
    public static string Render(TaskDefinition task, string indent = "    ")
    {
        var lines = new List<string> { $"\"label\": {Quote(task.Label)}" };
        lines.Add($"\"type\": {Quote(task.Type == TaskType.Process ? "process" : "shell")}");
        if (task.Command is not null) lines.Add($"\"command\": {Quote(task.Command.Value)}");
        if (task.Args.Length > 0) lines.Add($"\"args\": [{string.Join(", ", task.Args.Select(a => Quote(a.Value)))}]");
        if (task.Cwd is not null || task.Env.Count > 0)
        {
            var options = new List<string>();
            if (task.Cwd is not null) options.Add($"\"cwd\": {Quote(task.Cwd)}");
            if (task.Env.Count > 0) options.Add($"\"env\": {{ {string.Join(", ", task.Env.Select(kv => $"{Quote(kv.Key)}: {Quote(kv.Value)}"))} }}");
            lines.Add($"\"options\": {{ {string.Join(", ", options)} }}");
        }

        if (task.Group != TaskGroup.None)
        {
            var name = task.Group == TaskGroup.Build ? "build" : "test";
            lines.Add(task.IsDefault ? $"\"group\": {{ \"kind\": \"{name}\", \"isDefault\": true }}" : $"\"group\": \"{name}\"");
        }

        if (task.IsBackground) lines.Add("\"isBackground\": true");
        var named = task.Matchers.Where(m => m.Name.Length > 0).Select(m => Quote(m.Name)).ToList();
        if (named.Count > 0) lines.Add($"\"problemMatcher\": [{string.Join(", ", named)}]");
        var inner = indent + "  ";
        return indent + "{\n" + string.Join(",\n", lines.Select(l => inner + l)) + "\n" + indent + "}";
    }

    private static string Quote(string s) => JsonSerializer.Serialize(s);

    /// <summary>A new tasks.json holding one task.</summary>
    public static string Starter(TaskDefinition task) =>
        "{\n  // Tasks for this folder, in VS Code's format: run them from the palette (Tasks: Run Task) or with Ctrl+Shift+B.\n  \"version\": \"2.0.0\",\n  \"tasks\": [\n" + Render(task) + "\n  ]\n}\n";

    /// <summary>
    /// An existing tasks.json with <paramref name="task"/> added to its "tasks" list, everything else (comments included)
    /// left as it was. Null when the text cannot be read as a tasks file.
    /// </summary>
    public static string? AddTask(string existing, TaskDefinition task)
    {
        var bytes = Encoding.UTF8.GetBytes(existing);
        // Offsets below are in bytes; the text is cut by character, so the conversion goes through the byte array.
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
            var depth = 0;
            var rootHasProperties = false;
            long rootStart = -1;
            long tasksEnd = -1;
            long lastEnd = -1;
            var elements = 0;
            var inTasks = false;
            string? rootProperty = null;
            while (reader.Read())
            {
                var type = reader.TokenType;
                if (type == JsonTokenType.Comment) continue;
                var before = depth;
                if (type is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                if (type is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
                if (before == 0 && type == JsonTokenType.StartObject) rootStart = reader.TokenStartIndex;
                if (before == 1 && type == JsonTokenType.PropertyName)
                {
                    rootHasProperties = true;
                    rootProperty = reader.GetString();
                    continue;
                }

                if (before == 1 && type == JsonTokenType.StartArray && rootProperty == "tasks" && tasksEnd < 0)
                {
                    inTasks = true;
                    lastEnd = reader.BytesConsumed;
                    continue;
                }

                if (!inTasks) continue;
                if (before == 2 && type == JsonTokenType.EndArray)
                {
                    tasksEnd = reader.TokenStartIndex;
                    inTasks = false;
                    continue;
                }

                if (before == 2) elements++;
                if (depth == 2) lastEnd = reader.BytesConsumed;
            }

            string Slice(long from, long to) => Encoding.UTF8.GetString(bytes, (int)from, (int)(to - from));
            var obj = Render(task);
            if (tasksEnd >= 0)
            {
                if (elements == 0) return Slice(0, lastEnd) + "\n" + obj + "\n  " + Slice(tasksEnd, bytes.Length);
                var gap = Slice(lastEnd, tasksEnd);
                var comma = CodeComma(gap);
                if (comma < 0) return Slice(0, lastEnd) + "," + gap.TrimEnd() + "\n" + obj + "\n  " + Slice(tasksEnd, bytes.Length);
                var cut = lastEnd + Encoding.UTF8.GetByteCount(gap[..(comma + 1)]);
                return Slice(0, cut) + "\n" + obj + Slice(cut, bytes.Length);
            }

            if (rootStart < 0) return null;
            var block = "\n  \"tasks\": [\n" + obj + "\n  ]" + (rootHasProperties ? "," : "");
            return Slice(0, rootStart + 1) + block + Slice(rootStart + 1, bytes.Length);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The index of the first comma in a stretch of JSON text that is not inside a comment; -1 when none.</summary>
    private static int CodeComma(string gap)
    {
        for (var i = 0; i < gap.Length; i++)
        {
            if (gap[i] == '/' && i + 1 < gap.Length && gap[i + 1] == '/')
            {
                while (i < gap.Length && gap[i] != '\n') i++;
            }
            else if (gap[i] == '/' && i + 1 < gap.Length && gap[i + 1] == '*')
            {
                var end = gap.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + 1;
            }
            else if (gap[i] == ',')
            {
                return i;
            }
        }

        return -1;
    }
}
