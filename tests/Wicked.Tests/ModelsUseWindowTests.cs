using Avalonia.Controls;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.Tests.Chat;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>
/// Chat models apart from code completion models, everywhere a model is chosen: Browse lists them on tabs of their own, the
/// installed list in sections of its own, and the chat never offers a model that only completes code (it rambles without end
/// when asked to chat). What this PC cannot run usefully is not listed.
/// </summary>
public sealed class ModelsUseWindowTests
{
    private static T Control<T>(Window window, string name) where T : Control
    {
        T? found = null;
        Ui.PumpUntil(() => (found = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)) is not null, 3);
        return found ?? throw new InvalidOperationException($"No visible {typeof(T).Name} called {name}");
    }

    private static List<CatalogRow> Rows(Window window) =>
        [.. window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("modelrow") && b.IsEffectivelyVisible && b.DataContext is CatalogRow).Select(b => (CatalogRow)b.DataContext!)];

    private static void OpenBrowse(Window window, ShellViewModel shell)
    {
        _ = shell.Models.StartAsync(allowNetwork: true);
        Input.Click(window, window.FindControl<Button>("RailModels")!);
        Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20), "no recommendations appeared");
        Input.Click(window, Control<Button>(window, "BrowseAll"));
        Assert.True(Ui.PumpUntil(() => Rows(window).Count > 0, 10), "the catalog did not appear");
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task BrowseListsChatAndCodeCompletionModelsOnTabsOfTheirOwnAndHidesWhatThisPcCannotRun()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync(completion: true);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            OpenBrowse(window, shell);
            var browse = shell.Models.Browse;

            // Chat models: no completion-only model, nothing too big for the machine, and the role chips.
            Assert.Equal(["Tiny Chat", "Tiny Coder", "Tiny Quick", "Tiny Chat 1", "Tiny Gated"], Rows(window).Select(r => r.Name));
            Assert.Equal("5 chat models · 1 too large for this PC is hidden", browse.CatalogNote);
            Assert.True(Control<ItemsControl>(window, "RoleChips").IsEffectivelyVisible);

            // Code completion: only what completes code, said plainly, without the chat roles.
            Input.Click(window, Control<Button>(window, "CompletionTabButton"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Select(r => r.Name).SequenceEqual(["Tiny Complete"]), 5), string.Join(", ", Rows(window).Select(r => r.Name)));
            Assert.Equal("1 code completion model", browse.CatalogNote);
            var note = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "UseNote" && t.IsEffectivelyVisible);
            Assert.Equal("Code completion only: fills in code as you type, cannot chat", note.Text);
            Assert.False(window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "RoleChips").IsEffectivelyVisible);
            Assert.Contains("active", Control<Button>(window, "CompletionTabButton").Classes);
            ModelsUi.Shot(window, "browse-completion");

            Input.Click(window, Control<Button>(window, "CatalogTabButton"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Count == 5, 5));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AnInstalledCompletionModelIsListedApartFromTheChatModels()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync(completion: true);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            OpenBrowse(window, shell);
            Input.Click(window, Control<Button>(window, "CompletionTabButton"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Count == 1, 5));
            var row = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("modelrow") && b.IsEffectivelyVisible);
            Input.Click(window, ModelsUi.Labelled(row, "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(ModelWorld.Complete).Busy, 30), "the install did not finish");
            Input.Click(window, Control<Button>(window, "CloseBrowse"));

            // The Models panel: under "Code completion only", with what that means; the chat models' list does not have it.
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<StackPanel>().Any(s => s.Name == "CompletionSection" && s.IsEffectivelyVisible), 5), "no code completion section");
            var completion = Control<ItemsControl>(window, "CompletionList");
            Assert.Contains("Tiny-Complete-GGUF", completion.GetVisualDescendantsText(), StringComparison.Ordinal);
            var chat = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "InstalledList");
            Assert.DoesNotContain(chat.GetVisualDescendants().OfType<Grid>(), g => g.Classes.Contains("installed"));
            Assert.Contains("cannot chat", Control<StackPanel>(window, "CompletionSection").GetVisualDescendantsText(), StringComparison.Ordinal);
            ModelsUi.Shot(window, "installed-completion");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheChatNeverOffersAModelThatOnlyCompletesCodeAndSaysWhyItIsEmpty()
    {
        using var profile = new TempProfile();
        var world = new ChatWorld();
        var completion = FakeRuntime.Model("test/complete:Q8_0", "Coder 1.5B (completion)") with { Chats = false, Completes = true };
        world.Runtime.Set(completion);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);

            // Only a completion model installed: the chat has no model, and says why rather than "No model yet".
            Assert.True(Ui.PumpUntil(() => ChatUi.Control<TextBlock>(window, "NoModelTitle").Text == "No chat model yet", 5), ChatUi.Control<TextBlock>(window, "NoModelTitle").Text);
            Assert.Contains("Coder 1.5B (completion) only completes code as you type", ChatUi.Control<TextBlock>(window, "NoModelText").Text, StringComparison.Ordinal);
            Assert.Null(shell.Chat.SelectedModel);
            Assert.Empty(shell.Chat.Models);
            Assert.False(shell.Chat.CanSend);
            Assert.Null(shell.AssistantModel());
            Assert.Equal("test/complete:Q8_0", shell.CompletionModel());
            ModelsUi.Shot(window, "chat-completion-only");

            // A chat model arrives: the chat takes it, its menu lists it alone, and completions keep the completion model.
            world.Runtime.Set(completion, FakeRuntime.Model("test/alpha:Q4_K_M", "Alpha 8B"));
            Assert.True(Ui.PumpUntil(() => shell.Chat.SelectedModel?.Id == "test/alpha:Q4_K_M", 5), "the chat model was not chosen");
            Assert.Equal(["test/alpha:Q4_K_M"], shell.Chat.Models.Select(m => m.Id));
            Input.Click(window, ChatUi.Control<Button>(window, "ModelChip"));
            Assert.True(Ui.PumpUntil(() => ChatUi.Named(window, "Alpha 8B") is not null, 5), "the model menu did not open");
            Assert.Null(ChatUi.Named(window, "Coder 1.5B (completion)"));
            Assert.Equal("test/alpha:Q4_K_M", shell.AssistantModel());
            Assert.Equal("test/complete:Q8_0", shell.CompletionModel());
            window.Close();
            return 0;
        });
    }
}
