using Avalonia.Controls;
using Wicked.Core;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>Pause, resume and cancel from the card in the Models panel, with the footer following along.</summary>
public sealed class ModelsDownloadsWindowTests
{
    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task ADownloadCanBePausedResumedAndCancelledFromItsCard()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            _ = shell.Models.StartAsync(allowNetwork: true);
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20), "no recommendations appeared");

            world.Hub.ChunkDelay = TimeSpan.FromMilliseconds(40);
            var card = ModelsUi.Card(window, "Tiny Chat");
            var item = ((App.ViewModels.PickCard)card.DataContext!).Item;
            Input.Click(window, ModelsUi.Labelled(card, "Install")!);

            // Downloading: a pause and a cancel button replace Install, and the footer shows the progress.
            Assert.True(Ui.PumpUntil(() => item.Busy && item.Progress > 0.05 && ModelsUi.Named(card, "Pause") is not null, 20), "the download did not start");
            Assert.Null(ModelsUi.Labelled(card, "Install"));
            Assert.NotNull(ModelsUi.Named(card, "Cancel"));
            Assert.Equal(NoticeKind.Progress, shell.Notice?.Kind);
            Assert.Contains("Tiny Chat", shell.Notice!.Text, StringComparison.Ordinal);

            // Pause: the bar stops, Resume appears, the footer says so.
            Input.Click(window, ModelsUi.Named(card, "Pause")!);
            Assert.True(Ui.PumpUntil(() => item.Paused && ModelsUi.Named(card, "Resume") is not null, 10), "the pause did not take");
            Assert.Null(ModelsUi.Named(card, "Pause"));
            Assert.True(Ui.PumpUntil(() => shell.Notice?.Text.StartsWith("Paused", StringComparison.Ordinal) == true, 5), shell.Notice?.Text);
            var frozen = item.Progress;
            Ui.Pump(0.6);
            Assert.Equal(frozen, item.Progress);
            ModelsUi.Shot(window, "models-download-paused");

            // Resume carries on; Cancel then removes everything and the card offers Install again.
            Input.Click(window, ModelsUi.Named(card, "Resume")!);
            Assert.True(Ui.PumpUntil(() => !item.Paused && item.Progress > frozen, 10), "it did not carry on");
            Input.Click(window, ModelsUi.Named(card, "Cancel")!);
            Assert.True(Ui.PumpUntil(() => !item.Busy && ModelsUi.Labelled(card, "Install") is not null, 15), "the card did not return to Install");
            Assert.True(Ui.PumpUntil(() => !Directory.Exists(world.Store(profile.Paths)) || Directory.GetFiles(world.Store(profile.Paths), "*", SearchOption.AllDirectories).Length == 0, 5),
                "files were left behind");
            Assert.Null(shell.Notice is { Kind: NoticeKind.Progress } ? shell.Notice : null);
            Assert.Empty(shell.Models.Installed);

            // Run to the end this time.
            world.Hub.ChunkDelay = TimeSpan.Zero;
            Input.Click(window, ModelsUi.Labelled(card, "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !item.Busy, 30), "the install did not finish");
            Assert.True(Ui.PumpUntil(() => ModelsUi.Card(window, "Tiny Chat").GetVisualDescendantsText().Contains("Installed"), 5));
            window.Close();
            return 0;
        });
    }
}
