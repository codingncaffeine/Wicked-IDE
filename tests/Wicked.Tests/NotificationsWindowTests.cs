using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Wicked.Core;
using Wicked.Core.Models;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>The footer's "+N", the Notifications list and "What's new in your models", walked the way a person would.</summary>
public sealed class NotificationsWindowTests
{
    private static T Control<T>(Window window, string name) where T : Control
    {
        T? found = null;
        Ui.PumpUntil(() => (found = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name && c.IsEffectivelyVisible)) is not null, 3);
        return found ?? throw new InvalidOperationException($"No visible {typeof(T).Name} called {name}");
    }

    private static Button? ButtonLabelled(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Content as string == content && b.IsEffectivelyVisible);

    private static IEnumerable<string> Headings(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Classes.Contains("heading") && t.IsEffectivelyVisible && t.GetVisualAncestors().Contains(window.FindControl<Border>("NotificationsLayer")!)).Select(t => t.Text!);

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ThePlusNInTheFooterOpensEveryWaitingNoticeWorstFirstWithItsButtons()
    {
        using var profile = new TempProfile();
        var board = new NoticeBoard();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths, board);
            var opened = new List<string>();
            shell.Models.OpenPath = opened.Add;
            var layer = window.FindControl<Border>("NotificationsLayer")!;
            Assert.False(layer.IsVisible);
            Assert.False(window.FindControl<Button>("MoreNoticesButton")!.IsVisible); // nothing waiting

            board.Post(new Notice("model-update:test/x", "x: a newer upload is available", NoticeKind.Action, "panel/models", Actions: [new("update", "Update"), new("dismiss", "Dismiss")]));
            board.Post(new Notice("app-update", "Wicked 9.0.0 is available", NoticeKind.Action, "url/https://example.test/releases/v9", Actions: [new("notes", "Open release notes"), new("dismiss", "Dismiss")]));
            board.Post(new Notice("load-failed:y", "y could not be loaded", NoticeKind.Error, "panel/models"));
            board.Post(new Notice("saved", "Saved", NoticeKind.Info));
            Assert.True(Ui.PumpUntil(() => window.FindControl<Button>("MoreNoticesButton")!.IsVisible, 5));
            Assert.Equal("+3", Control<TextBlock>(window, "MoreNoticesText").Text);
            Assert.Equal("y could not be loaded", Control<TextBlock>(window, "NoticeText").Text); // the worst is the one shown

            Input.Click(window, window.FindControl<Button>("MoreNoticesButton")!);
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 5), "the list did not open");
            Assert.True(Ui.PumpUntil(() => Headings(window).Count() == 3, 5));
            Assert.Equal(["PROBLEMS", "UPDATES AND SUGGESTIONS", "RECENT"], Headings(window));
            Assert.Equal("Notifications (4)", Control<TextBlock>(window, "NotificationsTitle").Text);
            ModelsUi.Shot(window, "notifications-list");

            // The app notice's button opens the release notes; the other offers Dismiss, which clears it.
            Input.Click(window, ButtonLabelled(window, "Open release notes")!);
            Assert.True(Ui.PumpUntil(() => opened.Count == 1, 5));
            Assert.Equal("https://example.test/releases/v9", opened[0]);
            var dismiss = window.GetVisualDescendants().OfType<Button>().Where(b => b.Content as string == "Dismiss" && b.IsEffectivelyVisible).ToList();
            Assert.Equal(2, dismiss.Count);
            Input.Click(window, dismiss[1]); // the second Dismiss belongs to the older update notice, below the app's
            Assert.True(Ui.PumpUntil(() => board.All.Count < 4, 5));

            // A row's arrow goes where the notice is dealt with and closes the list.
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Go" && b.IsEffectivelyVisible));
            Assert.True(Ui.PumpUntil(() => !layer.IsVisible, 5));

            // Escape closes it too.
            shell.Notifications.IsOpen = true;
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 5));
            Input.Chord(window, Key.Escape, RawInputModifiers.None);
            Assert.False(layer.IsVisible);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task CheckNowFindsAnUploadAndTheListOffersToUpdateOrSilenceIt()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var start = shell.Models.StartAsync(allowNetwork: false);
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 20));
            var model = world.Catalog.Models.First(m => m.Repo == ModelWorld.Coder);
            Task.Run(() => shell.Models.Services.InstallModelAsync(model, model.Tiers.First(t => t.Quant == "Q8_0"), licenceAccepted: true)).GetAwaiter().GetResult();
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1, 10));
            world.Hub.Repo(ModelWorld.Coder).Upload("rev2", "Tiny-Coder-Q8_0.gguf", StandIns.Weights(1_700_000, 3), "Retrained on more code");

            shell.Notifications.IsOpen = true;
            var check = Control<Button>(window, "CheckNowButton");
            Input.Click(window, check);
            Assert.True(Ui.PumpUntil(() => shell.Notices.All.Any(n => n.Key == "model-update:" + ModelWorld.Coder), 15), Control<TextBlock>(window, "NotificationsStatus").Text);
            Assert.Equal("1 update available", Control<TextBlock>(window, "NotificationsStatus").Text);
            Assert.NotNull(ButtonLabelled(window, "Update"));
            Assert.NotNull(ButtonLabelled(window, "Don't tell me about this model"));
            ModelsUi.Shot(window, "notifications-update");

            // "What's new in your models": the commit it is at, the one on offer, the size change, the commit's title.
            Input.Click(window, Control<Button>(window, "WhatsNewButton"));
            var layer = window.FindControl<Border>("WhatsNewLayer")!;
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 5));
            Assert.Equal("Tiny-Coder-GGUF", Control<TextBlock>(window, "WhatsNewName").Text);
            Assert.EndsWith("→ rev2", Control<TextBlock>(window, "WhatsNewRevisions").Text, StringComparison.Ordinal);
            Assert.Contains("→", Control<TextBlock>(window, "WhatsNewSize").Text, StringComparison.Ordinal);
            Assert.Contains("Retrained on more code", window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text), StringComparer.Ordinal);
            ModelsUi.Shot(window, "whats-new");

            // Silencing the model pins it and clears the notice.
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Don't tell me about this model" && b.IsEffectivelyVisible && b.GetVisualAncestors().Contains(layer)));
            Assert.True(Ui.PumpUntil(() => shell.Models.Services.State.Installed.Single().Manifest!.Settings.GetValueOrDefault("update.policy") == "pin", 10));
            Assert.DoesNotContain(shell.Notices.All, n => n.Key == "model-update:" + ModelWorld.Coder);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task UpdateInTheWhatsNewViewReplacesTheFilesAfterTheNewOnesAnswer()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var start = shell.Models.StartAsync(allowNetwork: false);
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 20));
            var model = world.Catalog.Models.First(m => m.Repo == ModelWorld.Coder);
            var engine = Task.Run(async () =>
            {
                var e = await shell.Models.Services.EnsureEngineAsync();
                await shell.Models.Services.InstallModelAsync(model, model.Tiers.First(t => t.Quant == "Q8_0"), licenceAccepted: true);
                return e;
            }).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(engine.ServerPath)!, "fake.json"), JsonSerializer.Serialize(new { reply = "ok" }));
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1, 10));
            var fresh = StandIns.Weights(1_700_000, 3);
            world.Hub.Repo(ModelWorld.Coder).Upload("rev2", "Tiny-Coder-Q8_0.gguf", fresh, "Retrained");
            _ = shell.Models.CheckNowAsync();
            Assert.True(Ui.PumpUntil(() => shell.Models.Services.State.Updates?.Count == 1, 15));
            shell.WhatsNew.Open();

            Input.Click(window, Control<Button>(window, "WhatsNewUpdate"));

            var file = Path.Combine(profile.Paths.Data, "models", "test", "Tiny-Coder-GGUF", "Tiny-Coder-Q8_0.gguf");
            Assert.True(Ui.PumpUntil(() => shell.Models.Services.State.Updates is not { Count: > 0 } && File.Exists(file) && File.ReadAllBytes(file).Length == fresh.Length, 30), "the update did not finish");
            Assert.Equal(fresh, File.ReadAllBytes(file));
            Assert.False(File.Exists(file + ".previous"));
            Assert.True(Ui.PumpUntil(() => shell.WhatsNew.IsEmpty, 5));
            Assert.Equal("Everything you have installed is up to date.", Control<TextBlock>(window, "WhatsNewSummary").Text);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AModelsMenuSetsItsUpdatePolicyAndTheSettingsSetTheDefault()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
            Assert.True(Ui.PumpUntil(() => shell.Models.Ready, 20));
            var model = world.Catalog.Models.First(m => m.Repo == ModelWorld.Coder);
            Task.Run(() => shell.Models.Services.InstallModelAsync(model, model.Tiers.First(t => t.Quant == "Q8_0"), licenceAccepted: true)).GetAwaiter().GetResult();
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1, 10));
            var row = shell.Models.Installed.Single();
            Assert.Equal(UpdatePolicy.Notify, row.Policy);

            var more = window.GetVisualDescendants().OfType<Button>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "More" && b.IsEffectivelyVisible);
            Input.Click(window, more);
            var flyout = (MenuFlyout)more.Flyout!;
            Assert.True(Ui.PumpUntil(() => flyout.IsOpen, 5));
            var updates = flyout.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Updates");
            Assert.Equal(["Ask me first", "Update automatically", "Never (pin this version)"], updates.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()));
            updates.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "Update automatically").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.True(Ui.PumpUntil(() => row.Policy == UpdatePolicy.Auto, 10));
            Assert.Equal("auto", shell.Models.Services.Store.ReadManifestAsync(ModelWorld.Coder).GetAwaiter().GetResult()!.Settings["update.policy"]);
            flyout.Hide();

            // The default for models without a setting of their own, and the master switch, live in Settings → Models.
            shell.SettingsSheet.UpdatePolicy = shell.SettingsSheet.UpdatePolicies.First(p => p.Value == "pin");
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains("\"models.updatePolicy\": \"pin\"", StringComparison.Ordinal), 5));
            shell.SettingsSheet.CheckForUpdates = false;
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains("\"models.checkForUpdates\": false", StringComparison.Ordinal), 5));
            var text = File.ReadAllText(profile.Paths.SettingsFile);
            Assert.Contains("\"models.updatePolicy\": \"pin\"", text, StringComparison.Ordinal);
            Assert.Equal(UpdatePolicy.Auto, shell.Models.Services.PolicyOf(shell.Models.Services.State.Installed.Single())); // its own setting wins
            window.Close();
            return 0;
        });
    }
}
