using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.App.Assistant;
using Wicked.Core;
using Wicked.Core.Chat;

namespace Wicked.App.Chat;

/// <summary>
/// Agent mode (D18): the model gets the workspace's tools, and a turn runs until it stops calling them — each call shown as
/// a step in the reply, the ones that change something waiting on the user's consent. The calls and their answers ride
/// with the turn, so the next question has them.
/// </summary>
public sealed partial class ChatViewModel
{
    public const int MaxAgentSteps = 30;

    private const string AgentInstructions = """
        You are the coding agent of Wicked, an IDE, working in the user's project on their own computer. Use the tools to
        look before you change anything: list and search the files, read the parts that matter, then make small, exact
        edits with edit_file. Run commands (builds, tests) with run_command when they help you check your work. Keep going
        until the task is done or you need the user, then say briefly what you did and what is left. Never invent file
        contents: read them.
        """;

    /// <summary>Ask (a chat) or Agent (the model may read, search, edit and run in the project, asking first for changes).</summary>
    [ObservableProperty]
    private bool _agentMode;

    public bool CanUseAgent => _host.Agent is not null;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleAgentMode() => AgentMode = !AgentMode && CanUseAgent;

    /// <summary>The steps of a turn, shown in its message (posted to the UI thread).</summary>
    private sealed class MessageSteps(ChatMessageViewModel message) : IAgentSteps
    {
        public Task<AgentStep> AddAsync(string icon, string text) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            var step = new AgentStep(icon, text);
            message.Steps.Add(step);
            message.StepsChanged();
            return step;
        }).GetTask();

        public Task UpdateAsync(Action change) => Dispatcher.UIThread.InvokeAsync(change).GetTask();
    }

    private Task<TurnResult> AgentPumpAsync(ChatRequest request, ChatMessageViewModel target, IAgentTools tools, CancellationToken ct) => Task.Run(async () =>
    {
        var renderer = new StreamRenderer(target.PostFrame);
        renderer.Start();
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        ChatUsage? usage = null;
        var reason = "stop";
        string? error = null;
        var clock = Stopwatch.StartNew();
        var steps = new MessageSteps(target);
        try
        {
            var workspace = await tools.DescribeAsync(ct).ConfigureAwait(false);
            var messages = new List<ChatMessage>();
            var system = request.Messages.FirstOrDefault(m => m.Role == ChatRole.System)?.Content;
            messages.Add(new ChatMessage(ChatRole.System, AgentInstructions + "\n" + workspace + (system is { Length: > 0 } ? "\n" + system : "")));
            messages.AddRange(request.Messages.Where(m => m.Role != ChatRole.System));
            for (var round = 0; round < MaxAgentSteps; round++)
            {
                var calls = new SortedDictionary<int, (string? Id, string? Name, StringBuilder Arguments)>();
                var said = new StringBuilder();
                await foreach (var chunk in _services.Chat.StreamAsync(request with { Messages = messages, Tools = tools.Definitions }, ct).WithCancellation(ct).ConfigureAwait(false))
                {
                    switch (chunk)
                    {
                        case ContentDelta delta:
                            said.Append(delta.Text);
                            content.Append(delta.Text);
                            renderer.AppendContent(delta.Text);
                            break;
                        case ReasoningDelta delta:
                            reasoning.Append(delta.Text);
                            renderer.AppendReasoning(delta.Text);
                            break;
                        case ToolCallDelta call:
                            if (!calls.TryGetValue(call.Index, out var entry)) calls[call.Index] = entry = (null, null, new StringBuilder());
                            calls[call.Index] = (call.Id ?? entry.Id, call.Name ?? entry.Name, entry.Arguments.Append(call.Arguments));
                            break;
                        case ChatUsage u:
                            usage = u;
                            break;
                        case ChatFinished finished:
                            reason = finished.Reason;
                            break;
                    }
                }

                // No calls: the model has answered.
                if (calls.Count == 0 || reason == "cancelled") break;
                var made = calls.Select((kv, i) => new ChatToolCall(kv.Value.Id ?? $"call_{round}_{i}", kv.Value.Name ?? "", kv.Value.Arguments.ToString())).ToList();
                var asked = new ChatMessage(ChatRole.Assistant, said.ToString(), ToolCalls: made);
                messages.Add(asked);
                await Dispatcher.UIThread.InvokeAsync(() => target.Transcript.Add(asked));
                foreach (var call in made)
                {
                    var answer = await tools.RunAsync(call, steps, ct).ConfigureAwait(false);
                    var told = new ChatMessage(ChatRole.Tool, answer, ToolCallId: call.Id);
                    messages.Add(told);
                    await Dispatcher.UIThread.InvokeAsync(() => target.Transcript.Add(told));
                }

                if (said.Length > 0)
                {
                    content.Append("\n\n");
                    renderer.AppendContent("\n\n");
                }

                if (round == MaxAgentSteps - 1) error = $"The agent stopped after {MaxAgentSteps} rounds of tools; ask it to go on.";
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled";
        }
        catch (ChatException ex)
        {
            error = ex.Message;
            reason = "error";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Log.Error("An agent turn failed", ex);
            error = $"The agent failed: {ex.Message}";
            reason = "error";
        }

        var frame = await renderer.CompleteAsync().ConfigureAwait(false);
        return new TurnResult(content.ToString().TrimEnd(), reasoning.ToString(), usage, reason, error, frame, clock.Elapsed);
    });
}
