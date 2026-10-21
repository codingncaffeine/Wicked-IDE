using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Wicked.App.Shell;
using Wicked.App.Themes;
using Wicked.App.Views;

namespace Wicked.Tests;

/// <summary>Phase 26: what every desktop expects of an app, whatever the IDE does.</summary>
public sealed class PlatformBasicsTests
{
    private sealed class FakeScheme : ISystemColorScheme
    {
        public bool? PrefersDark { get; set; }

        public event Action? Changed;

        public void Raise() => Changed?.Invoke();
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheWindowFollowsTheDesktopsLightOrDarkWhenAskedTo()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
            var scheme = new FakeScheme { PrefersDark = false };
            shell.SystemScheme = scheme;
            Assert.Equal("Wicked Dark", shell.Themes.Current?.Name); // not following: the settings' own theme

            var on = shell.UpdateSettingsAsync(s => s with { AutoDetectColorScheme = true });
            Assert.True(Ui.PumpUntil(() => on.IsCompleted && shell.Themes.Current?.Name == "Wicked Light", 5), shell.Themes.Current?.Name);

            // The desktop turns dark: the window follows at once.
            scheme.PrefersDark = true;
            scheme.Raise();
            Assert.True(Ui.PumpUntil(() => shell.Themes.Current?.Name == "Wicked Dark", 5), shell.Themes.Current?.Name);

            // A theme picked while following becomes the preferred one for the desktop's choice; the settings' own is kept.
            var pick = shell.RunCommandAsync("workbench.action.selectTheme");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Any(i => i.Label == "Ember"), 5));
            var chosen = shell.QuickInput.Items.First(i => i.Label == "Ember").Choose();
            Assert.True(Ui.PumpUntil(() => chosen.IsCompleted && shell.Settings.PreferredDarkColorTheme == "Ember" && shell.Themes.Current?.Name == "Ember", 5), shell.Themes.Current?.Name);
            Assert.Equal("Wicked Dark", shell.Settings.ColorTheme);

            // Off again: the settings' own theme, whatever the desktop says.
            var off = shell.UpdateSettingsAsync(s => s with { AutoDetectColorScheme = false });
            Assert.True(Ui.PumpUntil(() => off.IsCompleted && shell.Themes.Current?.Name == "Wicked Dark", 5), shell.Themes.Current?.Name);
            window.Close();
            return 0;
        });
    }

    private static List<string> Unnamed(Avalonia.Visual root) =>
        [.. AccessibleNames.Unnamed(root).Select(b => $"{b.GetType().Name} {b.Name ?? "(no name)"} in {b.FindAncestorOfType<UserControl>()?.GetType().Name}")];

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheNewPartsAreNamedForScreenReadersAndTheBarsNamesAreToldApart()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "text\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            WorkbenchWindowTests.OpenByClicks(window, shell, "a.txt");

            // The title bar and the status bar: every button named, and no two alike (a screen reader says only the name).
            foreach (var bar in new[] { "TitleBar", "StatusBar" })
            {
                var part = window.FindControl<Control>(bar)!;
                Assert.Empty(Unnamed(part));
                var names = part.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).Select(b => AutomationProperties.GetName(b)).Where(n => !string.IsNullOrEmpty(n)).ToList();
                Assert.Equal(names.Distinct().Count(), names.Count);
            }

            // The menus: every item says what it is.
            var menus = window.FindControl<Menu>("MenuBar")!.Items.OfType<MenuItem>().ToList();
            Assert.NotEmpty(menus);
            foreach (var menu in menus)
            {
                menu.Open();
                Ui.Pump(0.1);
                var items = menu.Items.OfType<MenuItem>().ToList();
                Assert.All(items, i => Assert.False(string.IsNullOrWhiteSpace(i.Header as string ?? AutomationProperties.GetName(i)), $"an item of {menu.Header} has no words"));
                menu.Close();
            }

            // The Settings list and the Keyboard Shortcuts editor.
            shell.SettingsOpen = true;
            Ui.Pump(0.2);
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "JumpAll"));
            Ui.Pump(0.3);
            Assert.Empty(Unnamed(window.GetVisualDescendants().OfType<SettingsView>().Single()));
            shell.SettingsOpen = false;
            var opened = shell.RunCommandAsync("workbench.action.openGlobalKeybindings");
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && window.GetVisualDescendants().OfType<ShortcutsView>().Any(v => v.IsEffectivelyVisible), 5));
            Ui.Pump(0.3);
            Assert.Empty(Unnamed(window.GetVisualDescendants().OfType<ShortcutsView>().Single()));
            window.Close();
            return 0;
        });
    }
}
