using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Wicked.Core.Chat;
using Wicked.Tests.Models;

namespace Wicked.Tests.Chat;

/// <summary>The assistant panel, walked the way a person uses it: type, send, stop, regenerate, edit, switch model.</summary>
public sealed class ChatWindowTests
{
    private static ChatWorld WorldWithModels(bool images = false)
    {
        var world = new ChatWorld();
        world.Runtime.Set(
            FakeRuntime.Model("test/alpha", "Alpha 8B", images: false, reasons: true),
            FakeRuntime.Model("test/beta", "Beta Vision", RunState.Unloaded, images: images));
        return world;
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task EnterSendsAnAnswerStreamsInAndShiftEnterIsANewLine()
    {
        using var profile = new TempProfile();
        var world = WorldWithModels();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Assert.True(Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5));
            ModelsUi.Shot(window, "chat-empty");

            // Shift+Enter writes a second line and sends nothing; Enter sends the two.
            ChatUi.Type(window, "first line");
            Input.Chord(window, Key.Enter, RawInputModifiers.Shift);
            window.KeyTextInput("second line");
            Ui.Pump(0.1);
            Assert.Empty(world.Engine.Requests);
            Assert.Equal("first line\nsecond line", shell.Chat.Draft.Replace("\r\n", "\n"));
            Input.Chord(window, Key.Enter, RawInputModifiers.None);

            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 1 && !shell.Chat.IsStreaming, 10), "the reply did not finish");
            Assert.Equal("", shell.Chat.Draft);
            var request = world.Engine.Requests[0];
            Assert.Equal("test/alpha", request.Model);
            Assert.Equal("first line\nsecond line", request.Messages[^1].Content.Replace("\r\n", "\n"));
            Assert.Contains("You said: first line", ChatUi.AnswerText(window, 0), StringComparison.Ordinal);
            Assert.StartsWith("42.5 tok/s · ", ChatUi.All<TextBlock>(window, "FooterText").Last().Text, StringComparison.Ordinal);
            Assert.Equal("first line", shell.Chat.CurrentTitle);
            Assert.True(shell.Chat.HasContext, "no context meter");
            ModelsUi.Shot(window, "chat-reply");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task StopEndsAReplyWhereItIsAndKeepsWhatArrived()
    {
        using var profile = new TempProfile();
        var world = WorldWithModels();
        world.Engine.Script = (_, ct) => FakeEngine.Endless(TimeSpan.FromMilliseconds(10), ct);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            ChatUi.Send(window, "go on forever");
            Assert.True(Ui.PumpUntil(() => shell.Chat.IsStreaming && ChatUi.Answers(window).Count > 0 && ChatUi.AnswerText(window, 0).Contains("word5", StringComparison.Ordinal), 10), "nothing streamed");
            Assert.NotNull(ChatUi.Named(window, "Stop"));
            Assert.Null(ChatUi.Named(window, "Send"));

            // Escape stops it, as the Stop button does.
            Input.Chord(window, Key.Escape, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming, 10), "the reply did not stop");
            var kept = ChatUi.AnswerText(window, 0);
            Assert.Contains("word0", kept, StringComparison.Ordinal);
            Ui.Pump(0.3);
            Assert.Equal(kept, ChatUi.AnswerText(window, 0)); // nothing more arrives after the stop
            Assert.Contains("stopped", ChatUi.All<TextBlock>(window, "FooterText").Last().Text, StringComparison.Ordinal);
            Assert.NotNull(ChatUi.Named(window, "Send"));

            // The Stop button does the same.
            ChatUi.Send(window, "again");
            Assert.True(Ui.PumpUntil(() => shell.Chat.IsStreaming, 5));
            Input.Click(window, ChatUi.Named(window, "Stop")!);
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming, 10));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task RegenerateAsksAgainAndReplacesTheLastReply()
    {
        using var profile = new TempProfile();
        var world = WorldWithModels();
        var n = 0;
        world.Engine.Script = (r, ct) => FakeEngine.Reply($"Version {++n}", 2, TimeSpan.FromMilliseconds(5), ct);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            ChatUi.Send(window, "write something");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && ChatUi.Answers(window).Count == 1, 10));
            Assert.Contains("Version 1", ChatUi.AnswerText(window, 0), StringComparison.Ordinal);

            Input.Click(window, ChatUi.Named(window, "Regenerate")!);
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 2 && !shell.Chat.IsStreaming, 10), "no second request");
            Assert.Single(ChatUi.Answers(window));
            Assert.Contains("Version 2", ChatUi.AnswerText(window, 0), StringComparison.Ordinal);
            Assert.Equal(2, shell.Chat.Messages.Count);
            // The regenerated request carries the same question, not the old answer.
            Assert.Equal(["write something"], world.Engine.Requests[1].Messages.Select(m => m.Content));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task EditingAnEarlierQuestionDropsTheTurnsAfterItAndAsksAgain()
    {
        using var profile = new TempProfile();
        var world = WorldWithModels();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            ChatUi.Send(window, "one");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && shell.Chat.Messages.Count == 2, 10));
            ChatUi.Send(window, "two");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && shell.Chat.Messages.Count == 4, 10));

            // Edit the first question.
            Input.Click(window, ChatUi.All<Button>(window, "EditMessageButton").First());
            var box = ChatUi.Control<TextBox>(window, "EditBox");
            Assert.Equal("one", box.Text);
            box.Text = "uno";
            Ui.Pump(0.1);
            Input.Click(window, ChatUi.Control<Button>(window, "ResendButton"));
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 3 && !shell.Chat.IsStreaming, 10), "no resend");

            Assert.Equal(["uno", "You said: uno"], shell.Chat.Messages.Select(m => m.Text));
            Assert.Equal(["uno"], world.Engine.Requests[2].Messages.Select(m => m.Content));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheModelChipSwitchesTheModelEvenMidConversation()
    {
        using var profile = new TempProfile();
        var world = WorldWithModels();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            Assert.Equal("Alpha 8B", ChatUi.Control<TextBlock>(window, "ModelChipText").Text); // the loaded one is chosen
            ChatUi.Send(window, "hello");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && shell.Chat.Messages.Count == 2, 10));

            Input.Click(window, ChatUi.Control<Button>(window, "ModelChip"));
            Assert.True(Ui.PumpUntil(() => ChatUi.Named(window, "Beta Vision") is not null, 5), "the model list did not open");
            ModelsUi.Shot(window, "chat-model-chip");
            Input.Click(window, ChatUi.Named(window, "Beta Vision")!);
            Ui.Pump(0.4);
            Assert.Equal("Beta Vision", ChatUi.Control<TextBlock>(window, "ModelChipText").Text);

            ChatUi.Send(window, "and now?");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 2 && !shell.Chat.IsStreaming, 10));
            Assert.Equal("test/alpha", world.Engine.Requests[0].Model);
            Assert.Equal("test/beta", world.Engine.Requests[1].Model);
            // The whole conversation goes along to the new model.
            Assert.Equal(["hello", "You said: hello", "and now?"], world.Engine.Requests[1].Messages.Select(m => m.Content));
            window.Close();
            return 0;
        });
    }
}
