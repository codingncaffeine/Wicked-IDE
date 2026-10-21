using System.Collections.Immutable;

namespace Wicked.Core.Tasks;

/// <summary>Whether a task's command line goes through a shell or starts a program directly.</summary>
public enum TaskType
{
    Shell,
    Process,
}

/// <summary>When a task's output panel is brought forward.</summary>
public enum RevealMode
{
    Always,

    /// <summary>Only when the task fails or reports an error.</summary>
    Silent,

    Never,
}

/// <summary>The group a task belongs to; the default task of a group is what its shortcut runs.</summary>
public enum TaskGroup
{
    None,
    Build,
    Test,
}

/// <summary>Whether the tasks a task depends on start together or one after the other.</summary>
public enum DependsOrder
{
    Parallel,
    Sequence,
}

/// <summary>How an argument is protected from the shell.</summary>
public enum QuoteMode
{
    /// <summary>Quoted only when it needs it (VS Code's default).</summary>
    Escape,

    /// <summary>Always single-quoted: the shell expands nothing in it.</summary>
    Strong,

    /// <summary>Always double-quoted: variables still expand.</summary>
    Weak,
}

/// <summary>A command or argument, with how to quote it for a shell.</summary>
public sealed record TaskArgument(string Value, QuoteMode Quoting = QuoteMode.Escape);

/// <summary>How a task shows itself: the panel it writes to, and when that panel is brought forward.</summary>
public sealed record TaskPresentation(RevealMode Reveal = RevealMode.Always, bool Clear = false, bool Focus = false, bool Echo = true);

/// <summary>A custom shell for a shell task (<c>options.shell</c>).</summary>
public sealed record ShellOptions(string Executable, ImmutableArray<string> Args);

/// <summary>A question a task asks before it runs (<c>${input:id}</c>): a typed line or a pick from a list.</summary>
/// <param name="Id">What <c>${input:id}</c> names.</param>
/// <param name="Type">"promptString" or "pickString".</param>
/// <param name="Description">The prompt shown.</param>
/// <param name="Default">The value offered first.</param>
/// <param name="Options">The choices of a pick: value and label.</param>
/// <param name="Password">Whether the typed text is hidden.</param>
public sealed record TaskInput(string Id, string Type, string Description, string? Default, ImmutableArray<(string Value, string Label)> Options, bool Password = false);

/// <summary>One task: what to run and how, as tasks.json or the detectors describe it.</summary>
public sealed record TaskDefinition
{
    public required string Label { get; init; }

    public TaskType Type { get; init; } = TaskType.Shell;

    /// <summary>The program, or the first word of the command line.</summary>
    public TaskArgument? Command { get; init; }

    public ImmutableArray<TaskArgument> Args { get; init; } = [];

    /// <summary>The working directory, before variables are expanded; null is the workspace folder.</summary>
    public string? Cwd { get; init; }

    public ImmutableDictionary<string, string> Env { get; init; } = ImmutableDictionary<string, string>.Empty;

    public ShellOptions? Shell { get; init; }

    public TaskGroup Group { get; init; }

    public bool IsDefault { get; init; }

    public ImmutableArray<string> DependsOn { get; init; } = [];

    public DependsOrder DependsOrder { get; init; }

    public TaskPresentation Presentation { get; init; } = new();

    public bool IsBackground { get; init; }

    public ImmutableArray<ProblemMatcher> Matchers { get; init; } = [];

    /// <summary>Where the task came from: "Workspace" for tasks.json, or a detector's name ("npm", "make").</summary>
    public string Source { get; init; } = "Workspace";

    /// <summary>A quiet line beside the label in a list (the script's body, the file it builds).</summary>
    public string Detail { get; init; } = "";

    /// <summary>Found by looking at the project rather than written in tasks.json.</summary>
    public bool Detected { get; init; }

    /// <summary>Starts by itself when the folder opens (<c>runOptions.runOn</c>).</summary>
    public bool RunOnFolderOpen { get; init; }

    /// <summary>The command line as shown in the output header and in lists.</summary>
    public string CommandText => Command is null ? "" : string.Join(' ', new[] { Command.Value }.Concat(Args.Select(a => a.Value)));
}

/// <summary>What reading tasks.json found.</summary>
/// <param name="Tasks">The tasks, platform overrides applied.</param>
/// <param name="Inputs">The inputs, by id.</param>
/// <param name="Problems">Everything in the file that could not be used, one line each.</param>
public sealed record TasksFileContent(ImmutableArray<TaskDefinition> Tasks, ImmutableDictionary<string, TaskInput> Inputs, ImmutableArray<string> Problems)
{
    public static readonly TasksFileContent Empty = new([], ImmutableDictionary<string, TaskInput>.Empty, []);
}

/// <summary>How a run of a task ended.</summary>
/// <param name="ExitCode">The process's exit code; null when it never started or was stopped before it ended.</param>
/// <param name="Terminated">The user stopped it.</param>
/// <param name="Error">Why it could not run (a variable that did not resolve, a program that was not found).</param>
/// <param name="Problems">What the problem matchers found.</param>
public sealed record TaskRunResult(int? ExitCode, bool Terminated, string? Error, ImmutableArray<TaskProblem> Problems)
{
    public bool Succeeded => Error is null && !Terminated && ExitCode == 0;
}
