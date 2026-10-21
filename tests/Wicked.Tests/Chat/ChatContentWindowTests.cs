using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Wicked.App.Chat;
using Wicked.Core.Chat;
using Wicked.Editor.Model;
using Wicked.Tests.Models;

namespace Wicked.Tests.Chat;

/// <summary>What a reply is made of: thinking, code blocks you can copy and insert, pictures a model can see.</summary>
public sealed class ChatContentWindowTests
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private static ChatWorld World()
    {
        var world = new ChatWorld();
        world.Runtime.Set(
            FakeRuntime.Model("test/alpha", "Alpha 8B", reasons: true),
            FakeRuntime.Model("test/beta", "Beta Vision", images: true));
        return world;
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ThinkingStreamsInView_ThenFoldsIntoAThoughtLineThatOpensAgain()
    {
        using var profile = new TempProfile();
        var world = World();
        world.Engine.Script = (_, ct) => FakeEngine.Reply("The answer is 42.", 2, TimeSpan.FromMilliseconds(5), ct,
            reasoning: "First I consider the question. Then I weigh the options.", thinkFor: TimeSpan.FromSeconds(1.3));
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            ChatUi.Send(window, "what is it?");

            // While it thinks, the thinking is on screen as it streams.
            Assert.True(Ui.PumpUntil(() => ChatUi.All<MarkdownView>(window, "ReasoningView").Any(v => ChatUi.TextOf(v).Contains("First I consider", StringComparison.Ordinal)), 10), "the thinking did not show");
            Assert.StartsWith("Thinking", ChatUi.Control<TextBlock>(window, "ThoughtLabel").Text, StringComparison.Ordinal);
            ModelsUi.Shot(window, "chat-thinking");

            // Done: folded to "Thought for N s", the text out of sight.
            Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming, 15));
            Ui.Pump(0.2);
            Assert.Equal("Thought for 1 s", ChatUi.Control<TextBlock>(window, "ThoughtLabel").Text);
            Assert.True(ChatUi.Absent<MarkdownView>(window, "ReasoningView"), "the thinking stayed open");
            Assert.Contains("The answer is 42.", ChatUi.AnswerText(window, 0), StringComparison.Ordinal);
            Assert.DoesNotContain("First I consider", ChatUi.AnswerText(window, 0), StringComparison.Ordinal); // the thinking is not part of the answer

            // Expand it and the whole of it is there.
            Input.Click(window, ChatUi.Control<Button>(window, "ThoughtToggle"));
            Assert.Contains("Then I weigh the options.", ChatUi.TextOf(ChatUi.Control<MarkdownView>(window, "ReasoningView")), StringComparison.Ordinal);
            ModelsUi.Shot(window, "chat-thought-open");
            Input.Click(window, ChatUi.Control<Button>(window, "ThoughtToggle"));
            Assert.True(ChatUi.Absent<MarkdownView>(window, "ReasoningView"));

            // The thinking is never sent back to the model.
            ChatUi.Send(window, "and?");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 2 && !shell.Chat.IsStreaming, 15));
            Assert.All(world.Engine.Requests[1].Messages, m => Assert.Null(m.Reasoning));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ACodeBlockIsHighlightedCopiesAndInsertsIntoTheOpenFile()
    {
        using var profile = new TempProfile();
        var world = World();
        const string Code = "int x = 1;\nreturn x;";
        world.Engine.Script = (_, ct) => FakeEngine.Reply($"Try this:\n\n```csharp\n{Code}\n```\n\nThat is all.", 6, TimeSpan.FromMilliseconds(5), ct);
        var path = Path.Combine(profile.Root, "notes.cs");
        File.WriteAllText(path, "// notes\n");
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);
            ChatUi.Send(window, "show me");
                        Assert.True(Ui.PumpUntil(() => !shell.Chat.IsStreaming, 15));
            var block = ChatUi.CodeBlocks(window).Single();
            Assert.Equal("csharp", ChatUi.Control<TextBlock>(window, "LanguageLabel").Text);

            // Highlighted off the UI thread: the lines arrive in more than one colour.
            Assert.True(Ui.PumpUntil(() => block.Body.Lines.Count == 2 && block.Body.Lines[0].Spans.Select(s => s.Color).Distinct().Count() > 1, 15), "the code was not highlighted");
            ModelsUi.Shot(window, "chat-code-block");

            // No file open: Insert is not offered.
            var insert = ChatUi.Control<Button>(window, "InsertCode");
            Assert.False(insert.IsEnabled);

            // Copy puts the code on the clipboard.
            Input.Click(window, ChatUi.Control<Button>(window, "CopyCode"));
            var read = window.Clipboard!.TryGetTextAsync();
            Assert.True(Ui.PumpUntil(() => read.IsCompleted, 5));
            Assert.Equal(Code, read.Result);
            Assert.Equal("Copied", ChatUi.Control<TextBlock>(window, "CopyLabel").Text);

            // Open a file: Insert puts the code at the caret.
            var opening = shell.Workbench.OpenAsync(path);
            Assert.True(Ui.PumpUntil(() => opening.IsCompleted && shell.Workbench.ActiveTab is not null, 10));
            Assert.True(Ui.PumpUntil(() => insert.IsEnabled, 5), "Insert stayed off with a file open");
            var document = shell.Workbench.ActiveTab!.Document;
            document.SetSelections([Selection.Caret(0)]);
            Input.Click(window, insert);
            Assert.Equal("int x = 1;\nreturn x;// notes\n", document.Text.ToString());

            // With a selection, it replaces the selection.
            document.SetSelections([new Selection(0, document.Text.Length)]);
            Input.Click(window, insert);
            Assert.Equal(Code, document.Text.ToString());
            Assert.True(document.CanUndo);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task APictureCanBeAttachedOnlyWhenTheModelSeesAndGoesWithTheMessage()
    {
        using var profile = new TempProfile();
        var world = World();
        var file = Path.Combine(profile.Root, "dot.png");
        File.WriteAllBytes(file, Png);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Ui.PumpUntil(() => shell.Chat.SelectedModel is not null, 5);

            // Alpha cannot see: no attach button, and a picture is refused with a word about it.
            Assert.Equal("Alpha 8B", shell.Chat.SelectedModel!.Name);
            Assert.True(ChatUi.Absent<Button>(window, "AttachButton"));
            var refused = shell.Chat.AddImageFilesAsync([file]);
            Assert.True(Ui.PumpUntil(() => refused.IsCompleted, 5));
            Assert.Empty(shell.Chat.Attachments);
            Assert.Equal("Alpha 8B does not take images.", ChatUi.Control<TextBlock>(window, "ComposerNote").Text);

            // Beta can.
            ChatUi.ChooseModel(window, "Beta Vision");
            Assert.NotNull(ChatUi.Control<Button>(window, "AttachButton"));
            var accepted = shell.Chat.AddImageFilesAsync([file]);
            Assert.True(Ui.PumpUntil(() => accepted.IsCompleted && shell.Chat.Attachments.Count == 1, 5));
            Assert.NotNull(ChatUi.Control<ItemsControl>(window, "AttachmentList"));
            Assert.True(ChatUi.Absent<TextBlock>(window, "ComposerNote"));

            // A file that is not a picture is turned away with a word.
            var notePath = Path.Combine(profile.Root, "x.txt");
            File.WriteAllText(notePath, "x");
            var text = shell.Chat.AddImageFilesAsync([notePath]);
            Assert.True(Ui.PumpUntil(() => text.IsCompleted, 5));
            Assert.Single(shell.Chat.Attachments);
            Assert.Contains("not a picture type", shell.Chat.ComposerNote, StringComparison.Ordinal);

            ChatUi.Send(window, "what is this?");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 1 && !shell.Chat.IsStreaming, 10));
            var sent = world.Engine.Requests[0].Messages.Single(m => m.Role == ChatRole.User);
            var image = Assert.Single(sent.Images!);
            Assert.Equal("image/png", image.MediaType);
            Assert.Equal(Png, image.Data);
            Assert.Empty(shell.Chat.Attachments);
            Assert.True(Ui.PumpUntil(() => shell.Chat.Messages[0].Thumbnails.Count == 1, 5), "the picture is not shown in the conversation");
            ModelsUi.Shot(window, "chat-picture");

            // Back to a model that cannot see: the picture stays in the conversation but is not sent again.
            ChatUi.ChooseModel(window, "Alpha 8B");
            ChatUi.Send(window, "and now?");
            Assert.True(Ui.PumpUntil(() => world.Engine.Requests.Count == 2 && !shell.Chat.IsStreaming, 10));
            Assert.All(world.Engine.Requests[1].Messages, m => Assert.Null(m.Images));
            window.Close();
            return 0;
        });
    }
}
