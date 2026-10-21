using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wicked.FakeAdapter;

/// <summary>What the scripted adapter claims to support.</summary>
public sealed class ScriptedAdapterOptions
{
    public bool SupportsTerminate { get; init; } = true;

    public bool SupportsRestart { get; init; }

    public bool SupportsCancel { get; init; }
}

/// <summary>
/// A debug adapter that "runs" a text file: every line is a statement, <c>pc</c> is the current line. It stops at breakpoints
/// (with conditions, hit counts and logpoints), steps a line at a time, has a stack of two frames, variables with a nested object
/// and a list of 250 items to page through, an editable <c>counter</c>, and an evaluator for <c>pc</c>, <c>counter</c>, sums and
/// <c>counter = N</c>. A line with <c>throw</c> stops with an exception when the "all" filter is on; a line <c>func name</c>
/// stops for a function breakpoint of that name. The launch arguments steer it: <c>program</c>, <c>stopOnEntry</c>,
/// <c>hangAtEnd</c> (the program keeps running at its last line until paused), <c>console</c>=<c>integratedTerminal</c> with
/// <c>terminalCommand</c> (asks the client to run it), <c>child</c> (asks for a second session), <c>failLaunch</c> (the launch
/// fails with that message), <c>threads</c> (a number of threads).
/// </summary>
public sealed class ScriptedAdapter(Stream input, Stream output, ScriptedAdapterOptions? options = null)
{
    private readonly ScriptedAdapterOptions _options = options ?? new ScriptedAdapterOptions();
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _replies = new();
    private readonly List<(string Source, int Line, string? Condition, string? Hit, string? Log)> _breakpoints = [];
    private readonly Dictionary<(string, int), int> _hits = [];
    private readonly HashSet<string> _filters = [];
    private readonly List<string> _functions = [];
    private string[] _lines = [];
    private string _program = "";
    private int _pc;
    private int _counter = 7;
    private int _seq;
    private bool _stopOnEntry;
    private bool _hangAtEnd;
    private bool _running;
    private int _threadCount = 1;
    private JsonNode? _launchArguments;
    private string _launchCommand = "launch";
    private TaskCompletionSource? _configured;
    private Task? _launchTask;
    private int _nextBreakpointId = 100;

