using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Daemon;

namespace Wicked.Tests;

/// <summary>The window walked the way a user walks it: real clicks and keys, asserting what is on screen.</summary>
public sealed class ShellWindowTests
{
    private static TextBlock Text(Window window, string name) =>
        window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == name);

    private static string? PanelHeading(Window window) =>
        window.FindControl<Border>("SidePanel") is { IsEffectivelyVisible: true } panel
            ? panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("heading")).Text
            : null;

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task TheRailOpensPanelsAndItsLitButtonClosesTheSideBar()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, _) = Ui.Start(profile.Paths);
            Assert.Equal("EXPLORER", PanelHeading(window));

            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.Equal("MODELS", PanelHeading(window));
            Assert.Contains("active", window.FindControl<Button>("RailModels")!.Classes);

            // The lit button closes the side bar, and the editor takes its room (its width and the gap after it).
            var editor = window.FindControl<Border>("Editor")!;
            var before = editor.Bounds.Width;
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Ui.Pump(0.2);
            Assert.Null(PanelHeading(window));
            Assert.Equal(before + 266, editor.Bounds.Width, precision: 0);

            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task CtrlBAndCtrlLHideAndBringBackThePanelsAtTheirWidth()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, _) = Ui.Start(profile.Paths);
            var assistant = window.FindControl<Border>("Assistant")!;
            var width = assistant.Bounds.Width;
            Assert.True(width > 300, $"the assistant is {width} wide");

            Input.Chord(window, Key.L, RawInputModifiers.Control);
            Assert.False(assistant.IsEffectivelyVisible);
            Input.Chord(window, Key.L, RawInputModifiers.Control);
            Ui.Pump(0.2);
            Assert.True(assistant.IsEffectivelyVisible);
            Assert.Equal(width, assistant.Bounds.Width, precision: 0);

            Input.Chord(window, Key.B, RawInputModifiers.Control);
            Assert.Null(PanelHeading(window));
            Input.Chord(window, Key.B, RawInputModifiers.Control);
            Assert.Equal("EXPLORER", PanelHeading(window));

            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AFooterNoticeTakesTheUserToWhereItIsDealtWith()
    {
        using var profile = new TempProfile();
        var board = new NoticeBoard();
        await Ui.Run(() =>
        {
            var (window, _) = Ui.Start(profile.Paths, board);
            var button = window.FindControl<Button>("NoticeButton")!;
            Assert.False(button.IsEffectivelyVisible);

            // Posted from a worker, as the update checks will.
            Task.Run(() =>
            {
                board.Post(new Notice("update:a", "A newer upload of Qwen is out", NoticeKind.Action, ShellRoutes.Models));
                board.Post(new Notice("update:b", "A newer upload of Gemma is out", NoticeKind.Action, ShellRoutes.Models));
            }).Wait();
            Ui.Pump(0.2);

            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal("A newer upload of Gemma is out", Text(window, "NoticeText").Text);
            Assert.Equal("+1", Text(window, "MoreNoticesText").Text); // its own button now: it opens the list
            Assert.Equal("EXPLORER", PanelHeading(window));

            Input.Click(window, button);
            Assert.Equal("MODELS", PanelHeading(window));

            board.Clear("update:b");
            board.Clear("update:a");
            Ui.Pump(0.2);
            Assert.False(button.IsEffectivelyVisible);

            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheFooterShowsWhetherTheDaemonAnswers()
    {
        using var profile = new TempProfile();
        DaemonHost? daemon = null;
        try
        {
            await Ui.Run(() =>
            {
                var (window, _) = Ui.Start(profile.Paths);
                Assert.True(Ui.PumpUntil(() => Text(window, "DaemonText").Text == "Daemon not running", 5), Text(window, "DaemonText").Text);

                daemon = Task.Run(() => DaemonHost.StartAsync(profile.Paths, CancellationToken.None)).Result;
                Assert.True(Ui.PumpUntil(() => Text(window, "DaemonText").Text == "Daemon running", 5), Text(window, "DaemonText").Text);

                window.Close();
                return 0;
            });
        }
        finally
        {
            if (daemon is not null) await daemon.DisposeAsync();
        }
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheShellRendersAFrame()
    {
        using var profile = new TempProfile();
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT");
        await Ui.Run(() =>
        {
            var (window, _) = Ui.Start(profile.Paths);
            Ui.Pump(0.3);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.Equal(1440, frame.PixelSize.Width);
            if (!string.IsNullOrEmpty(shot)) frame.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();
            return 0;
        });
    }
}
