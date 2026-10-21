using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avalonia.Threading;
using Wicked.App.ViewModels;
using Wicked.Core.Chat;
using Wicked.Core.Search;
using Wicked.Editor.Model;

namespace Wicked.App.Assistant;

/// <summary>
/// The agent's tools in the open folder (D18): read, list, search and problems freely; edit, create and run only with the
/// user's consent (Always remembers it for the session: edits as a kind, commands one by one). Paths are relative to the
/// folder and may not leave it. Edits go through the editors, so each is one undo step the user can take back; every tool
/// runs on a worker and touches the editors on the UI thread.
/// </summary>
public sealed class WorkspaceAgentTools(ShellViewModel shell) : IAgentTools
{
    private const int ReadLineCap = 400;
    private const int OutputCap = 8000;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    private readonly HashSet<string> _allowedCommands = new(StringComparer.Ordinal);
    private bool _editsAllowed;

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public IReadOnlyList<ChatTool> Definitions { get; } =
    [
        new("read_file", "Read a file of the project (relative path). Lines come numbered; ask for a range for long files.",
            Schema("""{"type":"object","properties":{"path":{"type":"string"},"start_line":{"type":"integer","description":"1-based, optional"},"end_line":{"type":"integer","description":"1-based, inclusive, optional"}},"required":["path"]}""")),
        new("list_files", "List the project's files (ignored folders left out), under a folder or matching a glob such as **/*.cs.",
            Schema("""{"type":"object","properties":{"path":{"type":"string","description":"a folder, optional"},"glob":{"type":"string","description":"optional"}}}""")),
        new("search_text", "Search the project's files for text or a regular expression; answers path:line: the line.",
            Schema("""{"type":"object","properties":{"query":{"type":"string"},"regex":{"type":"boolean"},"include":{"type":"string","description":"globs, optional"}},"required":["query"]}""")),
        new("get_problems", "The compiler and linter problems the language servers report, for one file or all.",
            Schema("""{"type":"object","properties":{"path":{"type":"string","description":"optional"}}}""")),
        new("edit_file", "Replace text in a file: old_text must appear exactly once (include enough lines to make it unique); new_text takes its place.",
            Schema("""{"type":"object","properties":{"path":{"type":"string"},"old_text":{"type":"string"},"new_text":{"type":"string"}},"required":["path","old_text","new_text"]}""")),
        new("create_file", "Create a new file with the given content (it must not exist yet).",
            Schema("""{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string"}},"required":["path","content"]}""")),
        new("run_command", "Run a shell command in the project folder (no input; two minutes at most) and get its output and exit code.",
            Schema("""{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}""")),
    ];

    private string Root => shell.Workbench.Root ?? throw new AgentToolException("No folder is open.");

    /// <summary>A relative path inside the folder, as a full path; refused when it would leave the folder.</summary>
    private string Resolve(string relative)
    {
        var root = Path.GetFullPath(Root);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (full != root && !full.StartsWith(prefix, StringComparison.Ordinal)) throw new AgentToolException($"{relative} is outside the project folder.");
        return full;
    }

    private string Relative(string full) => Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');