    /// <summary>Every request received, as <c>command {arguments json}</c>, in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Drops the line as a crashed adapter would: both ends of its streams close.</summary>
    public void Die()
    {
        output.Dispose();
        input.Dispose();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await ReadAsync(ct) is { } message)
            {
                var type = (string?)message["type"];
                if (type == "request") await HandleAsync(message);
                else if (type == "response" && message["request_seq"]?.GetValue<int>() is { } rs && _replies.TryRemove(rs, out var waiting)) waiting.TrySetResult(message);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The client went away.
        }
    }

    // ── Wire ─────────────────────────────────────────────────────────────────────────────────────

    private async Task<JsonNode?> ReadAsync(CancellationToken ct)
    {
        var length = -1;
        var line = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            if (await input.ReadAsync(one, ct) == 0) return null;
            if (one[0] == '\n')
            {
                var text = line.ToString().TrimEnd('\r');
                line.Clear();
                if (text.Length == 0)
                {
                    if (length >= 0) break;
                    continue;
                }

                if (text.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(text[15..].Trim(), CultureInfo.InvariantCulture);
            }
            else line.Append((char)one[0]);
        }

        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await input.ReadAsync(body.AsMemory(read), ct);
            if (n == 0) return null;
            read += n;
        }

        return JsonNode.Parse(body);
    }

    private void Send(JsonObject message)
    {
        message["seq"] = Interlocked.Increment(ref _seq);
        var json = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n");
        lock (_writeGate)
        {
            output.Write(header);
            output.Write(json);
            output.Flush();
        }
    }

    private void Respond(JsonNode request, JsonNode? body = null) => Send(new JsonObject
    {
        ["type"] = "response",
        ["request_seq"] = request["seq"]!.GetValue<int>(),
        ["success"] = true,
        ["command"] = (string?)request["command"],
        ["body"] = body,
    });

    private void Fail(JsonNode request, string message) => Send(new JsonObject
    {
        ["type"] = "response",
        ["request_seq"] = request["seq"]!.GetValue<int>(),
        ["success"] = false,
        ["command"] = (string?)request["command"],
        ["message"] = message,
        ["body"] = new JsonObject { ["error"] = new JsonObject { ["id"] = 1, ["format"] = message, ["showUser"] = true } },
    });

    private void Event(string name, JsonObject? body = null) => Send(new JsonObject { ["type"] = "event", ["event"] = name, ["body"] = body ?? new JsonObject() });

    private Task<JsonNode?> AskAsync(string command, JsonObject arguments)
    {
        var seq = Interlocked.Increment(ref _seq);
        var waiting = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _replies[seq] = waiting;
        lock (_writeGate)
        {
            var json = Encoding.UTF8.GetBytes(new JsonObject { ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = arguments }.ToJsonString());
            output.Write(Encoding.ASCII.GetBytes($"Content-Length: {json.Length}\r\n\r\n"));
            output.Write(json);
            output.Flush();
        }

        return waiting.Task;
    }

    // ── Requests ─────────────────────────────────────────────────────────────────────────────────

    private async Task HandleAsync(JsonNode request)
    {
        var command = (string)request["command"]!;
        var args = request["arguments"];
        Requests.Enqueue(command + " " + (args?.ToJsonString() ?? ""));
        switch (command)
        {
            case "initialize":
                Respond(request, new JsonObject
                {
                    ["supportsConfigurationDoneRequest"] = true,
                    ["supportsFunctionBreakpoints"] = true,
                    ["supportsConditionalBreakpoints"] = true,
                    ["supportsHitConditionalBreakpoints"] = true,
                    ["supportsLogPoints"] = true,
                    ["supportsSetVariable"] = true,
                    ["supportsSetExpression"] = false,
                    ["supportsExceptionInfoRequest"] = true,
                    ["supportsTerminateRequest"] = _options.SupportsTerminate,
                    ["supportsRestartRequest"] = _options.SupportsRestart,
                    ["supportsCancelRequest"] = _options.SupportsCancel,
                    ["supportsLoadedSourcesRequest"] = true,
                    ["supportsEvaluateForHovers"] = true,
                    ["exceptionBreakpointFilters"] = new JsonArray
                    {
                        new JsonObject { ["filter"] = "all", ["label"] = "All Exceptions", ["default"] = false },
                        new JsonObject { ["filter"] = "uncaught", ["label"] = "Uncaught Exceptions", ["default"] = true },
                    },
                });
                Event("initialized");
                break;
            case "launch" or "attach":
                _launchArguments = args;
                _launchCommand = command;
                _configured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _program = (string?)args?["program"] ?? "";
                _stopOnEntry = args?["stopOnEntry"]?.GetValue<bool>() == true;
                _hangAtEnd = args?["hangAtEnd"]?.GetValue<bool>() == true;
                _threadCount = args?["threads"]?.GetValue<int>() ?? 1;
                if ((string?)args?["failLaunch"] is { } failure)
                {
                    Fail(request, failure);
                    break;
                }

                _lines = _program.Length > 0 && File.Exists(_program) ? File.ReadAllLines(_program) : ["one", "two", "three", "four", "five"];
                // Like most adapters, the answer to launch waits for configurationDone.
                _launchTask = Task.Run(async () =>
                {
                    await _configured.Task;
                    Respond(request);
                    await BeginAsync();
                });
                break;
            case "setBreakpoints":
                SetBreakpoints(request, args!);
                break;
            case "setFunctionBreakpoints":
                _functions.Clear();
                var fb = new JsonArray();
                foreach (var b in args!["breakpoints"]!.AsArray())
                {
                    _functions.Add((string)b!["name"]!);
                    fb.Add(new JsonObject { ["verified"] = true, ["id"] = _nextBreakpointId++ });
                }

                Respond(request, new JsonObject { ["breakpoints"] = fb });
                break;
            case "setExceptionBreakpoints":
                _filters.Clear();
                foreach (var f in args!["filters"]!.AsArray()) _filters.Add((string)f!);
                Respond(request);
                break;
            case "configurationDone":
                Respond(request);
                _configured?.TrySetResult();
                break;
            case "threads":
                var threads = new JsonArray();
                for (var i = 1; i <= _threadCount; i++) threads.Add(new JsonObject { ["id"] = i, ["name"] = i == 1 ? "main" : $"worker {i}" });
                Respond(request, new JsonObject { ["threads"] = threads });
                break;
            case "stackTrace":
                StackTrace(request, args!);
                break;
            case "scopes":
                Respond(request, new JsonObject
                {
                    ["scopes"] = new JsonArray
                    {
                        new JsonObject { ["name"] = "Locals", ["variablesReference"] = 1000 + args!["frameId"]!.GetValue<int>(), ["expensive"] = false },
                        new JsonObject { ["name"] = "Globals", ["variablesReference"] = 2, ["expensive"] = true },
                    },
                });
                break;
            case "variables":
                Variables(request, args!);
                break;
            case "setVariable":
                if ((string)args!["name"]! == "counter" && int.TryParse((string)args["value"]!, NumberStyles.Integer, CultureInfo.InvariantCulture, out var set))
                {
                    _counter = set;
                    Respond(request, new JsonObject { ["value"] = _counter.ToString(CultureInfo.InvariantCulture), ["type"] = "int" });
                }
                else Fail(request, $"{(string)args["name"]!} cannot be set to that");

                break;
            case "evaluate":
                Evaluate(request, args!);
                break;
            case "continue":
                Respond(request, new JsonObject { ["allThreadsContinued"] = true });
                await ExecuteAsync(stepOnly: false);
                break;
            case "next" or "stepIn" or "stepOut":
                Respond(request);
                await ExecuteAsync(stepOnly: true);
                break;
            case "pause":
                Respond(request);
                if (_running)
                {
                    _running = false;
                    Event("stopped", new JsonObject { ["reason"] = "pause", ["threadId"] = 1, ["allThreadsStopped"] = true });
                }

                break;
            case "exceptionInfo":
                Respond(request, new JsonObject { ["exceptionId"] = "FakeError", ["description"] = "The fake program threw", ["breakMode"] = "always", ["details"] = new JsonObject { ["typeName"] = "FakeError", ["stackTrace"] = "at Main" } });
                break;
            case "loadedSources":
                Respond(request, new JsonObject { ["sources"] = new JsonArray { new JsonObject { ["name"] = Path.GetFileName(_program), ["path"] = _program } } });
                break;
            case "restart":
                Respond(request);
                _pc = 0;
                _running = false;
                await BeginAsync();
                break;
            case "cancel":
                Respond(request);
                break;
            case "terminate":
                Respond(request);
                Event("terminated");
                break;
            case "disconnect":
                Respond(request);
                break;
            default:
                Fail(request, $"The fake adapter does not do {command}");
                break;
        }
    }

    private void SetBreakpoints(JsonNode request, JsonNode args)
    {
        var source = (string?)args["source"]?["path"] ?? "";
        _breakpoints.RemoveAll(b => b.Source == source);
        var answers = new JsonArray();
        foreach (var b in args["breakpoints"]!.AsArray())
        {
            var line = b!["line"]!.GetValue<int>();
            _breakpoints.Add((source, line, (string?)b["condition"], (string?)b["hitCondition"], (string?)b["logMessage"]));
            // A line beyond the end of the file cannot be bound; the adapter says why.
            var fits = line >= 1 && (_lines.Length == 0 || line <= _lines.Length || !File.Exists(source));
            answers.Add(fits
                ? new JsonObject { ["verified"] = true, ["line"] = line, ["id"] = _nextBreakpointId++ }
                : new JsonObject { ["verified"] = false, ["line"] = line, ["message"] = "There is no code on that line", ["id"] = _nextBreakpointId++ });
        }

        Respond(request, new JsonObject { ["breakpoints"] = answers });
    }

    // ── The program ──────────────────────────────────────────────────────────────────────────────

    private async Task BeginAsync()
    {
        Event("output", new JsonObject { ["category"] = "stdout", ["output"] = "Hello from the fake adapter\n" });
        if ((string?)_launchArguments?["console"] == "integratedTerminal" && _launchArguments?["terminalCommand"] is JsonArray command)
        {
            var reply = await AskAsync("runInTerminal", new JsonObject { ["kind"] = "integrated", ["title"] = "fake", ["cwd"] = Path.GetDirectoryName(_program) ?? ".", ["args"] = command.DeepClone() });
            var pid = reply?["body"]?["processId"]?.ToString() ?? "none";
            Event("output", new JsonObject { ["category"] = "console", ["output"] = $"terminal process {pid}\n" });
        }

        if (_launchArguments?["child"] is JsonObject child)
        {
            var configuration = (JsonObject)child.DeepClone();
            configuration["program"] ??= _program;
            await AskAsync("startDebugging", new JsonObject { ["request"] = "launch", ["configuration"] = configuration });
        }

        if (_stopOnEntry)
        {
            _pc = 1;
            Event("stopped", new JsonObject { ["reason"] = "entry", ["threadId"] = 1, ["allThreadsStopped"] = true });
            return;
        }

        await ExecuteAsync(stepOnly: false);
    }

    private Task ExecuteAsync(bool stepOnly)
    {
        _running = true;
        while (true)
        {
            _pc++;
            if (_pc > _lines.Length)
            {
                if (_hangAtEnd)
                {
                    _pc = _lines.Length;
                    return Task.CompletedTask; // still "running"; a pause stops it
                }

                _running = false;
                Event("exited", new JsonObject { ["exitCode"] = 0 });
                Event("terminated");
                return Task.CompletedTask;
            }

            var hit = BreakpointHere();
            if (hit is { Log: { } log })
            {
                Event("output", new JsonObject { ["category"] = "console", ["output"] = log.Replace("{pc}", _pc.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) + "\n" });
                hit = null;
                if (!stepOnly) continue;
            }

            string? reason = null;
            if (hit is not null) reason = "breakpoint";
            else if (_lines[_pc - 1].Contains("throw", StringComparison.Ordinal) && _filters.Contains("all")) reason = "exception";
            else if (_functions.Any(f => _lines[_pc - 1].TrimStart().StartsWith("func " + f, StringComparison.Ordinal))) reason = "function breakpoint";
            else if (stepOnly) reason = "step";
            if (reason is null) continue;
            _running = false;
            var stop = new JsonObject { ["reason"] = reason, ["threadId"] = 1, ["allThreadsStopped"] = true };
            if (reason == "exception") stop["text"] = "The fake program threw";
            Event("stopped", stop);
            return Task.CompletedTask;
        }
    }

    private (string? Log, bool Any)? BreakpointHere()
    {
        foreach (var b in _breakpoints.Where(b => b.Line == _pc))
        {
            var key = (b.Source, b.Line);
            _hits[key] = _hits.GetValueOrDefault(key) + 1;
            if (b.Condition is { } condition && !Truthy(condition)) continue;
            if (b.Hit is { } hit && !HitMatches(hit, _hits[key])) continue;
            return (b.Log, true);
        }

        return null;
    }

    private bool Truthy(string condition)
    {
        var m = Regex.Match(condition, @"^\s*(\w+)\s*(==|>=|<=|>|<|!=)\s*(-?\d+)\s*$");
        if (!m.Success) return true;
        var left = Value(m.Groups[1].Value);
        var right = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "==" => left == right, "!=" => left != right, ">" => left > right, "<" => left < right, ">=" => left >= right, _ => left <= right };
    }

    private static bool HitMatches(string condition, int count)
    {
        var m = Regex.Match(condition, @"^\s*(==|>=|<=|>|<|%)?\s*(\d+)\s*$");
        if (!m.Success) return true;
        var n = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return m.Groups[1].Value switch { "" or "==" => count == n, ">" => count > n, "<" => count < n, ">=" => count >= n, "<=" => count <= n, _ => n != 0 && count % n == 0 };
    }

    private int Value(string name) => name switch { "pc" => _pc, "counter" => _counter, _ => int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0 };

    // ── Looking ──────────────────────────────────────────────────────────────────────────────────

    private void StackTrace(JsonNode request, JsonNode args)
    {
        var source = new JsonObject { ["name"] = Path.GetFileName(_program), ["path"] = _program };
        var all = new[]
        {
            new JsonObject { ["id"] = 1, ["name"] = "Main", ["source"] = source.DeepClone(), ["line"] = Math.Max(1, _pc), ["column"] = 1 },
            new JsonObject { ["id"] = 2, ["name"] = "Caller", ["source"] = source.DeepClone(), ["line"] = 1, ["column"] = 1 },
        };
        var start = args["startFrame"]?.GetValue<int>() ?? 0;
        var levels = args["levels"]?.GetValue<int>() is > 0 and var l ? l : all.Length;
        var frames = new JsonArray();
        foreach (var f in all.Skip(start).Take(levels)) frames.Add(f);
        Respond(request, new JsonObject { ["stackFrames"] = frames, ["totalFrames"] = all.Length });
    }

    private void Variables(JsonNode request, JsonNode args)
    {
        var reference = args["variablesReference"]!.GetValue<int>();
        var list = new JsonArray();
        if (reference >= 1000)
        {
            list.Add(new JsonObject { ["name"] = "pc", ["value"] = _pc.ToString(CultureInfo.InvariantCulture), ["type"] = "int", ["evaluateName"] = "pc" });
            list.Add(new JsonObject { ["name"] = "counter", ["value"] = _counter.ToString(CultureInfo.InvariantCulture), ["type"] = "int", ["evaluateName"] = "counter" });
            list.Add(new JsonObject { ["name"] = "name", ["value"] = "\"fake\"", ["type"] = "string", ["evaluateName"] = "name" });
            list.Add(new JsonObject { ["name"] = "obj", ["value"] = "{a, b, list}", ["type"] = "Thing", ["variablesReference"] = 10, ["namedVariables"] = 3, ["evaluateName"] = "obj" });
        }
        else if (reference == 2) list.Add(new JsonObject { ["name"] = "version", ["value"] = "\"1.0\"", ["type"] = "string" });
        else if (reference == 10)
        {
            list.Add(new JsonObject { ["name"] = "a", ["value"] = "1", ["type"] = "int" });
            list.Add(new JsonObject { ["name"] = "b", ["value"] = "\"two\"", ["type"] = "string" });
            list.Add(new JsonObject { ["name"] = "list", ["value"] = "int[250]", ["type"] = "int[]", ["variablesReference"] = 11, ["indexedVariables"] = 250 });
        }
        else if (reference == 11)
        {
            var start = args["start"]?.GetValue<int>() ?? 0;
            var count = args["count"]?.GetValue<int>() is > 0 and var c ? c : 250 - start;
            for (var i = start; i < Math.Min(250, start + count); i++) list.Add(new JsonObject { ["name"] = $"[{i}]", ["value"] = (i * 2).ToString(CultureInfo.InvariantCulture), ["type"] = "int" });
        }

        Respond(request, new JsonObject { ["variables"] = list });
    }

    private void Evaluate(JsonNode request, JsonNode args)
    {
        var expression = ((string)args["expression"]!).Trim();
        if (Regex.Match(expression, @"^counter\s*=\s*(-?\d+)$") is { Success: true } assign)
        {
            _counter = int.Parse(assign.Groups[1].Value, CultureInfo.InvariantCulture);
            Respond(request, new JsonObject { ["result"] = _counter.ToString(CultureInfo.InvariantCulture), ["type"] = "int" });
            return;
        }

        if (expression == "obj")
        {
            Respond(request, new JsonObject { ["result"] = "{a, b, list}", ["type"] = "Thing", ["variablesReference"] = 10, ["namedVariables"] = 3 });
            return;
        }

        if (expression == "name")
        {
            Respond(request, new JsonObject { ["result"] = "\"fake\"", ["type"] = "string" });
            return;
        }

        var sum = Regex.Match(expression, @"^(\w+)\s*([+\-*])\s*(\w+)$");
        if (sum.Success || Regex.IsMatch(expression, @"^(\w+)$"))
        {
            var known = (string s) => s is "pc" or "counter" || int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            var parts = sum.Success ? new[] { sum.Groups[1].Value, sum.Groups[3].Value } : [expression];
            if (parts.All(known))
            {
                var value = sum.Success
                    ? sum.Groups[2].Value switch { "+" => Value(parts[0]) + Value(parts[1]), "-" => Value(parts[0]) - Value(parts[1]), _ => Value(parts[0]) * Value(parts[1]) }
                    : Value(expression);
                Respond(request, new JsonObject { ["result"] = value.ToString(CultureInfo.InvariantCulture), ["type"] = "int" });
                return;
            }
        }

        Fail(request, $"Unknown identifier '{expression}'");
    }
}
