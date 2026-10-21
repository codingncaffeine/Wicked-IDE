using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>The licence step before a download, and the installed list's menu, delete and update flags.</summary>
public sealed class ModelsInstalledWindowTests
{
    private static T Control<T>(Window window, string name) where T : Control
    {
        T? found = null;
        Ui.PumpUntil(() => (found = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)) is not null, 3);
        return found ?? throw new InvalidOperationException($"No visible {typeof(T).Name} called {name}");
    }

    private static void OpenModels(Window window, ShellViewModel shell)
    {
        _ = shell.Models.StartAsync(allowNetwork: true);
        Input.Click(window, window.FindControl<Button>("RailModels")!);
        Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20), "no recommendations appeared");
    }

    private static string StoreFiles(WickedPaths paths) =>
        Directory.Exists(Path.Combine(paths.Data, "models")) ? string.Join(",", Directory.GetFiles(Path.Combine(paths.Data, "models"), "*", SearchOption.AllDirectories)) : "";

    [Fact]
    public async Task AModelUnderACommunityLicenceAsksBeforeItDownloads()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var opened = new List<string>();
            shell.Models.OpenPath = opened.Add;
            OpenModels(window, shell);
            var card = ModelsUi.Card(window, "Tiny Quick"); // under the gemma licence
            var prompt = window.FindControl<Border>("PromptLayer")!;
            Assert.False(prompt.IsVisible);

            // Install asks first, and nothing has been fetched.
            Input.Click(window, ModelsUi.Labelled(card, "Install")!);
            Assert.True(Ui.PumpUntil(() => prompt.IsVisible, 10), "the licence was not asked about");
            Assert.Equal("Accept the gemma licence?", Control<TextBlock>(window, "PromptTitle").Text);
            Assert.Contains("not one of the permissive licences", Control<TextBlock>(window, "PromptBody").Text, StringComparison.Ordinal);
            Assert.DoesNotContain(world.Hub.Requests, r => r.Contains("/resolve/", StringComparison.Ordinal));
            Assert.Equal("", StoreFiles(profile.Paths));
            ModelsUi.Shot(window, "licence-prompt");

            // The link goes to the licence text; No installs nothing.
            Input.Click(window, Control<Button>(window, "PromptLink"));
            Assert.True(Ui.PumpUntil(() => opened.Count == 1, 5));
            Assert.Equal("https://example.test/gemma-terms", opened[0]);
            Input.Click(window, Control<Button>(window, "PromptCancel"));
            Assert.False(prompt.IsVisible);
            Ui.Pump(0.3);
            Assert.Empty(shell.Models.Installed);
            Assert.NotNull(ModelsUi.Labelled(card, "Install"));

            // Accept and install: the download runs and the manifest records the acceptance.
            Input.Click(window, ModelsUi.Labelled(card, "Install")!);
            Assert.True(Ui.PumpUntil(() => prompt.IsVisible, 10));
            Input.Click(window, Control<Button>(window, "PromptConfirm"));
            Assert.False(prompt.IsVisible);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(ModelWorld.Quick).Busy, 30), "the install did not finish");
            var manifest = shell.Models.Services.Store.ReadManifestAsync(ModelWorld.Quick).GetAwaiter().GetResult();
            Assert.NotNull(manifest!.AcceptedAt);
            Assert.Equal("gemma", manifest.LicenseId);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task AnInstalledModelHasAMenuADeleteThatAsksAndAnUpdateFlag()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var opened = new List<string>();
            shell.Models.OpenPath = opened.Add;
            OpenModels(window, shell);

            Input.Click(window, ModelsUi.Labelled(ModelsUi.Card(window, "Tiny Coder"), "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(ModelWorld.Coder).Busy, 30), "the install did not finish");
            var row = shell.Models.Installed.Single();
            Assert.Equal("Tiny-Coder-GGUF", row.Name);
            Assert.Contains("Q8_0", row.Details, StringComparison.Ordinal);
            Assert.Contains("on disk", row.Details, StringComparison.Ordinal);

            // The menu: Show in folder hands the model's folder to the desktop.
            var more = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "More" && b.IsEffectivelyVisible);
            Input.Click(window, more);
            var flyout = (MenuFlyout)more.Flyout!;
            Assert.True(Ui.PumpUntil(() => flyout.IsOpen, 5), "the menu did not open");
            Assert.Equal(["Show in folder", "Delete…"], flyout.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
            ModelsUi.Shot(window, "models-installed-menu");
            flyout.Items.OfType<MenuItem>().First().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Ui.PumpUntil(() => opened.Count == 1, 5));
            Assert.Equal(Path.Combine(profile.Paths.Data, "models", "test", "Tiny-Coder-GGUF"), opened[0]);
            flyout.Hide();

            // Update flag: the repo has a newer commit.
            world.Hub.Repo(ModelWorld.Coder).Upload("rev2", "Tiny-Coder-Q8_0.gguf", StandIns.Weights(1_600_000, 3), "Retrained");
            Input.Click(window, Control<Button>(window, "CheckUpdates"));
            Assert.True(Ui.PumpUntil(() => row.HasUpdate, 10), shell.Models.UpdateStatus);
            Assert.Equal("1 update available", shell.Models.UpdateStatus);
            Assert.Contains("newer upload", row.UpdateText, StringComparison.Ordinal);
            Assert.Equal(NoticeKind.Action, shell.Notice?.Kind);
            Ui.Pump(0.2);
            ModelsUi.Shot(window, "models-installed-update");
            world.Hub.Repo(ModelWorld.Coder).Sha = "rev1";
            Input.Click(window, Control<Button>(window, "CheckUpdates"));
            Assert.True(Ui.PumpUntil(() => !row.HasUpdate && shell.Models.UpdateStatus.StartsWith("Everything is up to date", StringComparison.Ordinal), 10), shell.Models.UpdateStatus);

            // Delete asks first; saying no keeps it, saying yes removes every file and the card offers Install again.
            Input.Click(window, more);
            Assert.True(Ui.PumpUntil(() => flyout.IsOpen, 5));
            flyout.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var prompt = window.FindControl<Border>("PromptLayer")!;
            Assert.True(Ui.PumpUntil(() => prompt.IsVisible, 5));
            Assert.Equal("Delete Tiny-Coder-GGUF?", Control<TextBlock>(window, "PromptTitle").Text);
            ModelsUi.Shot(window, "models-delete-prompt");
            Input.Click(window, Control<Button>(window, "PromptCancel"));
            Ui.Pump(0.3);
            Assert.Single(shell.Models.Installed);
            Assert.NotEqual("", StoreFiles(profile.Paths));

            flyout.Items.OfType<MenuItem>().Last().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Ui.PumpUntil(() => prompt.IsVisible, 5));
            Input.Click(window, Control<Button>(window, "PromptConfirm"));
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 0, 10), "the model was not deleted");
            Assert.Equal("", StoreFiles(profile.Paths));
            Assert.True(Ui.PumpUntil(() => ModelsUi.Labelled(ModelsUi.Card(window, "Tiny Coder"), "Install") is not null, 5));
            window.Close();
            return 0;
        });
    }
}
