using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.Daemon;
using Wicked.Tests.Daemon;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>Settings → Server and the request log view, walked through the controls a person would use.</summary>
public sealed class ServerSettingsWindowTests
{
    private static T Control<T>(Window window, string name) where T : Control
    {
        T? found = null;
        Ui.PumpUntil(() => (found = window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)) is not null, 3);
        return found ?? throw new InvalidOperationException($"No {typeof(T).Name} called {name}");
    }

    private static void Press(Window window, Control control)
    {
        control.BringIntoView();
        Ui.Pump(0.15);
        Input.Click(window, control);
    }

    private static string Settings(TempProfile profile) => File.Exists(profile.Paths.SettingsFile) ? File.ReadAllText(profile.Paths.SettingsFile) : "";

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task TheServerPageStartsTheDaemonAndFollowsTheSettingsTheKeysAndTheRecipes()
    {
        using var profile = new TempProfile();
        var home = Path.Combine(profile.Root, "xdg");
        var commands = new List<string>();
        DaemonHost? daemon = null;
        try
        {
            // The daemon takes its port from settings.json, so the test's own free one is written there first.
            profile.Paths.EnsureCreated();
            await Wicked.Core.Settings.SettingsFile.WriteAsync(profile.Paths.SettingsFile, new Wicked.Core.Settings.WickedSettings { ServerPort = FreePort() }, CancellationToken.None);
            await Ui.Run(() =>
            {
                var (window, shell) = Ui.Start(profile.Paths);
                var copied = new List<string>();
                shell.Clipboard = text =>
                {
                    copied.Add(text);
                    return Task.CompletedTask;
                };
                var server = shell.SettingsSheet.Server;
                server.ConfigHome = home;
                server.DaemonPath = "/usr/bin/wickedd";
                server.SystemctlRunner = (args, _) =>
                {
                    commands.Add(string.Join(' ', args));
                    return Task.FromResult(0);
                };
                server.Launch = async ct =>
                {
                    daemon = await DaemonHost.StartAsync(profile.Paths, ct, new DaemonOptions { AllowNetwork = false, RunUpdates = false });
                    await daemon.Ready;
                    var started = daemon;
                    // What wickedd's own main does: when asked to stop, stop.
                    _ = started.WaitForShutdownAsync(CancellationToken.None).ContinueWith(_ => started.DisposeAsync().AsTask(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                    return true;
                };
                var start = shell.StartAsync();
                // The start includes a real daemon brought up in this process: on a loaded machine that took over 10 s.
                Assert.True(Ui.PumpUntil(() => start.IsCompleted, 30), "Wicked did not finish starting");

                Input.Chord(window, Key.OemComma, RawInputModifiers.Control);
                Assert.True(shell.SettingsOpen);
                Press(window, Control<Button>(window, "JumpServer"));
                Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "DaemonStateText").Text?.StartsWith("The daemon is not running", StringComparison.Ordinal) == true, 5));
                Assert.False(Control<Button>(window, "StopServerButton").IsEnabled);
                Assert.Equal(7, Control<ItemsControl>(window, "SnippetList").ItemCount);

                // Start it from the page: it comes up, serving on the port the settings name.
                Press(window, Control<Button>(window, "StartServerButton"));
                Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "DaemonStateText").Text?.StartsWith("The daemon is running", StringComparison.Ordinal) == true, 20), Control<TextBlock>(window, "DaemonStateText").Text);
                Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "GatewayStateText").Text?.StartsWith("Serving http://127.0.0.1:", StringComparison.Ordinal) == true, 10), Control<TextBlock>(window, "GatewayStateText").Text);
                ModelsUi.Shot(window, "settings-server");

                // The switch turns the API off and on without touching the daemon.
                Press(window, Control<ToggleSwitch>(window, "ServerEnabledToggle"));
                Assert.True(Ui.PumpUntil(() => Settings(profile).Contains("\"server.enabled\": false", StringComparison.Ordinal), 5));
                Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "GatewayStateText").Text == "The server is switched off.", 10), Control<TextBlock>(window, "GatewayStateText").Text);
                Assert.False(daemon!.Gateway.Running);
                Press(window, Control<ToggleSwitch>(window, "ServerEnabledToggle"));
                Assert.True(Ui.PumpUntil(() => daemon.Gateway.Running, 10));

                // The port: typed, kept in settings.json, and the daemon moves to it.
                var port = FreePort();
                Control<NumericUpDown>(window, "ServerPortBox").Value = port;
                Assert.True(Ui.PumpUntil(() => Settings(profile).Contains($"\"server.port\": {port}", StringComparison.Ordinal), 5));
                Assert.True(Ui.PumpUntil(() => daemon.Gateway.Port == port, 10));
                Assert.Contains($"http://127.0.0.1:{port}/v1", Control<ItemsControl>(window, "SnippetList").GetVisualDescendants().OfType<SelectableTextBlock>().Select(t => t.Text).First(t => t!.Contains("apiBase", StringComparison.Ordinal))!, StringComparison.Ordinal);

                // Reaching the network shows its warning; web pages allowed to call are a list, one per line.
                Assert.False(Control<Border>(window, "LanWarning").IsVisible);
                Press(window, Control<ToggleSwitch>(window, "ServerLanToggle"));
                Assert.True(Ui.PumpUntil(() => Control<Border>(window, "LanWarning").IsVisible, 5));
                Assert.True(Ui.PumpUntil(() => Settings(profile).Contains("\"server.listenOnLan\": true", StringComparison.Ordinal), 5));
                Control<TextBox>(window, "ServerCorsBox").Text = "http://localhost:3000\nhttp://localhost:5173";
                Assert.True(Ui.PumpUntil(() => Settings(profile).Contains("http://localhost:5173", StringComparison.Ordinal), 5));
                Assert.True(Ui.PumpUntil(() => daemon.Gateway.ListensOnLan, 10));
                Press(window, Control<ToggleSwitch>(window, "ServerLanToggle"));
                Assert.True(Ui.PumpUntil(() => !daemon.Gateway.ListensOnLan, 10));

                // A key: made once, shown once, copied, listed, then revoked; the daemon counts it.
                Input.Click(window, Control<TextBox>(window, "NewKeyNameBox"));
                window.KeyTextInput("Continue");
                Press(window, Control<Button>(window, "CreateKeyButton"));
                Assert.True(Ui.PumpUntil(() => Control<Border>(window, "NewKeyBox").IsVisible, 5));
                var key = Control<TextBox>(window, "NewKeyText").Text!;
                Assert.StartsWith("wk-", key, StringComparison.Ordinal);
                Assert.True(Ui.PumpUntil(() => Control<ItemsControl>(window, "KeyList").ItemCount == 1, 5));
                Press(window, Control<Button>(window, "CopyNewKeyButton"));
                Assert.True(Ui.PumpUntil(() => copied.Contains(key), 5));
                Assert.True(Ui.PumpUntil(() => daemon.Keys.ListAsync().GetAwaiter().GetResult().Count == 1, 5));
                Assert.Contains(Control<ItemsControl>(window, "SnippetList").GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text!.Contains(key, StringComparison.Ordinal)); // the recipes carry the new key
                Press(window, Control<Button>(window, "HideNewKeyButton"));
                Assert.False(Control<Border>(window, "NewKeyBox").IsVisible);
                Assert.DoesNotContain(Control<ItemsControl>(window, "SnippetList").GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text!.Contains(key, StringComparison.Ordinal));
                Assert.DoesNotContain(key, File.ReadAllText(profile.Paths.ApiKeysFile), StringComparison.Ordinal);

                // A recipe's Copy puts its text on the clipboard.
                var firstCopy = Control<ItemsControl>(window, "SnippetList").GetVisualDescendants().OfType<Button>().First(b => Avalonia.Automation.AutomationProperties.GetName(b) == "Copy");
                Press(window, firstCopy);
                Assert.True(Ui.PumpUntil(() => copied.Any(c => c.Contains("provider: openai", StringComparison.Ordinal)), 5));

                Press(window, Control<ItemsControl>(window, "KeyList").GetVisualDescendants().OfType<Button>().First(b => b.Content as string == "Revoke"));
                Assert.True(Ui.PumpUntil(() => Control<ItemsControl>(window, "KeyList").ItemCount == 0, 5));

                // Start at login writes the unit into the folder it was given, and nowhere else.
                var unitPath = Path.Combine(home, "systemd", "user", "wickedd.service");
                Assert.False(File.Exists(unitPath));
                Press(window, Control<ToggleSwitch>(window, "StartAtLoginToggle"));
                Assert.True(Ui.PumpUntil(() => File.Exists(unitPath), 5));
                Assert.Contains("ExecStart=\"/usr/bin/wickedd\"", File.ReadAllText(unitPath), StringComparison.Ordinal);
                Assert.Contains("Environment=\"WICKED_HOME=", File.ReadAllText(unitPath), StringComparison.Ordinal); // this profile is isolated, so the service is too
                Assert.Equal(["daemon-reload", "enable wickedd.service"], commands);
                Press(window, Control<ToggleSwitch>(window, "StartAtLoginToggle"));
                Assert.True(Ui.PumpUntil(() => !File.Exists(unitPath), 5));

                // Stop it from the page.
                Press(window, Control<Button>(window, "StopServerButton"));
                Assert.True(Ui.PumpUntil(() => Control<TextBlock>(window, "DaemonStateText").Text?.StartsWith("The daemon is not running", StringComparison.Ordinal) == true, 20), Control<TextBlock>(window, "DaemonStateText").Text);
                window.Close();
                return 0;
            });
        }
        finally
        {
            if (daemon is not null) await daemon.DisposeAsync();
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task TheRequestLogViewFollowsTheServerAndFiltersAndClears()
    {
        await using var rig = await GatewayRig.StartAsync();
        rig.Script(new { reply = "a b c" });
        await Ui.Run(() =>
        {
            var log = new ApiLogViewModel(rig.Profile.Paths);
            var window = new Window { Width = 1100, Height = 400, Content = new ApiLogView { DataContext = log } };
            window.Show();
            Ui.Pump(0.2);
            log.Start();
            Assert.True(Ui.PumpUntil(() => log.Status == "Following the server's requests", 10), log.Status);
            Assert.True(Control<TextBlock>(window, "ApiLogEmpty").IsVisible);

            _ = Task.Run(async () =>
            {
                await rig.PostAsync("/v1/chat/completions", new { model = GatewayRig.CoderCatalog, messages = new[] { new { role = "user", content = "hi" } } });
                await rig.PostAsync("/v1/chat/completions", new { model = "nope", messages = Array.Empty<object>() });
            });
            Assert.True(Ui.PumpUntil(() => log.Rows.Count(r => r.Request == "POST /v1/chat/completions") == 2, 20), $"{log.Rows.Count} rows");

            // Newest first: the refusal, then the answer, with what the person looks for in each.
            var rows = log.Rows.Where(r => r.Request == "POST /v1/chat/completions").ToList();
            Assert.Equal("404", rows[0].Status);
            Assert.True(rows[0].IsError);
            Assert.Equal("nope", rows[0].Model);
            Assert.Equal("200", rows[1].Status);
            Assert.Equal(GatewayRig.CoderRun, rows[1].Model);
            Assert.Equal("7 in · 3 out", rows[1].Tokens);
            Assert.False(Control<TextBlock>(window, "ApiLogEmpty").IsVisible);
            ModelsUi.Shot(window, "api-log");

            log.Filter = "nope";
            Assert.True(Ui.PumpUntil(() => log.Rows.Count == 1 && log.Rows[0].Model == "nope", 5));
            log.Filter = "";
            Assert.True(Ui.PumpUntil(() => log.Rows.Count >= 2, 5));
            Input.Click(window, Control<Button>(window, "ApiLogClear"));
            Assert.Empty(log.Rows);
            Assert.True(Control<TextBlock>(window, "ApiLogEmpty").IsVisible);

            log.Stop();
            Assert.Equal("Not following", log.Status);
            window.Close();
            return 0;
        });
    }

    [Fact(Timeout = 120_000)]
    public async Task TheRequestLogViewSaysWhenThereIsNoDaemon()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var log = new ApiLogViewModel(profile.Paths);

            log.Start();

            Assert.True(Ui.PumpUntil(() => log.Status.StartsWith("The daemon is not running", StringComparison.Ordinal), 10), log.Status);
            log.Dispose();
            return 0;
        });
    }
}
