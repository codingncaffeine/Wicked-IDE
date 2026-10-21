using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;

namespace Wicked.Tests;

/// <summary>The Models panel on a fresh profile, offline: this machine described, two or three picks with an Install each.</summary>
public sealed class ModelsPanelTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheModelsPanelRecommendsTwoOrThreeModelsForThisMachine()
    {
        using var profile = new TempProfile();
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_MODELS");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            _ = shell.Models.StartAsync(allowNetwork: false);
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Ready && shell.Models.Picks.Count > 0, 20), "no recommendations appeared");

            Assert.InRange(shell.Models.Picks.Count, 2, 3);
            Assert.DoesNotContain("Looking", shell.Models.HardwareText);
            var cards = window.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("pick") && b.IsEffectivelyVisible).ToList();
            Assert.Equal(shell.Models.Picks.Count, cards.Count);
            Assert.All(cards, card => Assert.Contains(card.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible && (b.Content as string) == "Install"));
            Assert.All(shell.Models.Picks, p => Assert.False(string.IsNullOrWhiteSpace(p.Reason)));

            Ui.Pump(0.3);
            if (!string.IsNullOrEmpty(shot))
            {
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            window.Close();
            return 0;
        });
    }
}
