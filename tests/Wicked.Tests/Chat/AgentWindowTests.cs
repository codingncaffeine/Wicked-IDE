using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Wicked.App.Assistant;
using Wicked.Core.Chat;

namespace Wicked.Tests.Chat;

/// <summary>Agent mode in the window: the model's tool calls run as steps, a change waits for Allow, the edit lands in the editor.</summary>
public sealed class AgentWindowTests
{
    private static async IAsyncEnumerable<ChatChunk> Call(string id, string name, string arguments, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Delay(10, ct);
        yield return new ToolCallDelta(0, id, name, "");
        yield return new ToolCallDelta(0, null, null, arguments);
        yield return new ChatFinished("tool_calls");
    }

    /// <summary>The model's side of the plan: read the file, change it, then say it is done.</summary>
    private static IAsyncEnumerable<ChatChunk> Agent(ChatRequest request, CancellationToken ct)
    {
        var answers = request.Messages.Count(m => m.Role == ChatRole.Tool);
        return answers switch
        {
            0 => Call("c1", "read_file", """{"path":"notes.txt"}""", ct),
            1 => Call("c2", "edit_file", """{"path":"notes.txt","old_text":"hello world","new_text":"hello agent"}""", ct),
            _ => FakeEngine.Reply("Changed the greeting.", 2, TimeSpan.FromMilliseconds(5), ct),
        };
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task AnAgentTurnReadsAsksBeforeEditingAndTheEditLandsInTheEditor()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "hello world\n");
        File.WriteAllText(Path.Combine(root, "AGENTS.md"), "Keep greetings short.\n");
        var world = new ChatWorld();
        world.Runtime.Set(new RunningModel("agent:Q4", "Agent Model", RunState.Loaded, null, false, false, 8192, null));
        world.Engine.Script = Agent;
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var opened = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && shell.Workbench.Index is not null, 10));
            var chat = shell.Chat;
            Assert.True(Ui.PumpUntil(() => chat.HasSelectedModel, 5), "no model selected");
            Assert.True(chat.CanUseAgent);
            // With a project open the chat is an agent from the start (D30): no click needed.
            Assert.True(chat.AgentMode, "Agent was not on with a project open");
            Assert.True(window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "AgentToggle").IsChecked);
            chat.Draft = "Change the greeting in notes.txt to say agent";
            chat.SendCommand.Execute(null);

            // The read runs at once; the edit waits on the user.
            var reply = () => chat.Messages.Last();
            Assert.True(Ui.PumpUntil(() => reply().Steps.Any(s => s.IsAsking), 10), "the edit did not ask");
            Assert.Equal(StepState.Done, reply().Steps[0].State);
            Assert.StartsWith("Read notes.txt", reply().Steps[0].Text, StringComparison.Ordinal);
            Assert.Contains("+ hello agent", reply().Steps[1].Detail, StringComparison.Ordinal);
            var allow = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StepAllow" && b.IsEffectivelyVisible);
            Input.Click(window, allow);
            Assert.True(Ui.PumpUntil(() => !chat.IsStreaming, 10), "the turn did not finish");
            Assert.Equal("Changed the greeting.", reply().Text);
            Assert.StartsWith("Edited notes.txt", reply().Steps[1].Text, StringComparison.Ordinal);

            // The edit is in the editor, unsaved and undoable; the file on disk is as it was.
            var tab = shell.Workbench.AllTabs.Single(t => t.Title == "notes.txt");
            Assert.Equal("hello agent\n", tab.Document.Text.ToString());
            Assert.True(tab.IsDirty);
            Assert.Equal("hello world\n", File.ReadAllText(Path.Combine(root, "notes.txt")));

            // The model saw the tools, the project's rules, and each tool's answer.
            var last = world.Engine.Requests.Last();
            Assert.Contains(last.Tools!, t => t.Name == "edit_file");
            Assert.Contains("Keep greetings short.", last.Messages[0].Content, StringComparison.Ordinal);
            Assert.Contains(last.Messages, m => m.Role == ChatRole.Tool && m.ToolCallId == "c1" && m.Content.Contains("1: hello world", StringComparison.Ordinal));
            Assert.Contains(last.Messages, m => m.Role == ChatRole.Tool && m.ToolCallId == "c2" && m.Content.StartsWith("Edited notes.txt", StringComparison.Ordinal));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AskModeSendsTheUserToAgentAndTheChoiceOutlivesTheProject()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        var world = new ChatWorld();
        world.Runtime.Set(new RunningModel("agent:Q4", "Agent Model", RunState.Loaded, null, false, false, 8192, null));
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var chat = shell.Chat;
            Assert.False(chat.AgentMode);   // no project, nothing to be an agent in
            var opened = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && chat.CanUseAgent, 10));
            Assert.True(Ui.PumpUntil(() => chat.HasSelectedModel, 5));
            Assert.True(Ui.PumpUntil(() => chat.AgentMode, 5), "Agent was not on with a project open");

            // The user turns Agent off: the question goes without tools, and the model is told how to get them.
            Input.Click(window, window.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Name == "AgentToggle"));
            Assert.False(chat.AgentMode);
            chat.Draft = "write Program.cs for me";
            chat.SendCommand.Execute(null);
            Assert.True(Ui.PumpUntil(() => !chat.IsStreaming && world.Engine.Requests.Count == 1, 10), "the reply did not finish");
            var asked = world.Engine.Requests[0];
            Assert.Null(asked.Tools);
            Assert.Equal(ChatRole.System, asked.Messages[0].Role);
            Assert.Contains("switch on Agent", asked.Messages[0].Content, StringComparison.Ordinal);

            // Closing the project and opening it again keeps the user's choice.
            var closed = shell.Workbench.CloseFolderAsync();
            Assert.True(Ui.PumpUntil(() => closed.IsCompleted && !chat.CanUseAgent, 10));
            Assert.False(chat.AgentMode);
            opened = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && chat.CanUseAgent, 10));
            Ui.Pump(0.2);
            Assert.False(chat.AgentMode, "the user's Ask was not kept");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ADeniedCommandIsNotRunAndTheModelIsToldSo()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "ran.txt");
        var world = new ChatWorld();
        world.Runtime.Set(new RunningModel("agent:Q4", "Agent Model", RunState.Loaded, null, false, false, 8192, null));
        world.Engine.Script = (request, ct) => request.Messages.Any(m => m.Role == ChatRole.Tool)
            ? FakeEngine.Reply("Understood.", 1, TimeSpan.FromMilliseconds(5), ct)
            : Call("c1", "run_command", "{\"command\":\"touch ran.txt\"}", ct);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var opened = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted, 10));
            var chat = shell.Chat;
            Assert.True(Ui.PumpUntil(() => chat.HasSelectedModel, 5));
            chat.AgentMode = true;
            chat.Draft = "make a file";
            chat.SendCommand.Execute(null);
            Assert.True(Ui.PumpUntil(() => chat.Messages.Last().Steps.Any(s => s.IsAsking), 10));
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "StepDeny" && b.IsEffectivelyVisible));
            Assert.True(Ui.PumpUntil(() => !chat.IsStreaming, 10));
            Assert.False(File.Exists(marker));
            Assert.Equal(StepState.Denied, chat.Messages.Last().Steps[0].State);
            Assert.Contains(world.Engine.Requests.Last().Messages, m => m.Role == ChatRole.Tool && m.Content.Contains("did not allow", StringComparison.Ordinal));
            window.Close();
            return 0;
        });
    }
}
