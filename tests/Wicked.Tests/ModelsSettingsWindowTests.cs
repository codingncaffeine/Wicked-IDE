using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>Settings → Models and Settings → Runtime, walked through the sheet's own controls.</summary>
public sealed class ModelsSettingsWindowTests
{
    private static T Control<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void OpenSettings(Window window, ShellViewModel shell)
    {
        Input.Chord(window, Key.OemComma, RawInputModifiers.Control);
        Assert.True(shell.SettingsOpen);
    }

    /// <summary>Scrolls a control into view, as a person scrolling the sheet would, then clicks it.</summary>
    private static void Press(Window window, Control control)
    {
        control.BringIntoView();
        Ui.Pump(0.15);
        Input.Click(window, control);
    }

    private static void Start(ShellViewModel shell)
    {
        var start = shell.StartAsync();
        Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
    }

    private static bool SettingsFileHas(TempProfile profile, string text) =>
        File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains(text, StringComparison.Ordinal);

    [Fact]
    public async Task TheModelsSettingsChooseTheFolderTheTokenAndTheSpeedLimit()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        var elsewhere = Path.Combine(profile.Root, "big-drive");
        Directory.CreateDirectory(elsewhere);
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Start(shell);
            OpenSettings(window, shell);
            Input.Click(window, Control<Button>(window, "JumpModels"));
            var sheet = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.GetVisualDescendants().Any(d => d is Control { Name: "SectionModels" }));
            Assert.True(Ui.PumpUntil(() => sheet.Offset.Y > 0, 5), "the sheet did not scroll to Models");
            Assert.Equal("Install new models in", window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "Install new models in").Text);
            Assert.Contains("(default)", Control<TextBlock>(window, "StoreLocationText").Text, StringComparison.Ordinal);

            // The folder: the picker answers, new installs follow.
            shell.SettingsSheet.FolderPicker = () => Task.FromResult<string?>(elsewhere);
            Input.Click(window, Control<Button>(window, "ChooseStore"));
            Assert.True(Ui.PumpUntil(() => SettingsFileHas(profile, $"\"models.storeLocation\": \"{elsewhere}\""), 5), "the folder was not kept");
            Assert.Equal(elsewhere, shell.Models.Services.Store.Root);
            Assert.Equal(elsewhere, Control<TextBlock>(window, "StoreLocationText").Text);

            // The token: typed into a password box, kept in settings.json, in force for the Hub.
            var token = Control<TextBox>(window, "TokenBox");
            Input.Click(window, token);
            window.KeyTextInput("hf_secret123");
            Assert.True(Ui.PumpUntil(() => SettingsFileHas(profile, "\"huggingface.token\": \"hf_secret123\""), 5), "the token was not kept");
            Assert.Equal("hf_secret123", shell.Models.Services.Token);
            Assert.Equal('●', token.PasswordChar);
            Assert.False(token.RevealPassword);
            var reveal = window.GetVisualDescendants().OfType<ToggleButton>().First(t => Avalonia.Automation.AutomationProperties.GetName(t) == "Show token");
            Input.Click(window, reveal);
            Assert.True(token.RevealPassword);

            // The speed limit.
            var limit = Control<ComboBox>(window, "DownloadLimitBox");
            Assert.Equal("Unlimited", limit.SelectedItem?.ToString());
            limit.SelectedItem = shell.SettingsSheet.DownloadLimits.First(c => c.Value == "5");
            Assert.True(Ui.PumpUntil(() => SettingsFileHas(profile, "\"models.downloadLimitMBps\": 5"), 5));
            Assert.Equal(5 * 1_048_576, shell.Models.Services.Downloads.BandwidthLimit);
            Input.Click(window, reveal); // hidden again for the picture
            Assert.False(token.RevealPassword);
            limit.BringIntoView();
            ModelsUi.Shot(window, "settings-models");
            limit.SelectedItem = shell.SettingsSheet.DownloadLimits[0];
            Assert.True(Ui.PumpUntil(() => shell.Models.Services.Downloads.BandwidthLimit is null, 5));

            // A model installed now lands in the chosen folder, not the default one.
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Ui.Pump(0.1);
            Assert.False(shell.SettingsOpen);
            Input.Click(window, Control<Button>(window, "RailModels"));
            Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20), "no recommendations appeared");
            Input.Click(window, ModelsUi.Labelled(ModelsUi.Card(window, "Tiny Coder"), "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(ModelWorld.Coder).Busy, 30), "the install did not finish");
            Assert.True(Directory.GetFiles(Path.Combine(elsewhere, "test", "Tiny-Coder-GGUF"), "*.gguf").Length == 1);
            Assert.False(Directory.Exists(world.Store(profile.Paths)));
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheEngineUpdateNoticeLeadsToAnUpdateButtonThatInstallsIt()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Start(shell);
            var ensure = Task.Run(() => shell.Models.Services.EnsureEngineAsync());
            Assert.True(Ui.PumpUntil(() => ensure.IsCompleted, 30), "the engine did not install");
            Assert.Equal("b999-vulkan", shell.Models.Services.Engines.Current?.Id);

            // The daily check finds a newer build and says so in the footer.
            world.Builds = [999, 1000];
            var sweep = Task.Run(() => shell.Models.Services.Updates.CheckNowAsync());
            Assert.True(Ui.PumpUntil(() => sweep.IsCompleted && shell.Notices.All.Any(n => n.Key == "engine-update"), 30), "no engine notice");
            while (shell.Notice?.Key is { } key && key != "engine-update") shell.Notices.Clear(key);

            // The user clicks it: Settings opens on Runtime, and the update is right there to install, no Check needed.
            Assert.True(Ui.PumpUntil(() => shell.Notice?.Key == "engine-update", 5), $"the footer shows '{shell.Notice?.Text}'");
            Input.Click(window, Control<Button>(window, "NoticeButton"));
            Assert.True(Ui.PumpUntil(() => shell.SettingsOpen, 5), "the notice did not open Settings");
            Assert.True(Ui.PumpUntil(() => Control<Button>(window, "UpdateEngine").IsVisible, 10), $"no Update button: '{Control<TextBlock>(window, "RuntimeStatus").Text}'");
            Assert.Equal("Update to b1000-vulkan", Control<Button>(window, "UpdateEngine").Content);
            Press(window, Control<Button>(window, "UpdateEngine"));
            Assert.True(Ui.PumpUntil(() => shell.Models.Services.Engines.Current?.Id == "b1000-vulkan", 30), Control<TextBlock>(window, "RuntimeStatus").Text);
            Assert.True(Ui.PumpUntil(() => !Control<Button>(window, "UpdateEngine").IsVisible, 10), "the Update button stayed after the update");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheRuntimeSettingsShowTheEngineUpdateItAndRollBack()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            Start(shell);
            OpenSettings(window, shell);
            Input.Click(window, Control<Button>(window, "JumpRuntime"));
            Assert.Contains("No engine yet", Control<TextBlock>(window, "EngineText").Text, StringComparison.Ordinal);

            // Insist on the CPU build, then let the first install fetch it.
            var backend = Control<ComboBox>(window, "BackendBox");
            Assert.Equal("auto", ((SettingChoice)backend.SelectedItem!).Value);
            backend.SelectedItem = shell.SettingsSheet.Runtime.Backends.First(b => b.Value == "cpu");
            Assert.True(Ui.PumpUntil(() => SettingsFileHas(profile, "\"runtime.backend\": \"cpu\""), 5));
            var ensure = Task.Run(() => shell.Models.Services.EnsureEngineAsync());
            Assert.True(Ui.PumpUntil(() => ensure.IsCompleted, 30), "the engine did not install");
            Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "EngineText").Text!.Contains("b999-cpu", StringComparison.Ordinal), 10), Control<TextBlock>(window, "EngineText").Text);
            Assert.Contains("build 999", Control<TextBlock>(window, "EngineVersion").Text, StringComparison.Ordinal);
            var devices = Control<ItemsControl>(window, "DeviceList");
            Assert.True(Ui.PumpUntil(() => devices.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text!.Contains("Test GPU", StringComparison.Ordinal)), 5));
            Assert.Contains("8.0 GB", devices.GetVisualDescendants().OfType<TextBlock>().First().Text, StringComparison.Ordinal);

            // Nothing newer yet.
            Press(window, Control<Button>(window, "CheckEngine"));
            Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "RuntimeStatus").Text!.Contains("up to date", StringComparison.Ordinal), 10), Control<TextBlock>(window, "RuntimeStatus").Text);
            Assert.False(Control<Button>(window, "UpdateEngine").IsVisible);

            // A newer build appears: the check offers it, Update installs it and keeps the old one.
            world.Builds = [999, 1000];
            Press(window, Control<Button>(window, "CheckEngine"));
            Assert.True(Ui.PumpUntil(() => Control<Button>(window, "UpdateEngine").IsVisible, 10), Control<TextBlock>(window, "RuntimeStatus").Text);
            Assert.Equal("Update to b1000-cpu", Control<Button>(window, "UpdateEngine").Content);
            Press(window, Control<Button>(window, "UpdateEngine"));
            Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "EngineText").Text!.Contains("b1000-cpu", StringComparison.Ordinal), 30), Control<TextBlock>(window, "RuntimeStatus").Text);
            Assert.True(Ui.PumpUntil(() => Control<Button>(window, "RollbackEngine").IsVisible, 10));
            Assert.Equal("Roll back to b999-cpu", Control<Button>(window, "RollbackEngine").Content);
            Assert.Equal("b1000-cpu", shell.Models.Services.Engines.Current?.Id);
            ModelsUi.Shot(window, "settings-runtime");

            // Roll back.
            Press(window, Control<Button>(window, "RollbackEngine"));
            Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "EngineText").Text!.Contains("b999-cpu", StringComparison.Ordinal), 15));
            Assert.Equal("b999-cpu", shell.Models.Services.Engines.Current?.Id);
            window.Close();
            return 0;
        });
    }
}
