using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.Core.Chat;
using Wicked.Core.Settings;
using Wicked.Tests.Models;

namespace Wicked.Tests.Chat;

/// <summary>Conversations kept, found again, renamed and deleted; and the system prompts a chat starts with.</summary>
public sealed class ChatConversationsWindowTests
{
    private static ChatWorld World()
    {
        var world = new ChatWorld();
        world.Runtime.Set(FakeRuntime.Model("test/alpha", "Alpha 8B"), FakeRuntime.Model("test/beta", "Beta 3B", RunState.Unloaded));
        return world;
    }

    private static void ShowHistory(Avalonia.Controls.Window window, Wicked.App.ViewModels.ShellViewModel shell)
    {
        if (!shell.Chat.HistoryOpen) Input.Click(window, ChatUi.Control<Button>(window, "HistoryButton"));
        Ui.Pump(0.2);
    }

    [Fact]
    public async Task AFinishedReplySavesTheChatAndTheNextStartReopensIt()
    {
        using var profile = new TempProfile();
        var world = World();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            _ = shell.Chat.StartAsync();
            ChatUi.Send(window, "Explain monads\nin one line");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && shell.Chat.Messages.Count == 2, 10));
            Assert.True(Ui.PumpUntil(() => world.Store.All.Count == 1, 5), "nothing was saved");
            var saved = world.Store.All[0];
            Assert.Equal("Explain monads", saved.Title);
            Assert.Equal("test/alpha", saved.Model);
            Assert.Equal([ChatRole.User, ChatRole.Assistant], saved.Messages.Select(m => m.Role));
            Assert.Contains("You said: Explain monads", saved.Messages[1].Content, StringComparison.Ordinal);
            window.Close();

            // A fresh window on the same store comes back to it, with the reply drawn as it was.
            var (again, second) = world.Start(profile.Paths);
            _ = second.Chat.StartAsync();
            Assert.True(Ui.PumpUntil(() => second.Chat.Messages.Count == 2, 10), "the chat was not reopened");
            Assert.True(Ui.PumpUntil(() => ChatUi.Answers(again).Count == 1 && ChatUi.AnswerText(again, 0).Contains("You said: Explain monads", StringComparison.Ordinal), 5));
            Assert.Equal("Explain monads", ChatUi.Control<TextBlock>(again, "ChatTitle").Text);
            Assert.Equal("test/alpha", second.Chat.SelectedModel!.Id);
            again.Close();
            return 0;
        });
    }

    [Fact]
    public async Task AChatClearedBeforeClosingIsStillClearWhenWickedStartsAgain()
    {
        using var profile = new TempProfile();
        var world = World();
        await Ui.Run(() =>
        {
            // Wicked started as a user starts it, a chat, then /clear, then closed.
            var (window, shell) = world.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted && shell.Chat.SelectedModel is not null, 10));
            ChatUi.Send(window, "hello");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && world.Store.All.Count == 1, 10), "nothing was saved");
            ChatUi.Send(window, "/clear");
            Assert.True(Ui.PumpUntil(() => shell.Chat.Messages.Count == 0, 5));
            window.Close();
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SessionFile) && File.ReadAllText(profile.Paths.SessionFile).Contains("chatConversation", StringComparison.OrdinalIgnoreCase), 10), "the session was not saved");

            // Started again: still clear, the old chat in Past chats.
            var (again, second) = world.Start(profile.Paths);
            var restart = second.StartAsync();
            Assert.True(Ui.PumpUntil(() => restart.IsCompleted && second.Chat.History.Count == 1, 10), "the old chat is not in the history");
            Ui.Pump(0.5);
            Assert.Empty(second.Chat.Messages);
            Assert.Equal("hello", second.Chat.History[0].Title);

            // The other way round: a chat open when Wicked closes is open when it starts.
            ChatUi.Send(again, "second topic");
            Assert.True(Ui.PumpUntil(() => !second.Chat.IsStreaming && world.Store.All.Count == 2, 10));
            again.Close();
            Assert.True(Ui.PumpUntil(() => File.ReadAllText(profile.Paths.SessionFile).Contains(second.Chat.History.First(h => h.Title == "second topic").Id.ToString(), StringComparison.Ordinal), 10), "the open chat was not remembered");
            var (third, thirdShell) = world.Start(profile.Paths);
            var last = thirdShell.StartAsync();
            Assert.True(Ui.PumpUntil(() => last.IsCompleted && thirdShell.Chat.Messages.Count == 2, 10), "the open chat did not come back");
            Assert.Equal("second topic", thirdShell.Chat.CurrentTitle);
            third.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ChatsAreListedNewestFirstOpenedRenamedAndDeletedWithAnAsk()
    {
        using var profile = new TempProfile();
        var world = World();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            _ = shell.Chat.StartAsync();

            ChatUi.Send(window, "first topic");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && shell.Chat.Messages.Count == 2, 10));
            // Ctrl+N in the panel starts a fresh chat; the first one is kept.
            Input.Chord(window, Key.N, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => shell.Chat.Messages.Count == 0, 5));
            Assert.Equal("New chat", shell.Chat.CurrentTitle);
            ChatUi.Send(window, "second topic");
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming && shell.Chat.Messages.Count == 2, 10));
            Assert.True(Ui.PumpUntil(() => world.Store.All.Count == 2, 5));

            // The history lists both, the newest first.
            Input.Click(window, ChatUi.Control<Button>(window, "HistoryButton"));
            Assert.True(Ui.PumpUntil(() => ChatUi.All<Button>(window, "OpenChat").Count() == 2, 5));
            Assert.Equal(["second topic", "first topic"], shell.Chat.History.Select(h => h.Title));
            ModelsUi.Shot(window, "chat-history");

            // Open the older one.
            Input.Click(window, ChatUi.All<Button>(window, "OpenChat").Last());
            Assert.True(Ui.PumpUntil(() => shell.Chat.CurrentTitle == "first topic" && shell.Chat.Messages.Count == 2, 10));
            Assert.Contains("first topic", shell.Chat.Messages[0].Text, StringComparison.Ordinal);

            // Rename it through the name prompt.
            ShowHistory(window, shell);
            Assert.True(Ui.PumpUntil(() => ChatUi.All<Button>(window, "RenameChat").Any(), 5));
            Input.Click(window, ChatUi.All<Button>(window, "RenameChat").Last());
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen, 5), "no name prompt");
            shell.QuickInput.Query = "Monads, briefly";
            Input.Chord(window, Key.Enter, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => world.Store.All.Any(c => c.Title == "Monads, briefly"), 5), "the new name was not saved");
            Assert.Equal("Monads, briefly", shell.Chat.CurrentTitle);

            // Delete asks first: No keeps it, Yes removes it for good.
            ShowHistory(window, shell);
            Assert.True(Ui.PumpUntil(() => ChatUi.All<Button>(window, "DeleteChat").Any(), 5));
            Input.Click(window, ChatUi.All<Button>(window, "DeleteChat").Last());
            var dialog = window.FindControl<Border>("DialogLayer")!;
            Assert.True(Ui.PumpUntil(() => dialog.IsVisible, 5), "delete did not ask");
            ModelsUi.Shot(window, "chat-delete-ask");
            Input.Click(window, window.FindControl<Button>("DialogCancel")!);
            Assert.Equal(2, world.Store.All.Count);
            Input.Click(window, ChatUi.All<Button>(window, "DeleteChat").Last());
            Assert.True(Ui.PumpUntil(() => dialog.IsVisible, 5));
            Input.Click(window, window.FindControl<Button>("DialogSave")!);
            Assert.True(Ui.PumpUntil(() => world.Store.All.Count == 1, 5), "the chat was not deleted");
            Assert.Equal("Monads, briefly", world.Store.All[0].Title); // the renamed chat moved to the top, so the last row was the other
            Assert.Equal(["Monads, briefly"], shell.Chat.History.Select(h => h.Title));
            Assert.Equal("Monads, briefly", shell.Chat.CurrentTitle); // the open chat was not the one deleted
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheChatsSystemPromptIsShownAndTypedInItsChipAndClearStartsAFreshContext()
    {
        using var profile = new TempProfile();
        var world = World();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            _ = shell.Chat.StartAsync();
            Assert.Equal("System prompt", shell.Chat.PromptLabel);

            // The chip opens on this chat's prompt, in a box to type in.
            Input.Click(window, ChatUi.Control<Button>(window, "PromptChip"));
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<TextBox>().Any(b => b.Name == "SystemPromptBox"), 5), "the chip shows no prompt box");
            var box = ChatUi.Control<TextBox>(window, "SystemPromptBox");
            Assert.Equal("", box.Text);
            box.Focus();
            box.Text = "Reply in French.";
            Assert.True(Ui.PumpUntil(() => shell.Chat.PromptLabel == "Custom prompt", 5), "the chip does not say a prompt is set");
            Input.Chord(window, Key.Escape, RawInputModifiers.None);
            Ui.Pump(0.3);

            ChatUi.Send(window, "hello");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 1 && !shell.Chat.IsStreaming, 10));
            Assert.Equal(new ChatMessage(ChatRole.System, "Reply in French."), world.Engine.Requests[0].Messages[0]);
            Assert.True(Ui.PumpUntil(() => world.Store.All.Count == 1, 5));

            // /clear: a fresh chat with an empty context, nothing sent to the model, the old one kept in Past chats.
            ChatUi.Send(window, "/clear");
            Assert.True(Ui.PumpUntil(() => shell.Chat.Messages.Count == 0, 5), "/clear did not start over");
            Assert.Single(world.Engine.Requests);
            Assert.Equal("", shell.Chat.Draft);
            Assert.Contains(shell.Chat.History, h => h.Title == "hello");
            ChatUi.Send(window, "again");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 2 && !shell.Chat.IsStreaming, 10));
            Assert.Equal(["again"], world.Engine.Requests[1].Messages.Where(m => m.Role == ChatRole.User).Select(m => m.Content));
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task SystemPromptsAreKeptInSettingsAndEachChatKeepsTheOneItUsed()
    {
        using var profile = new TempProfile();
        var world = World();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            _ = shell.Chat.StartAsync();

            // Add a prompt in the editor and make it the default.
            Input.Click(window, ChatUi.Control<Button>(window, "PromptChip"));
            Assert.True(Ui.PumpUntil(() => ChatUi.Named(window, "No system prompt") is not null, 5));
            Input.Click(window, ChatUi.Control<Button>(window, "EditPromptsButton"));
            Ui.Pump(0.4);
            Input.Click(window, ChatUi.Control<Button>(window, "AddPrompt"));
            var name = ChatUi.Control<TextBox>(window, "PromptName");
            name.Text = "Terse";
            ChatUi.Control<TextBox>(window, "PromptText").Text = "Answer in one short sentence.";
            ChatUi.Control<ToggleSwitch>(window, "PromptDefault").IsChecked = true;
            Ui.Pump(0.2);
            ModelsUi.Shot(window, "chat-system-prompts");
            Input.Click(window, ChatUi.Control<Button>(window, "PromptsSave"));

            // settings.json now has them, under the names the settings use.
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains("chat.systemPrompts", StringComparison.Ordinal), 5), "settings.json was not written");
            var written = File.ReadAllText(profile.Paths.SettingsFile);
            Assert.Contains("\"chat.defaultSystemPrompt\": \"Terse\"", written, StringComparison.Ordinal);
            Assert.Contains("Answer in one short sentence.", written, StringComparison.Ordinal);
            var (settings, problem) = SettingsFile.ReadAsync(profile.Paths.SettingsFile, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Null(problem);
            Assert.Equal("Terse", Wicked.App.Chat.ChatSettings.DefaultName(settings));

            // A new chat starts with it: the model gets it as the system turn.
            Assert.True(Ui.PumpUntil(() => shell.Chat.PromptLabel == "Terse", 5));
            ChatUi.Send(window, "hello");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 1 && !shell.Chat.IsStreaming, 10));
            Assert.Equal(new ChatMessage(ChatRole.System, "Answer in one short sentence."), world.Engine.Requests[0].Messages[0]);
            Assert.True(Ui.PumpUntil(() => world.Store.All.Count == 1, 5));
            Assert.Equal("Answer in one short sentence.", world.Store.All[0].SystemPrompt);

            // Another chat can go without; the first keeps what it used.
            Input.Chord(window, Key.N, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => shell.Chat.Messages.Count == 0, 5));
            Assert.Equal("Terse", shell.Chat.PromptLabel);
            Input.Click(window, ChatUi.Control<Button>(window, "PromptChip"));
            Assert.True(Ui.PumpUntil(() => ChatUi.Named(window, "No system prompt") is not null, 5));
            Input.Click(window, ChatUi.Named(window, "No system prompt")!);
            Ui.Pump(0.4);
            ChatUi.Send(window, "plain");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 2 && !shell.Chat.IsStreaming, 10));
            Assert.DoesNotContain(world.Engine.Requests[1].Messages, m => m.Role == ChatRole.System);
            Assert.True(Ui.PumpUntil(() => world.Store.All.Count == 2, 5));

            var first = shell.Chat.History.Last();
            _ = shell.Chat.OpenAsync(first.Id);
            Assert.True(Ui.PumpUntil(() => shell.Chat.CurrentTitle == "hello" && shell.Chat.PromptLabel == "Terse", 5));
            window.Close();
            return 0;
        });
    }
}
