using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core.Debug;

namespace Wicked.App.Debug;

/// <summary>What a row of the Variables or Watch tree stands for.</summary>
public enum DebugNodeKind
{
    /// <summary>A scope of the frame (Locals, Globals).</summary>
    Scope,

    Variable,

    /// <summary>An expression the user watches.</summary>
    Watch,

    /// <summary>A range of a long list ("[0..99]"), loaded when opened.</summary>
    Page,

    /// <summary>A line of text in place of children ("no children", an error).</summary>
    Message,
}

/// <summary>
/// One row of a variables tree: a name and a value, and when the value has parts, how to get them. Children are fetched the
/// first time the row opens and kept until the next stop.
/// </summary>
public sealed partial class DebugNode : ObservableObject
{
    /// <summary>A long list is shown in ranges this big.</summary>
    public const int PageSize = 100;

    public DebugNode(DebugNodeKind kind, string name)
    {
        Kind = kind;
        _name = name;
    }

    public DebugNodeKind Kind { get; }

    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    private string _value = "";

    [ObservableProperty]
    private string? _type;

    /// <summary>The adapter's reference for this value's parts (0: none).</summary>
    public int Reference { get; set; }

    public int? Indexed { get; set; }

    public int? Named { get; set; }

    /// <summary>For a <see cref="DebugNodeKind.Page"/>: where the range starts and how many it holds.</summary>
    public int PageStart { get; set; }

    public int PageCount { get; set; }

    /// <summary>The expression that names this value, so a copy or a watch can use it.</summary>
    public string? EvaluateName { get; set; }

    /// <summary>The session and frame the value belongs to, for evaluating and for changing it.</summary>
    public DebugSession? Session { get; set; }

    public int? FrameId { get; set; }

    public DebugNode? Parent { get; set; }

    public int Depth { get; set; }

    /// <summary>A scope that costs to open (Globals): not opened until the user asks.</summary>
    public bool Expensive { get; set; }

    public List<DebugNode> Children { get; set; } = [];

    public bool ChildrenLoaded { get; set; }

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>The value differs from the last stop's: drawn in another colour, as VS Code does.</summary>
    [ObservableProperty]
    private bool _changed;

    /// <summary>The value is the adapter's complaint, not a value (a watch that cannot be evaluated).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValue))]
    private bool _isError;

    [ObservableProperty]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "";

    public bool IsScope => Kind == DebugNodeKind.Scope;

    public bool IsWatch => Kind == DebugNodeKind.Watch;

    public bool CanExpand => Kind == DebugNodeKind.Page || (Kind != DebugNodeKind.Message && Reference > 0);

    public bool HasValue => Kind is not (DebugNodeKind.Scope or DebugNodeKind.Page) && (Value.Length > 0 || IsError);

    /// <summary>Whether double-clicking the value edits it (a variable of a session whose adapter can set variables).</summary>
    public bool CanSet => Kind == DebugNodeKind.Variable && Parent is { Reference: > 0 } && Session is { State: DebugSessionState.Stopped, Capabilities.SupportsSetVariable: true };

    /// <summary>The names from the scope down: how an open row is found again after the next stop.</summary>
    public string Path => Parent is null ? Name : Parent.Path + "\u001f" + Name;

}
