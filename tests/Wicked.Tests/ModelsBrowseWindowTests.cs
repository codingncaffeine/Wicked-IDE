using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>The Browse page: the catalog with fit badges and a quant chooser, the Hugging Face search, installs from either, the gated-repo flow.</summary>
public sealed class ModelsBrowseWindowTests
{
    private static T Control<T>(Window window, string name) where T : Control
    {
        T? found = null;
        Ui.PumpUntil(() => (found = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)) is not null, 3);
        return found ?? throw new InvalidOperationException($"No visible {typeof(T).Name} called {name}");
    }

    private static void OpenBrowse(Window window, ShellViewModel shell)
    {
        _ = shell.Models.StartAsync(allowNetwork: true);
        Input.Click(window, window.FindControl<Button>("RailModels")!);
        Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20), "no recommendations appeared");
        Input.Click(window, Control<Button>(window, "BrowseAll"));
        Assert.True(window.FindControl<Border>("BrowseLayer")!.IsVisible);
        Assert.True(Ui.PumpUntil(() => shell.Models.Browse.Families.Count > 0 && Rows(window).Any(), 10), "the catalog did not appear");
    }

    private static List<Border> Rows(Window window) =>
        window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("modelrow") && b.IsEffectivelyVisible && b.DataContext is CatalogRow).ToList();

    private static Border Row(Window window, string name) => Rows(window).Single(r => ((CatalogRow)r.DataContext!).Name == name);

    private static void Type(Window window, TextBox box, string text)
    {
        box.Text = "";
        Input.Click(window, box);
        window.KeyTextInput(text);
        Ui.Pump(0.1);
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task TheCatalogListsEveryModelNewestFirstWithFitBadgesAQuantChooserFiltersAndInstall()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            OpenBrowse(window, shell);
            var browse = shell.Models.Browse;

            // Every family and chat model this PC can run, the family with the newest model first, each family newest first; the
            // 900 GB model is not listed at all, and the note says one was hidden.
            Assert.Equal(["Tiny", "Gated"], browse.Families.Select(f => f.Name));
            Assert.Equal(["Tiny Chat", "Tiny Coder", "Tiny Quick", "Tiny Chat 1"], browse.Families[0].Models.Select(m => m.Name));
            Assert.Equal(5, Rows(window).Count);
            Assert.Equal("5 chat models · 1 too large for this PC is hidden", browse.CatalogNote);
            Assert.DoesNotContain(Rows(window), r => ((CatalogRow)r.DataContext!).Name == "Huge Model");
            var chat = Row(window, "Tiny Chat");
            Assert.Contains("EVERYDAY", ((CatalogRow)chat.DataContext!).Roles, StringComparison.Ordinal);
            Assert.Contains("3B", ((CatalogRow)chat.DataContext!).Facts, StringComparison.Ordinal);
            Assert.Contains("32k context", ((CatalogRow)chat.DataContext!).Facts, StringComparison.Ordinal);

            // Fit badges: tiny models fit (in VRAM with a GPU, with offload without one).
            var badge = ((CatalogRow)chat.DataContext!).Fit;
            Assert.True(badge.Ok || badge.Offload, badge.Text);
            Assert.True(badge.Text is "Fits in VRAM" or "CPU offload");
            ModelsUi.Shot(window, "browse-catalog");

            // The quant chooser defaults to the recommender's quant (the same one the card offers), and changing it changes the size and the install.
            var chatRow = (CatalogRow)chat.DataContext!;
            Assert.Equal(shell.Models.Picks.Single(p => p.Name == "Tiny Chat").Pick.Tier.Quant, chatRow.SelectedTier!.Tier.Quant);
            Assert.Equal(["Q8_0", "Q4_K_M"], chatRow.Tiers.Select(t => t.Tier.Quant));
            Assert.Contains("MB", chatRow.Tiers[0].Label, StringComparison.Ordinal);
            var combo = chat.GetVisualDescendants().OfType<ComboBox>().Single();
            combo.SelectedItem = chatRow.Tiers[1];
            Assert.Equal("Q4_K_M", chatRow.SelectedTier.Tier.Quant);

            // The filter box: fuzzy by name; the chips: by role.
            Type(window, Control<TextBox>(window, "CatalogFilter"), "coder");
            Assert.True(Ui.PumpUntil(() => Rows(window).Count == 1, 5), "the filter did not narrow the list");
            Assert.Equal("Tiny Coder", ((CatalogRow)Rows(window)[0].DataContext!).Name);
            Type(window, Control<TextBox>(window, "CatalogFilter"), "zzzz");
            Assert.True(Ui.PumpUntil(() => browse.CatalogNote == "No model matches · 1 too large for this PC is hidden", 5), browse.CatalogNote);
            Type(window, Control<TextBox>(window, "CatalogFilter"), "");
            Assert.True(Ui.PumpUntil(() => Rows(window).Count == 5, 5));
            var chips = Control<ItemsControl>(window, "RoleChips").GetVisualDescendants().OfType<ToggleButton>().ToList();
            Input.Click(window, chips.Single(c => c.Content as string == "Coding"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Count == 2, 5));
            Assert.Equal(["Tiny Chat", "Tiny Coder"], Rows(window).Select(r => ((CatalogRow)r.DataContext!).Name));
            Input.Click(window, chips.Single(c => c.Content as string == "Coding"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Count == 5, 5));

            // Install the smaller quant from the page: the row follows the download and ends Installed; the card (another quant) still offers Install.
            chat = Row(window, "Tiny Chat"); // the rows were rebuilt by the filters; the chosen quant is remembered
            Assert.Equal(chatRow.Tiers[1], chat.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem);
            Input.Click(window, ModelsUi.Labelled(chat, "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(ModelWorld.Chat).Busy, 30), "the install did not finish");
            Assert.True(Ui.PumpUntil(() => chatRow.Installed && chat.GetVisualDescendantsText().Contains("Installed"), 5));
            var files = Directory.GetFiles(Path.Combine(profile.Paths.Data, "models", "test", "Tiny-Chat-GGUF"), "*.gguf");
            Assert.Equal(["Tiny-Chat-Q4_K_M.gguf"], files.Select(Path.GetFileName));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ADownloadFromTheCatalogCanBePausedResumedAndCancelledOnItsRow()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            OpenBrowse(window, shell);
            world.Hub.ChunkDelay = TimeSpan.FromMilliseconds(40);
            var row = Row(window, "Tiny Chat");
            var item = shell.Models.Item(ModelWorld.Chat);
            Input.Click(window, ModelsUi.Labelled(row, "Install")!);
            Assert.True(Ui.PumpUntil(() => item.Busy && item.Progress > 0.05 && ModelsUi.Named(row, "Pause") is not null, 20), "the download did not start");

            Input.Click(window, ModelsUi.Named(row, "Pause")!);
            Assert.True(Ui.PumpUntil(() => item.Paused && ModelsUi.Named(row, "Resume") is not null, 10));
            var frozen = item.Progress;
            Ui.Pump(0.5);
            Assert.Equal(frozen, item.Progress);
            Input.Click(window, ModelsUi.Named(row, "Resume")!);
            Assert.True(Ui.PumpUntil(() => !item.Paused && item.Progress > frozen, 10));
            Input.Click(window, ModelsUi.Named(row, "Cancel")!);
            Assert.True(Ui.PumpUntil(() => !item.Busy && ModelsUi.Labelled(row, "Install") is not null, 15), "the row did not return to Install");
            Assert.True(Ui.PumpUntil(() => !Directory.Exists(Path.Combine(profile.Paths.Data, "models")) || Directory.GetFiles(Path.Combine(profile.Paths.Data, "models"), "*", SearchOption.AllDirectories).Length == 0, 5), "files were left behind");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AGatedModelLeadsToTheTokenSettingAndInstallsOnceTheTokenIsRight()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("HF_TOKEN") is { Length: > 0 }, "HF_TOKEN is set in this environment");
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
            OpenBrowse(window, shell);
            var gated = Row(window, "Tiny Gated");

            // No token: a warning notice that leads to Settings → Models.
            Input.Click(window, ModelsUi.Labelled(gated, "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Notice?.Kind == NoticeKind.Warning, 10), shell.Notice?.Text);
            Assert.Contains("token", shell.Notice!.Text, StringComparison.Ordinal);
            Assert.Equal("settings/models", shell.Notice.Route);
            Assert.DoesNotContain(world.Hub.Requests, r => r.Contains("Tiny-Gated-GGUF/resolve", StringComparison.Ordinal));
            Assert.False(string.IsNullOrEmpty(Control<TextBlock>(window, "BrowseAlertText").Text));
            ModelsUi.Shot(window, "browse-gated-alert");
            Input.Click(window, Control<Button>(window, "BrowseAlertAction"));
            Assert.True(shell.SettingsOpen);
            Assert.False(shell.Models.Browse.IsOpen);
            var token = Control<TextBox>(window, "TokenBox");
            Assert.True(Ui.PumpUntil(() => token.IsFocused, 5), "the token box did not take the keyboard");

            // A wrong token: the Hub says no (403), and the notice says access was not granted.
            window.KeyTextInput("hf_wrong");
            Assert.True(Ui.PumpUntil(() => shell.Models.Services.Token == "hf_wrong", 5));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Ui.Pump(0.1);
            Input.Click(window, Control<Button>(window, "BrowseAll"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Any(), 5));
            Input.Click(window, ModelsUi.Labelled(Row(window, "Tiny Gated"), "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Notice?.Text.Contains("not given your account access", StringComparison.Ordinal) == true, 15), shell.Notice?.Text);
            Assert.Equal("settings/models", shell.Notice!.Route);

            // The right token: through the notice, the box, and the install goes through.
            Assert.Contains("not given your account access", Control<TextBlock>(window, "BrowseAlertText").Text, StringComparison.Ordinal);
            Input.Click(window, Control<Button>(window, "BrowseAlertAction"));
            Assert.True(shell.SettingsOpen);
            token = Control<TextBox>(window, "TokenBox");
            token.Text = "";
            Input.Click(window, token);
            window.KeyTextInput("hf_good");
            Assert.True(Ui.PumpUntil(() => shell.Models.Services.Token == "hf_good", 5));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Ui.Pump(0.1);
            Input.Click(window, Control<Button>(window, "BrowseAll"));
            Assert.True(Ui.PumpUntil(() => Rows(window).Any(), 5));
            gated = Row(window, "Tiny Gated");
            Input.Click(window, ModelsUi.Labelled(gated, "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(ModelWorld.Gated).Busy, 30), "the gated model did not install");
            Assert.Contains("Bearer hf_good", world.Hub.Authorizations);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheHuggingFaceTabSearchesShowsQuantsWithTheirFitAndInstallsOne()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        var header = () => new GgufBuilder().Llama(context: 32768, layers: 32, embedding: 4096, heads: 32, kvHeads: 8);
        var small = header().Build(fillerBytes: 150_000);
        var big = header().Build(fillerBytes: 400_000);
        world.Hub.Add(new HubRepo("someone/Fresh-7B-GGUF", "abc123") { License = "llama3", Downloads = 5000, Files = { ["Fresh-7B-Q4_K_M.gguf"] = small, ["Fresh-7B-Q8_0.gguf"] = big } });
        world.Hub.Add(new HubRepo("someone/Odd-GGUF", "r1") { Downloads = 4000, Files = { ["Odd-Q4_K_M.gguf"] = StandIns.Weights(50_000) } });
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            OpenBrowse(window, shell);
            var browse = shell.Models.Browse;
            Input.Click(window, Control<Button>(window, "HubTabButton"));
            Assert.True(browse.ShowHub);
            Assert.True(Ui.PumpUntil(() => browse.HubResults.Count >= 3 && browse.HubStatus == "Most downloaded GGUF models", 10), browse.HubStatus);
            Assert.Equal("Fresh-7B-GGUF", browse.HubResults[0].Name); // most downloaded first

            // Typing searches, debounced, on a worker; the request asks for GGUF repos sorted by downloads.
            Type(window, Control<TextBox>(window, "HubSearch"), "fresh");
            Assert.True(Ui.PumpUntil(() => browse.HubResults.Count == 1 && browse.HubStatus == "1 GGUF repo", 10), browse.HubStatus);
            Assert.Contains(world.Hub.Requests, r => r.Contains("search=fresh", StringComparison.Ordinal) && r.Contains("filter=gguf", StringComparison.Ordinal));

            // Choosing the repo lists its quants smallest first, each with a fit read from the GGUF header.
            var item = Control<ListBox>(window, "HubResultList").GetVisualDescendants().OfType<ListBoxItem>().Single();
            Input.Click(window, item);
            Assert.True(Ui.PumpUntil(() => browse.Detail is { Loading: false, Variants.Count: 2 }, 15), "the quants did not appear");
            Assert.Equal(["Q4_K_M", "Q8_0"], browse.Detail!.Variants.Select(v => v.Variant.Quant));
            Assert.Equal("Fresh-7B-GGUF", Control<TextBlock>(window, "HubDetailTitle").Text);
            Assert.All(browse.Detail.Variants, v => Assert.True(v.Fit.Ok || v.Fit.Offload, v.Fit.Text));
            Assert.Contains("GGUF header", browse.Detail.FitNote, StringComparison.Ordinal);
            ModelsUi.Shot(window, "browse-huggingface");

            // A repo whose header cannot be read says "Fit unknown" instead of guessing.
            Type(window, Control<TextBox>(window, "HubSearch"), "odd");
            Assert.True(Ui.PumpUntil(() => browse.HubResults.Count == 1 && browse.HubResults[0].Name == "Odd-GGUF", 10));
            Input.Click(window, Control<ListBox>(window, "HubResultList").GetVisualDescendants().OfType<ListBoxItem>().Single());
            Assert.True(Ui.PumpUntil(() => browse.Detail is { Loading: false, Variants.Count: 1 } d && d.Repo == "someone/Odd-GGUF", 15));
            Assert.Equal("Fit unknown", browse.Detail.Variants[0].Fit.Text);

            // Back to Fresh: its licence is a community one, so Install asks first, then installs the chosen quant.
            Type(window, Control<TextBox>(window, "HubSearch"), "fresh");
            Assert.True(Ui.PumpUntil(() => browse.HubResults.Count == 1 && browse.HubResults[0].Name == "Fresh-7B-GGUF", 10));
            Input.Click(window, Control<ListBox>(window, "HubResultList").GetVisualDescendants().OfType<ListBoxItem>().Single());
            Assert.True(Ui.PumpUntil(() => browse.Detail is { Loading: false, Variants.Count: 2 }, 15));
            var variant = Control<ItemsControl>(window, "VariantList").GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("modelrow"));
            Input.Click(window, ModelsUi.Labelled(variant, "Install")!);
            var prompt = window.FindControl<Border>("PromptLayer")!;
            Assert.True(Ui.PumpUntil(() => prompt.IsVisible, 10), "the licence was not asked about");
            Assert.Equal("Accept the llama3 licence?", Control<TextBlock>(window, "PromptTitle").Text);
            Input.Click(window, Control<Button>(window, "PromptConfirm"));
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item("someone/Fresh-7B-GGUF").Busy, 30), "the install did not finish");
            Assert.True(Ui.PumpUntil(() => browse.Detail!.Variants[0].Installed && !browse.Detail.Variants[1].Installed, 5));
            var manifest = shell.Models.Services.Store.ReadManifestAsync("someone/Fresh-7B-GGUF").GetAwaiter().GetResult();
            Assert.Equal("abc123", manifest!.Revision);
            Assert.Equal(["Fresh-7B-Q4_K_M.gguf"], manifest.Files.Select(f => f.Path));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task EscapeClosesTheBrowsePage()
    {
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            _ = shell.Models.StartAsync(allowNetwork: true);
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Ready, 20));
            Input.Click(window, Control<Button>(window, "BrowseAll"));
            Assert.True(shell.Models.Browse.IsOpen);
            Ui.Pump(0.3);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Ui.Pump(0.1);
            Assert.False(shell.Models.Browse.IsOpen);
            window.Close();
            return 0;
        });
    }
}
