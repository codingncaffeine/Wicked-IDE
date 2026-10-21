using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core.Debug;

namespace Wicked.App.Debug;

public enum StackRowKind
{
    Session,
    Thread,
    Frame,

    /// <summary>"Load more stack frames": the thread has more than were fetched.</summary>
    More,
}

/// <summary>A row of the Call Stack: a session, one of its threads, a frame of a thread, or the row that fetches more frames.</summary>
public sealed partial class StackRow : ObservableObject
{
    public StackRow(StackRowKind kind, DebugSession session, string title)
    {
        Kind = kind;
        Session = session;
        Title = title;
    }

    public StackRowKind Kind { get; }

    public DebugSession Session { get; }

    public int ThreadId { get; init; }

    public DapStackFrame? Frame { get; init; }

    public string Title { get; }

    /// <summary>The place ("Program.cs:12") beside a frame's name, or the state ("PAUSED ON BREAKPOINT") beside a thread's.</summary>
    public string Detail { get; init; } = "";

    /// <summary>Frames of libraries and the runtime are drawn quieter.</summary>
    public bool Subtle { get; init; }

    public int Depth { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public bool IsFrame => Kind == StackRowKind.Frame;

    public bool IsHeader => Kind is StackRowKind.Session or StackRowKind.Thread;

    public bool IsMore => Kind == StackRowKind.More;
}

public enum BreakpointRowKind
{
    Source,
    Function,
    Exception,
}

/// <summary>A row of the Breakpoints list: a checkbox, a name, and where it is or what it needs.</summary>
public sealed partial class BreakpointRow : ObservableObject
{
    public BreakpointRow(BreakpointRowKind kind, string title)
    {
        Kind = kind;
        Title = title;
    }

    public BreakpointRowKind Kind { get; }

    /// <summary>The breakpoint's id, or (for an exception filter) 0.</summary>
    public int Id { get; init; }

    public string? Filter { get; init; }

    public string? Path { get; init; }

    public int Line { get; init; }

    public string Title { get; }

    /// <summary>The line, or the condition, hit count or message.</summary>
    public string Detail { get; init; } = "";

    /// <summary>What the adapter said when it could not bind this one.</summary>
    public string? Problem { get; init; }

    public BreakpointKind Style { get; init; }

    public bool Unverified { get; init; }

    public bool IsSource => Kind == BreakpointRowKind.Source;

    public bool IsConditional => Style == BreakpointKind.Conditional;

    public bool IsLogpoint => Style == BreakpointKind.Logpoint;

    [ObservableProperty]
    private bool _isEnabled;
}

/// <summary>What a choice in the run picker starts.</summary>
public enum RunTargetKind
{
    /// <summary>A launch.json configuration.</summary>
    Launch,

    /// <summary>A launch.json compound: several configurations together.</summary>
    Compound,

    /// <summary>The active file, run or debugged with no configuration written (VS Code's Python and Node, JetBrains' context run).</summary>
    CurrentFile,

    /// <summary>A detected program to run (dotnet run, cargo run, npm start): runs as a task, without the debugger.</summary>
    Task,
}

/// <summary>A choice in the configuration picker: a launch.json configuration or compound, the current file, or a program to run.</summary>
public sealed record ConfigurationChoice(string Name, string Type, bool IsCompound, RunTargetKind Kind = RunTargetKind.Launch)
{
    /// <summary>The name "Current File" goes by in the picker and in the saved state.</summary>
    public const string CurrentFileName = "Current File";

    public static ConfigurationChoice CurrentFile { get; } = new(CurrentFileName, "", false, RunTargetKind.CurrentFile);

    public string Label => IsCompound ? $"{Name} (compound)" : Name;

    /// <summary>Started by launch.json (and so by the debugger).</summary>
    public bool IsLaunch => Kind is RunTargetKind.Launch or RunTargetKind.Compound;

    /// <summary>The quiet word beside the name in a list.</summary>
    public string Detail => Kind switch
    {
        RunTargetKind.Compound => "compound",
        RunTargetKind.CurrentFile => "run or debug the active file",
        RunTargetKind.Task => "task",
        _ => Type,
    };
}
