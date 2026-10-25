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
        Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3 + shell.Models.CompletionPicks.Count, 20), "no recommendations appeared");
        Input.Click(window, Control<Button>(window, "BrowseAll"));
        Assert.True(Ui.PumpUntil(() => Rows(window).Count > 0, 10), "the catalog did not appear");
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheCodeCompletionRecommendationHasACardAndAnInstallOfItsOwn()
    {
        // Before anything is downloaded: a chat card installs its chat model only; the completion model is offered apart, said to
        // be unable to chat, with its own size and its own Install.
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync(completion: true);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            _ = shell.Models.StartAsync(allowNetwork: true);
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && shell.Models.CompletionPicks.Count == 1 && ModelsUi.Cards(window).Count() == 4, 20), "no recommendations appeared");

            var chatCards = Control<ItemsControl>(window, "PickList").GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("pick")).Select(b => ((PickCard)b.DataContext!).Name).ToList();
            Assert.DoesNotContain("Tiny Complete", chatCards);
            var section = Control<StackPanel>(window, "CompletionPickSection");
            Assert.Contains("CODE COMPLETION FOR THIS PC", section.GetVisualDescendantsText(), StringComparison.Ordinal);
            Assert.Contains("It does not chat", section.GetVisualDescendantsText(), StringComparison.Ordinal);
            var completion = section.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("pick"));
            var card = (PickCard)completion.DataContext!;
            Assert.Equal("Tiny Complete", card.Name);
            Assert.Equal("CODE COMPLETION ONLY", card.Role);
            Assert.EndsWith("It cannot chat.", card.Reason, StringComparison.Ordinal);
            var coder = (PickCard)ModelsUi.Card(window, "Tiny Coder").DataContext!;
            Assert.Equal($"{ModelsViewModel.Size(coder.Pick.Tier.Size)} · mit", coder.Details); // its own size, nothing else folded in
            ModelsUi.Shot(window, "picks-completion");

            // The coding card installs the coding model alone; the completion card installs the completion model.
            Input.Click(window, ModelsUi.Labelled(ModelsUi.Card(window, "Tiny Coder"), "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(ModelWorld.Coder).Busy, 30), "the coding install did not finish");
            Ui.Pump(0.5);
            Assert.Equal([ModelWorld.Coder], shell.Models.Installed.Select(r => r.Id));
            Assert.True(card.CanInstall);
            completion.BringIntoView(); // below the chat cards: scrolled to, as a person scrolls
            Ui.Pump(0.2);
            Input.Click(window, ModelsUi.Labelled(completion, "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 2 && !shell.Models.Item(ModelWorld.Complete).Busy, 30), "the completion install did not finish");
            Assert.True(Ui.PumpUntil(() => shell.Models.CompletionInstalled.Count == 1 && shell.Models.ChatInstalled.Count == 1, 5));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task HuggingFaceReposSayWhatTheyAreForBeforeTheyAreDownloaded()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync(completion: true);
        world.Hub.Add(new HubRepo("someone/Qwen3-8B-Base-GGUF", "r1") { Downloads = 9000, Files = { ["Qwen3-8B-Base-Q4_K_M.gguf"] = StandIns.Weights(50_000) } });
        world.Hub.Add(new HubRepo("someone/Helper-7B-Instruct-GGUF", "r1") { Downloads = 8000, Files = { ["Helper-7B-Instruct-Q4_K_M.gguf"] = StandIns.Weights(50_000) } });
        world.Hub.Add(new HubRepo("someone/Mystery-GGUF", "r1") { Downloads = 7000, Files = { ["Mystery-Q4_K_M.gguf"] = StandIns.Weights(50_000) } });
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            OpenBrowse(window, shell);
            var browse = shell.Models.Browse;
            Input.Click(window, Control<Button>(window, "HubTabButton"));
            Assert.True(Ui.PumpUntil(() => browse.HubResults.Count >= 4, 10), browse.HubStatus);
            string Use(string name) => browse.HubResults.Single(r => r.Name == name).Use;
            Assert.Equal("Base model: continues or fills in text, cannot chat", Use("Qwen3-8B-Base-GGUF"));
            Assert.Equal("Chat model (an instruct or chat version)", Use("Helper-7B-Instruct-GGUF"));
            Assert.StartsWith("Hugging Face does not say whether it can chat", Use("Mystery-GGUF"), StringComparison.Ordinal);
            Assert.Equal("Code completion only: fills in code as you type, cannot chat", Use("Tiny-Complete-GGUF"));
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Name == "HubResultUse" && t.IsEffectivelyVisible && t.Text == Use("Qwen3-8B-Base-GGUF"));

            // The repo page says it too, above its quants.
            var item = Control<ListBox>(window, "HubResultList").GetVisualDescendants().OfType<ListBoxItem>().First(i => ((HubResultRow)i.DataContext!).Name == "Qwen3-8B-Base-GGUF");
            Input.Click(window, item);
            Assert.True(Ui.PumpUntil(() => browse.Detail is { Loading: false } d && d.Repo == "someone/Qwen3-8B-Base-GGUF", 15), "the repo did not open");
            Assert.Equal("Base model: continues or fills in text, cannot chat", Control<TextBlock>(window, "HubDetailUse").Text);
            ModelsUi.Shot(window, "hub-use");
            window.Close();
            return 0;
        });
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

    [Trait("Tier", "Window")]
    [Fact]
    public async Task WithNoChatModelThatFitsThePanelSaysSoAndTheCompletionCardSaysItOnlyCompletes()
    {
        // The user (2026-10-25): recommend only conversation models that fit; when none does, make it obvious what is
        // conversation and what only completes code.
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync(completion: true);
        world.PublishCatalog(world.Catalog with { Families = [.. world.Catalog.Families.Select(f => f with { Models = [.. f.Models.Where(m => !m.Has(Wicked.Core.Catalog.Capability.Chat))] })] });
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            _ = shell.Models.StartAsync(allowNetwork: true);
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.CompletionPicks.Count == 1 && ModelsUi.Cards(window).Count() == 1, 20), "the completion card did not appear");
            Assert.Empty(shell.Models.Picks);

            var note = Control<TextBlock>(window, "ChatPickNote");
            Assert.StartsWith("No chat model fits this PC's memory", note.Text, StringComparison.Ordinal);
            Assert.Contains("only completes code", note.Text, StringComparison.Ordinal);
            var card = (PickCard)Control<StackPanel>(window, "CompletionPickSection").GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("pick")).DataContext!;
            Assert.Equal("Tiny Complete", card.Name);
            Assert.Equal("CODE COMPLETION ONLY", card.Role);
            Assert.EndsWith("It cannot chat or work as the agent.", card.Reason, StringComparison.Ordinal);
            ModelsUi.Shot(window, "picks-no-chat");
            window.Close();
            return 0;
        });
    }
}
