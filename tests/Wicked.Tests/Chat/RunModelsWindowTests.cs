using Avalonia.Controls;
using Wicked.App.Chat;
using Wicked.App.ViewModels;
using Wicked.Core.Chat;
using Wicked.Tests.Models;

namespace Wicked.Tests.Chat;

/// <summary>Running models from the window: load, unload, the state they are in, their run settings, and the footer's word on it.</summary>
public sealed class RunModelsWindowTests
{
    private const string Coder = ModelWorld.Coder;

    private static string TextIn(Window window, string name) => ChatUi.Control<TextBlock>(window, name).Text ?? "";

    [Fact]
    public async Task AModelInstalledFromThePanelLoadsThroughTheRealRuntime()
    {
        // No fakes between the panel and the runtime: the runtime names a model by its variant ("repo:Q4_K_M"), the panel's
        // row by the stored model, and Load must reach the right one.
        Assert.SkipUnless(StandIns.CanRunFakeRouter, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var models = await ModelWorld.StartAsync();
        ShellViewModel? started = null;
        try
        {
            await Ui.Run(() =>
            {
                var (window, shell) = models.Start(profile.Paths);
                started = shell;
                _ = shell.Models.StartAsync(allowNetwork: true);
                Input.Click(window, window.FindControl<Button>("RailModels")!);
                Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20), "no recommendations appeared");
                Input.Click(window, ModelsUi.Labelled(ModelsUi.Card(window, "Tiny Coder"), "Install")!);
                Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(Coder).Busy, 30), "the install did not finish");

                Assert.True(Ui.PumpUntil(() => ModelsUi.Named(window, "Load") is not null, 15), "the runtime's listing never reached the row");
                Assert.Equal("Not loaded", TextIn(window, "RunStateText"));
                Input.Click(window, ModelsUi.Named(window, "Load")!);
                Assert.True(Ui.PumpUntil(() => ModelsUi.Named(window, "Unload") is not null, 30), $"the model did not load: {TextIn(window, "RunStateText")}");
                Assert.Equal("Loaded", TextIn(window, "RunStateText"));
                Assert.StartsWith(Coder, shell.Models.Installed[0].RunId, StringComparison.Ordinal);

                Input.Click(window, ModelsUi.Named(window, "Unload")!);
                Assert.True(Ui.PumpUntil(() => ModelsUi.Named(window, "Load") is not null, 30), "the model did not unload");
                window.Close();
                return 0;
            });
        }
        finally
        {
            // The window leaves its services to the app's exit; here the router they started must go with the test.
            if (started is not null) await started.Models.Services.DisposeAsync();
        }
    }

    [Fact]
    public async Task AnInstalledModelLoadsAndUnloadsWithItsStateAndTheFooterFollows()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var models = await ModelWorld.StartAsync();
        var chat = new ChatWorld();
        await Ui.Run(() =>
        {
            AssistantServices.Factory = _ => chat.Services;
            ShellViewModel shell;
            Window window;
            try
            {
                (window, shell) = models.Start(profile.Paths);
            }
            finally
            {
                AssistantServices.Factory = null;
            }

            _ = shell.Models.StartAsync(allowNetwork: true);
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20), "no recommendations appeared");
            Input.Click(window, ModelsUi.Labelled(ModelsUi.Card(window, "Tiny Coder"), "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(Coder).Busy, 30), "the install did not finish");
            Assert.Null(ModelsUi.Named(window, "Load")); // the runtime does not list it yet: nothing to load it with
            Assert.False(shell.Models.HasRuntimeStatus);

            // The runtime lists it: the row says it is not loaded and offers Load.
            chat.Runtime.Set(FakeRuntime.Model(Coder, "Tiny Coder", RunState.Unloaded));
            Assert.True(Ui.PumpUntil(() => ModelsUi.Named(window, "Load") is not null, 5), "no Load button");
            Assert.Equal("Not loaded", TextIn(window, "RunStateText"));
            Assert.Equal("No model loaded", TextIn(window, "ModelStatusText"));
            Assert.Null(ModelsUi.Named(window, "Unload"));
            ModelsUi.Shot(window, "models-run-unloaded");

            // Load: Loading with its progress, then Loaded, and the footer names the model.
            Input.Click(window, ModelsUi.Named(window, "Load")!);
            Assert.True(Ui.PumpUntil(() => TextIn(window, "RunStateText") == "Loading 40%", 5), "no progress shown");
            Assert.Equal("Loading Tiny Coder 40%", TextIn(window, "ModelStatusText"));
            ModelsUi.Shot(window, "models-run-loading");
            Assert.True(Ui.PumpUntil(() => ModelsUi.Named(window, "Unload") is not null, 5), "the model did not load");
            Assert.Equal("Loaded", TextIn(window, "RunStateText"));
            Assert.Equal("Tiny Coder", TextIn(window, "ModelStatusText"));
            Assert.Null(ModelsUi.Named(window, "Load"));
            ModelsUi.Shot(window, "models-run-loaded");
            Assert.Equal([Coder], chat.Runtime.Loads);

            // Unload.
            Input.Click(window, ModelsUi.Named(window, "Unload")!);
            Assert.True(Ui.PumpUntil(() => ModelsUi.Named(window, "Load") is not null, 5));
            Assert.Equal([Coder], chat.Runtime.Unloads);
            Assert.Equal("Not loaded", TextIn(window, "RunStateText"));

            // A load that fails shows why, and the footer says so; Load is offered again.
            chat.Runtime.FailLoadsWith = "not enough memory for the context";
            Input.Click(window, ModelsUi.Named(window, "Load")!);
            Assert.True(Ui.PumpUntil(() => TextIn(window, "RunStateText").StartsWith("Failed", StringComparison.Ordinal), 5), "no failure shown");
            Assert.Equal("Failed: not enough memory for the context", TextIn(window, "RunStateText"));
            Assert.Equal("Tiny Coder failed to load", TextIn(window, "ModelStatusText"));
            Assert.NotNull(ModelsUi.Named(window, "Load"));
            ModelsUi.Shot(window, "models-run-failed");

            // The footer's item goes to the Models panel.
            shell.Navigate(Wicked.App.Shell.ShellRoutes.Explorer);
            Ui.Pump(0.2);
            Assert.False(shell.IsModels);
            Input.Click(window, window.FindControl<Button>("ModelStatus")!);
            Assert.True(shell.IsModels);

            // The chat's model chip shows the same state.
            Assert.Equal("Tiny Coder", shell.Chat.SelectedModel!.Name);
            Assert.Equal(RunState.Failed, shell.Chat.SelectedModel.State);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task RunSettingsStartAtAutomaticAreEditedSavedAndComeBack()
    {
        Assert.SkipUnless(StandIns.CanRunEngine, "the stand-in engine is a shell script");
        using var profile = new TempProfile();
        await using var models = await ModelWorld.StartAsync();
        var chat = new ChatWorld();
        await Ui.Run(() =>
        {
            AssistantServices.Factory = _ => chat.Services;
            ShellViewModel shell;
            Window window;
            try
            {
                (window, shell) = models.Start(profile.Paths);
            }
            finally
            {
                AssistantServices.Factory = null;
            }

            _ = shell.Models.StartAsync(allowNetwork: true);
            Input.Click(window, window.FindControl<Button>("RailModels")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Picks.Count == 3 && ModelsUi.Cards(window).Count() == 3, 20));
            Input.Click(window, ModelsUi.Labelled(ModelsUi.Card(window, "Tiny Coder"), "Install")!);
            Assert.True(Ui.PumpUntil(() => shell.Models.Installed.Count == 1 && !shell.Models.Item(Coder).Busy, 30));
            chat.Runtime.Set(FakeRuntime.Model(Coder, "Tiny Coder", RunState.Unloaded));
            Assert.True(Ui.PumpUntil(() => ModelsUi.Named(window, "Run settings") is not null, 5));

            // The sheet opens with every choice at Automatic.
            Input.Click(window, ModelsUi.Named(window, "Run settings")!);
            var layer = window.FindControl<Border>("RunSettingsLayer")!;
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 5), "the sheet did not open");
            Assert.Equal("Run settings: Tiny-Coder-GGUF", TextIn(window, "RunSettingsTitle"));
            var sheet = shell.Models.RunSettings!;
            Assert.All(new[] { sheet.Context, sheet.GpuLayers, sheet.KvCache, sheet.FlashAttention, sheet.DraftModel, sheet.Reasoning }, c => Assert.Equal("Automatic", c.Label));
            Assert.Null(sheet.Temperature);
            Assert.Equal(new ModelRunSettings(), sheet.ToSettings());
            ModelsUi.Shot(window, "models-run-settings");

            // Edit through the controls and save.
            ChatUi.Control<ComboBox>(window, "ContextBox").SelectedItem = sheet.Contexts.Single(c => c.Value == "16384");
            ChatUi.Control<ComboBox>(window, "GpuLayersBox").SelectedItem = sheet.GpuLayerChoices.Single(c => c.Value == "all");
            ChatUi.Control<ComboBox>(window, "KvCacheBox").SelectedItem = sheet.KvCacheChoices.Single(c => c.Value == "q8_0");
            ChatUi.Control<ComboBox>(window, "FlashBox").SelectedItem = sheet.OnOff.Single(c => c.Value == "on");
            ChatUi.Control<ComboBox>(window, "ReasoningBox").SelectedItem = sheet.OnOff.Single(c => c.Value == "off");
            ChatUi.Control<NumericUpDown>(window, "TemperatureBox").Value = 0.3m;
            ChatUi.Control<NumericUpDown>(window, "MaxTokensBox").Value = 2048;
            Ui.Pump(0.2);
            Input.Click(window, ChatUi.Control<Button>(window, "RunSettingsSave"));
            Assert.True(Ui.PumpUntil(() => !layer.IsVisible && chat.Runtime.Saved.Count == 1, 5), "the settings were not saved");
            var (id, saved) = chat.Runtime.Saved[0];
            Assert.Equal(Coder, id);
            Assert.Equal(new ModelRunSettings(16384, "all", "q8_0", true, null, false, new ChatSampling(0.3, null, 2048)), saved);

            // Opened again, they are as saved; All Automatic puts every choice back.
            Input.Click(window, ModelsUi.Named(window, "Run settings")!);
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 5));
            Assert.Equal("16384", shell.Models.RunSettings!.Context.Value);
            Assert.Equal(0.3m, ChatUi.Control<NumericUpDown>(window, "TemperatureBox").Value);
            Input.Click(window, ChatUi.Control<Button>(window, "RunSettingsReset"));
            Assert.Equal(new ModelRunSettings(), shell.Models.RunSettings.ToSettings());
            Input.Click(window, ChatUi.Control<Button>(window, "RunSettingsCancel"));
            Assert.False(layer.IsVisible);
            Assert.Single(chat.Runtime.Saved); // cancelled: nothing more saved
            window.Close();
            return 0;
        });
    }
}
