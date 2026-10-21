using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Chat;

namespace Wicked.App.Assistant;

/// <summary>The answer to a step that asks before it acts.</summary>
public enum Consent
{
    Deny,
    Allow,

    /// <summary>Allow, and do not ask again for this kind of step in this workspace (this command, edits).</summary>
    AlwaysAllow,
}

public enum StepState
{
    Running,
    Asking,
    Done,
    Failed,
    Denied,
}

/// <summary>
/// One thing the agent did or wants to do, as the chat shows it: "Read src/app.cs", "Ran dotnet test — exit 0". A step
/// that needs consent waits on the user's Allow / Always / Deny.
/// </summary>
public sealed partial class AgentStep : ObservableObject
{
    private TaskCompletionSource<Consent>? _answer;

    public AgentStep(string icon, string text)
    {
        Icon = icon;
        _text = text;
    }

    public string Icon { get; }

    [ObservableProperty]
    private string _text;

    /// <summary>More about it (a command's output tail, an edit's lines), folded under the step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetail))]
    private string _detail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsking), nameof(IsRunning), nameof(IsFailed))]
    private StepState _state = StepState.Running;

    [ObservableProperty]
    private string _question = "";

    [ObservableProperty]
    private bool _expanded;

    public bool HasDetail => Detail.Length > 0;

    public bool IsAsking => State == StepState.Asking;

    public bool IsRunning => State == StepState.Running;

    public bool IsFailed => State is StepState.Failed or StepState.Denied;

    /// <summary>Waits for the user's answer (UI thread sets it through the buttons).</summary>
    internal Task<Consent> AskAsync(string question, CancellationToken ct)
    {
        Question = question;
        State = StepState.Asking;
        _answer = new TaskCompletionSource<Consent>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => _answer.TrySetResult(Consent.Deny));
        return _answer.Task;
    }

    [RelayCommand]
    private void Allow() => Answer(Consent.Allow);

    [RelayCommand]
    private void AlwaysAllow() => Answer(Consent.AlwaysAllow);

    [RelayCommand]
    private void Deny() => Answer(Consent.Deny);

    [RelayCommand]
    private void ToggleDetail() => Expanded = !Expanded;

    private void Answer(Consent consent)
    {
        if (_answer is null) return;
        State = consent == Consent.Deny ? StepState.Denied : StepState.Running;
        _answer.TrySetResult(consent);
    }
}

/// <summary>Where an agent's steps are shown (the assistant turn that is running); any thread.</summary>
public interface IAgentSteps
{
    /// <summary>A new step in the turn, made on the UI thread; awaits until it is there.</summary>
    Task<AgentStep> AddAsync(string icon, string text);

    /// <summary>Runs a change to a step on the UI thread.</summary>
    Task UpdateAsync(Action change);
}

/// <summary>The tools an agent may call in this workspace, and what it is told about the workspace.</summary>
public interface IAgentTools
{
    IReadOnlyList<ChatTool> Definitions { get; }

    /// <summary>The system prompt's description of the workspace: its folder, the open files, its rules files (AGENTS.md).</summary>
    Task<string> DescribeAsync(CancellationToken ct);

    /// <summary>Runs one call (asking first when it changes something), and gives the model's view of the result.</summary>
    Task<string> RunAsync(ChatToolCall call, IAgentSteps steps, CancellationToken ct);
}