    public async Task<string> DescribeAsync(CancellationToken ct)
    {
        if (shell.Workbench.Root is not { } root) return "No project folder is open; the tools that read files will refuse.";
        var open = await Dispatcher.UIThread.InvokeAsync(() => shell.Workbench.AllTabs.Where(t => t.Path is not null).Select(t => t.Path!).Distinct().ToList());
        var active = await Dispatcher.UIThread.InvokeAsync(() => shell.Workbench.ActiveTab?.Path);
        var sb = new StringBuilder();
        sb.Append("The project folder is ").Append(Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))).Append(" (paths are relative to it).\n");
        if (active is not null) sb.Append("The file in front of the user: ").Append(Relative(active)).Append('\n');
        if (open.Count > 0) sb.Append("Open files: ").Append(string.Join(", ", open.Select(Relative))).Append('\n');
        foreach (var name in new[] { "AGENTS.md", ".wicked/rules.md", "CLAUDE.md", ".github/copilot-instructions.md" })
        {
            var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var text = await File.ReadAllTextAsync(path, ct);
            sb.Append("\nThe project's rules (").Append(name).Append("):\n").Append(text.Length > 6000 ? text[..6000] : text).Append('\n');
            break;
        }

        return sb.ToString();
    }

    public async Task<string> RunAsync(ChatToolCall call, IAgentSteps steps, CancellationToken ct)
    {
        JsonElement args;
        try
        {
            args = JsonDocument.Parse(call.Arguments.Length == 0 ? "{}" : call.Arguments).RootElement.Clone();
        }
        catch (JsonException)
        {
            return $"The arguments were not valid JSON: {call.Arguments}";
        }

        string Str(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int? Int(string name) => args.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : null;
        bool Bool(string name) => args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        AgentStep? step = null;
        try
        {
            switch (call.Name)
            {
                case "read_file":
                    step = await steps.AddAsync("Icon.FileText", $"Read {Str("path")}");
                    return await Done(steps, step, await ReadAsync(Str("path"), Int("start_line"), Int("end_line"), ct));
                case "list_files":
                    step = await steps.AddAsync("Icon.Files", $"Listed {(Str("glob") is { Length: > 0 } g ? g : Str("path") is { Length: > 0 } p ? p : "the files")}");
                    return await Done(steps, step, await ListAsync(Str("path"), Str("glob")));
                case "search_text":
                    step = await steps.AddAsync("Icon.Search", $"Searched for {Str("query")}");
                    return await Done(steps, step, await SearchAsync(Str("query"), Bool("regex"), Str("include"), ct));
                case "get_problems":
                    step = await steps.AddAsync("Icon.CircleAlert", "Looked at the problems");
                    return await Done(steps, step, await ProblemsAsync(Str("path")));
                case "edit_file":
                    step = await steps.AddAsync("Icon.Pencil", $"Edit {Str("path")}");
                    return await EditAsync(steps, step, Str("path"), Str("old_text"), Str("new_text"), ct);
                case "create_file":
                    step = await steps.AddAsync("Icon.FilePlus", $"Create {Str("path")}");
                    return await CreateAsync(steps, step, Str("path"), Str("content"), ct);
                case "run_command":
                    step = await steps.AddAsync("Icon.Terminal", $"Run {Str("command")}");
                    return await CommandAsync(steps, step, Str("command"), ct);
                default:
                    return $"There is no tool called {call.Name}.";
            }
        }
        catch (AgentToolException ex)
        {
            if (step is not null) await Failed(steps, step, ex.Message);
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (step is not null) await Failed(steps, step, ex.Message);
            return $"{call.Name} failed: {ex.Message}";
        }
    }

    private static async Task<string> Done(IAgentSteps steps, AgentStep step, string result, string? detail = null)
    {
        await steps.UpdateAsync(() =>
        {
            step.State = StepState.Done;
            if (detail is not null) step.Detail = detail;
        });
        return result;
    }

    private static Task Failed(IAgentSteps steps, AgentStep step, string why) => steps.UpdateAsync(() =>
    {
        step.State = StepState.Failed;
        step.Detail = why;
    });

    /// <summary>The text of a file: its editor's when it is open (unsaved edits included), else the disk's.</summary>
    private async Task<string?> TextOfAsync(string full, CancellationToken ct)
    {
        var open = await Dispatcher.UIThread.InvokeAsync(() => shell.Workbench.AllTabs.FirstOrDefault(t => t.Path == full)?.Document.Text);
        if (open is not null) return open.ToString();
        if (!File.Exists(full)) return null;
        var loaded = await Wicked.Core.Files.TextFile.ReadAsync(full, false, ct);
        return loaded.Kind == Wicked.Core.Files.FileKind.Text ? loaded.Text.Replace("\r\n", "\n", StringComparison.Ordinal) : throw new AgentToolException($"{Relative(full)} is not a text file.");
    }

    private async Task<string> ReadAsync(string path, int? from, int? to, CancellationToken ct)
    {
        var full = Resolve(path);
        var text = await TextOfAsync(full, ct) ?? throw new AgentToolException($"{path} does not exist.");
        var lines = text.Split('\n');
        var start = Math.Clamp((from ?? 1) - 1, 0, Math.Max(0, lines.Length - 1));
        var end = Math.Clamp(to ?? start + ReadLineCap, start + 1, lines.Length);
        if (end - start > ReadLineCap) end = start + ReadLineCap;
        var sb = new StringBuilder();
        for (var i = start; i < end; i++) sb.Append(i + 1).Append(": ").Append(lines[i]).Append('\n');
        if (end < lines.Length) sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"… {lines.Length - end} more lines (ask with start_line={end + 1}).\n");
        return sb.ToString();
    }

    private Task<string> ListAsync(string path, string glob) => Task.Run(() =>
    {
        if (shell.Workbench.Index is not { } index) throw new AgentToolException("The project's files have not been listed yet.");
        var under = path.Trim('/', '.');
        var globs = GlobSet.Parse(glob);
        var files = index.Files.Where(f => (under.Length == 0 || f.StartsWith(under + "/", StringComparison.Ordinal)) && (globs.IsEmpty || globs.Matches(f))).ToList();
        var shown = files.Take(400).ToList();
        return string.Join('\n', shown) + (files.Count > shown.Count ? $"\n… and {files.Count - shown.Count} more" : "") + (files.Count == 0 ? "No files match." : "");
    });

    private async Task<string> SearchAsync(string query, bool regex, string include, CancellationToken ct)
    {
        if (shell.Workbench.Index is not { } index) throw new AgentToolException("The project's files have not been listed yet.");
        var root = Root;
        var open = await Dispatcher.UIThread.InvokeAsync(() => shell.Workbench.AllTabs.Where(t => t.Path is not null).DistinctBy(t => t.Path).ToDictionary(t => t.Path!, t =>
        {
            var text = t.Document.Text;
            return (Func<string>)text.ToString;
        }));
        var lines = new List<string>();
        var summary = await ProjectSearch.RunAsync(root, index.Files, new ProjectQuery(query, Regex: regex, Include: include), open, f =>
        {
            lock (lines)
            {
                foreach (var m in f.Matches.Take(10)) lines.Add($"{Relative(f.Path)}:{m.Line + 1}: {m.Preview.Trim()}");
            }
        }, ct);
        if (summary.Error is { } error) throw new AgentToolException(error);
        lines.Sort(StringComparer.Ordinal);
        return lines.Count == 0 ? "No matches." : string.Join('\n', lines.Take(80)) + (lines.Count > 80 ? $"\n… {summary.Matches - 80} more matches" : "");
    }

    private Task<string> ProblemsAsync(string path) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var items = shell.Language.Problems.Files.SelectMany(f => f.Items).Where(p => path.Length == 0 || Relative(p.Path) == path.TrimStart('/')).Take(100).ToList(); // not a path (a relative path as the model writes it)
        return items.Count == 0 ? "No problems reported." : string.Join('\n', items.Select(p => $"{Relative(p.Path)}:{p.Line + 1}:{p.Column + 1}: {p.Severity.ToString().ToLowerInvariant()}: {p.Message}"));
    }).GetTask();

    private async Task<bool> ConsentAsync(IAgentSteps steps, AgentStep step, string question, bool always, Action remember, CancellationToken ct)
    {
        if (always) return true;
        var answer = await Dispatcher.UIThread.InvokeAsync(() => step.AskAsync(question, ct));
        if (answer == Consent.AlwaysAllow) remember();
        if (answer == Consent.Deny) await steps.UpdateAsync(() => step.State = StepState.Denied);
        return answer != Consent.Deny;
    }

    private async Task<string> EditAsync(IAgentSteps steps, AgentStep step, string path, string oldText, string newText, CancellationToken ct)
    {
        var full = Resolve(path);
        var text = await TextOfAsync(full, ct) ?? throw new AgentToolException($"{path} does not exist; use create_file.");
        oldText = oldText.Replace("\r\n", "\n", StringComparison.Ordinal);
        newText = newText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var at = text.IndexOf(oldText, StringComparison.Ordinal);
        if (oldText.Length == 0 || at < 0) throw new AgentToolException($"old_text was not found in {path}; read the file again and copy the lines exactly.");
        if (text.IndexOf(oldText, at + 1, StringComparison.Ordinal) >= 0) throw new AgentToolException($"old_text appears more than once in {path}; include more lines around it.");
        var removed = oldText.Count(c => c == '\n') + 1;
        var added = newText.Count(c => c == '\n') + 1;
        await steps.UpdateAsync(() => step.Detail = Preview(oldText, newText));
        if (!await ConsentAsync(steps, step, $"Change {removed} line{(removed == 1 ? "" : "s")} in {path}?", _editsAllowed, () => _editsAllowed = true, ct)) return "The user did not allow this edit.";
        var applied = await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var tab = await shell.Workbench.OpenAsync(full);
            if (tab is null) return false;
            var doc = tab.Document;
            var current = doc.Text.ToString();
            var where = current.IndexOf(oldText, StringComparison.Ordinal);
            if (where < 0) return false;
            return doc.Apply([new TextChange(where, oldText.Length, newText)], [Selection.Caret(where + newText.Length)]);
        });
        if (!applied) throw new AgentToolException($"{path} changed while the edit waited; read it again.");
        await steps.UpdateAsync(() =>
        {
            step.Text = $"Edited {path} (−{removed} +{added})";
            step.State = StepState.Done;
        });
        return $"Edited {path}: {removed} line(s) replaced by {added}. The change is in the editor, unsaved.";
    }

    private static string Preview(string oldText, string newText)
    {
        static IEnumerable<string> Cap(string text, string mark) => text.Split('\n').Take(12).Select(l => mark + l);
        return string.Join('\n', Cap(oldText, "- ").Concat(Cap(newText, "+ ")));
    }

    private async Task<string> CreateAsync(IAgentSteps steps, AgentStep step, string path, string content, CancellationToken ct)
    {
        var full = Resolve(path);
        if (File.Exists(full)) throw new AgentToolException($"{path} exists already; use edit_file.");
        await steps.UpdateAsync(() => step.Detail = string.Join('\n', content.Split('\n').Take(12)));
        if (!await ConsentAsync(steps, step, $"Create {path}?", _editsAllowed, () => _editsAllowed = true, ct)) return "The user did not allow creating this file.";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await Wicked.Core.Files.TextFile.WriteAsync(full, content.Replace("\r\n", "\n", StringComparison.Ordinal), Wicked.Core.Files.FileEncoding.Utf8, ct);
        await Dispatcher.UIThread.InvokeAsync(() => shell.Workbench.OpenAsync(full));
        await steps.UpdateAsync(() =>
        {
            step.Text = $"Created {path}";
            step.State = StepState.Done;
        });
        return $"Created {path}.";
    }

    private async Task<string> CommandAsync(IAgentSteps steps, AgentStep step, string command, CancellationToken ct)
    {
        if (command.Trim().Length == 0) throw new AgentToolException("The command was empty.");
        var allowed = _allowedCommands.Contains(command) || shell.Settings.Other?.TryGetValue("wicked.agent.allowedCommands", out var list) == true
            && list.ValueKind == JsonValueKind.Array && list.EnumerateArray().Any(e => e.GetString() is { Length: > 0 } prefix && command.StartsWith(prefix, StringComparison.Ordinal));
        if (!await ConsentAsync(steps, step, $"Run this command in {Path.GetFileName(Root)}?", allowed, () => _allowedCommands.Add(command), ct)) return "The user did not allow running this command.";
        var (exit, output, timedOut) = await RunProcessAsync(command, Root, ct);
        var tail = output.Length > OutputCap ? "…" + output[^OutputCap..] : output;
        await steps.UpdateAsync(() =>
        {
            step.Text = $"Ran {command} — {(timedOut ? "stopped after 2 minutes" : $"exit {exit}")}";
            step.State = exit == 0 && !timedOut ? StepState.Done : StepState.Failed;
            step.Detail = tail.Length > 2000 ? "…" + tail[^2000..] : tail;
        });
        return $"Exit code {(timedOut ? "none (stopped after two minutes)" : exit.ToString(System.Globalization.CultureInfo.InvariantCulture))}.\n{tail}";
    }

    /// <summary>A shell command with no input, its output (both streams, in order), killed with its children on timeout or cancel.</summary>
    internal static async Task<(int Exit, string Output, bool TimedOut)> RunProcessAsync(string command, string cwd, CancellationToken ct)
    {
        var info = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command } } : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
        info.WorkingDirectory = cwd;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        info.UseShellExecute = false;
        info.Environment["TERM"] = "dumb";
        info.Environment["NO_COLOR"] = "1";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info) ?? throw new AgentToolException("The command did not start.");
        process.StandardInput.Close();
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.Append(e.Data).Append('\n');
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (output) output.Append(e.Data).Append('\n');
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(CommandTimeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            if (ct.IsCancellationRequested) throw;
        }

        lock (output) return (timedOut ? -1 : process.ExitCode, output.ToString(), timedOut);
    }
}

/// <summary>A tool refused its call; the message goes back to the model so it can try otherwise.</summary>
public sealed class AgentToolException(string message) : Exception(message);
