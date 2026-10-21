using System.Text.Json;

namespace Wicked.Core.Debug;

// The subset of the Debug Adapter Protocol the client speaks, as records. Names follow the specification (camelCase on the
// wire); lines and columns start at 1, which the client asks for when it initialises.

/// <summary>A source file: a path on disk, or (without one) a reference the adapter serves the text for.</summary>
public sealed record DapSource(string? Name = null, string? Path = null, int? SourceReference = null, string? PresentationHint = null);

public sealed record DapThread(int Id, string Name);

public sealed record DapStackFrame(
    int Id,
    string Name,
    DapSource? Source = null,
    int Line = 0,
    int Column = 0,
    int? EndLine = null,
    int? EndColumn = null,
    bool? CanRestart = null,
    string? PresentationHint = null,
    string? ModuleId = null);

public sealed record DapScope(string Name, int VariablesReference, bool Expensive = false, int? NamedVariables = null, int? IndexedVariables = null, string? PresentationHint = null);

public sealed record DapVariablePresentation(string? Kind = null, IReadOnlyList<string>? Attributes = null, string? Visibility = null);

public sealed record DapVariable(
    string Name,
    string Value,
    string? Type = null,
    int VariablesReference = 0,
    int? NamedVariables = null,
    int? IndexedVariables = null,
    string? EvaluateName = null,
    DapVariablePresentation? PresentationHint = null,
    string? MemoryReference = null);

/// <summary>The answer to <c>evaluate</c>.</summary>
public sealed record DapEvaluation(string Result, string? Type = null, int VariablesReference = 0, int? NamedVariables = null, int? IndexedVariables = null, string? MemoryReference = null, DapVariablePresentation? PresentationHint = null);

/// <summary>A breakpoint as the adapter reports it: verified or not, and where it actually is.</summary>
public sealed record DapBreakpoint(int? Id = null, bool Verified = false, string? Message = null, DapSource? Source = null, int? Line = null, int? Column = null, int? EndLine = null, int? EndColumn = null);

public sealed record ExceptionBreakpointsFilter(string Filter, string Label, string? Description = null, bool Default = false, bool SupportsCondition = false, string? ConditionDescription = null);

/// <summary>What the adapter can do, as it says in its answer to <c>initialize</c> (and later in <c>capabilities</c> events).</summary>
public sealed record DapCapabilities
{
    public bool SupportsConfigurationDoneRequest { get; init; }

    public bool SupportsFunctionBreakpoints { get; init; }

    public bool SupportsConditionalBreakpoints { get; init; }

    public bool SupportsHitConditionalBreakpoints { get; init; }

    public bool SupportsEvaluateForHovers { get; init; }

    public bool SupportsStepBack { get; init; }

    public bool SupportsSetVariable { get; init; }

    public bool SupportsRestartFrame { get; init; }

    public bool SupportsGotoTargetsRequest { get; init; }

    public bool SupportsStepInTargetsRequest { get; init; }

    public bool SupportsCompletionsRequest { get; init; }

    public bool SupportsModulesRequest { get; init; }

    public bool SupportsRestartRequest { get; init; }

    public bool SupportsExceptionOptions { get; init; }

    public bool SupportsValueFormattingOptions { get; init; }

    public bool SupportsExceptionInfoRequest { get; init; }

    public bool SupportTerminateDebuggee { get; init; }

    public bool SupportSuspendDebuggee { get; init; }

    public bool SupportsDelayedStackTraceLoading { get; init; }

    public bool SupportsLoadedSourcesRequest { get; init; }

    public bool SupportsLogPoints { get; init; }

    public bool SupportsTerminateThreadsRequest { get; init; }

    public bool SupportsSetExpression { get; init; }

    public bool SupportsTerminateRequest { get; init; }

    public bool SupportsDataBreakpoints { get; init; }

    public bool SupportsReadMemoryRequest { get; init; }

    public bool SupportsDisassembleRequest { get; init; }

    public bool SupportsCancelRequest { get; init; }

    public bool SupportsBreakpointLocationsRequest { get; init; }

    public bool SupportsClipboardContext { get; init; }

    public bool SupportsSteppingGranularity { get; init; }

    public bool SupportsInstructionBreakpoints { get; init; }

    public bool SupportsExceptionFilterOptions { get; init; }

    public bool SupportsSingleThreadExecutionRequests { get; init; }

    public IReadOnlyList<ExceptionBreakpointsFilter>? ExceptionBreakpointFilters { get; init; }

    /// <summary>These capabilities with the ones a <c>capabilities</c> event names replaced.</summary>
    public DapCapabilities Merge(JsonElement update)
    {
        if (update.ValueKind != JsonValueKind.Object) return this;
        var merged = JsonSerializer.SerializeToElement(this, DapJson.Options);
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            foreach (var p in merged.EnumerateObject())
            {
                if (!update.TryGetProperty(p.Name, out _)) p.WriteTo(w);
            }

            foreach (var p in update.EnumerateObject()) p.WriteTo(w);
            w.WriteEndObject();
        }

        return JsonSerializer.Deserialize<DapCapabilities>(stream.ToArray(), DapJson.Options) ?? this;
    }
}

/// <summary>Why the program stopped, from a <c>stopped</c> event.</summary>
public sealed record DapStop(string Reason, string? Description, string? Text, int? ThreadId, bool AllThreadsStopped, IReadOnlyList<int> HitBreakpointIds)
{
    /// <summary>The reason in words: "Paused on breakpoint", "Paused on exception", …</summary>
    public string Title => Reason switch
    {
        "breakpoint" => "Paused on breakpoint",
        "exception" => "Paused on exception",
        "step" => "Paused on step",
        "pause" => "Paused",
        "entry" => "Paused on entry",
        "goto" => "Paused on goto",
        "function breakpoint" => "Paused on function breakpoint",
        "data breakpoint" => "Paused on data breakpoint",
        "instruction breakpoint" => "Paused on instruction breakpoint",
        _ => string.IsNullOrEmpty(Reason) ? "Paused" : "Paused on " + Reason,
    };
}

/// <summary>Text the program or the adapter wrote, from an <c>output</c> event.</summary>
public sealed record DapOutput(string Category, string Text, DapSource? Source = null, int? Line = null, int VariablesReference = 0, string? Group = null)
{
    /// <summary>Reads an <c>output</c> event's body (whose text is called <c>output</c> on the wire).</summary>
    public static DapOutput From(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return new DapOutput("console", "");
        string? Text(string name) => body.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return new DapOutput(
            Text("category") is { Length: > 0 } category ? category : "console",
            Text("output") ?? "",
            body.TryGetProperty("source", out var s) ? DapJson.Read<DapSource?>(s, null) : null,
            body.TryGetProperty("line", out var l) && l.TryGetInt32(out var line) ? line : null,
            body.TryGetProperty("variablesReference", out var v) && v.TryGetInt32(out var reference) ? reference : 0,
            Text("group"));
    }
}

public sealed record DapExceptionInfo(string ExceptionId, string? Description, string? BreakMode, string? TypeName, string? FullTypeName, string? StackTrace);

