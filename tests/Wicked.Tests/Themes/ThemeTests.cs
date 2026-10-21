using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Wicked.App.Editor;
using Wicked.App.Themes;
using Wicked.Core.Themes;

namespace Wicked.Tests.Themes;

/// <summary>The window themes: readable (WCAG contrast), VS Code's files understood, and switched live in the window.</summary>
public sealed class ThemeTests
{
    private static readonly string[] Names = ["Wicked Dark", "Wicked Light", "Ember", "Neon", "Paper", "High Contrast"];

    [Fact]
    public async Task EveryBuiltInThemeIsReadable()
    {
        using var profile = new TempProfile();
        var failures = await Ui.Run(() =>
        {
            var service = new ThemeService(profile.Paths);
            Assert.Equal(Names, service.Builtins.Select(t => t.Name));
            var problems = new List<string>();
            foreach (var theme in service.Builtins)
            {
                var t = service.TokensOf(theme);
                var hc = theme.Kind == ThemeKind.HighContrast;
                void Need(string fg, string bg, double min)
                {
                    var ratio = WickedTheme.Contrast(t[fg], t[bg]);
                    if (ratio < min) problems.Add($"{theme.Name}: {fg} on {bg} is {ratio:0.00}, needs {min}");
                }

                foreach (var surface in new[] { "Brush.Surface", "Brush.Editor", "Brush.Raised", "Brush.Ground" })
                {
                    Need("Brush.Text", surface, hc ? 7 : 4.5);
                    Need("Brush.Text.Secondary", surface, hc ? 7 : 4.5);
                    Need("Brush.Text.Muted", surface, 3);
                }

                Need("Brush.Accent", "Brush.Surface", 3);
                Need("Brush.Text.OnAccent", "Brush.Accent", 4.5);
                Need("Brush.Danger", "Brush.Surface", 3);
                Need("Brush.Warning", "Brush.Surface", 3);
                Need("Brush.Success", "Brush.Surface", 3);
                if (WickedTheme.Contrast(theme.Colors["editor.foreground"], theme.Colors["editor.background"]) < (hc ? 7 : 4.5)) problems.Add($"{theme.Name}: the editor's text");
            }

            return problems;
        });
        Assert.Empty(failures);
    }

    [Fact]
    public void AVsCodeThemeReadsAndWhatItLeavesOutIsWorkedOut()
    {
        var theme = WickedTheme.Parse("""
            {
              // a VS Code theme: comments allowed
              "name": "Sample", "type": "light",
              "colors": { "editor.background": "#FAFAFA", "sideBar.background": "#F0F0F0", "foreground": "#222", "button.background": "#0066CCFF", "list.focusBackground": "#0066CC33" },
              "tokenColors": [ { "scope": "keyword", "settings": { "foreground": "#AA00AA" } } ],
            }
            """);
        Assert.Equal(ThemeKind.Light, theme.Kind);
        Assert.Equal(0xFF222222u, theme.Colors["foreground"]);
        Assert.Equal(0x330066CCu, theme.Colors["list.focusBackground"]);
        Assert.NotNull(theme.TokenColorsJson);
        var tokens = theme.Tokens(new Dictionary<string, uint>());
        Assert.Equal(0xFFFAFAFAu, tokens["Brush.Editor"]);
        Assert.Equal(0xFFF0F0F0u, tokens["Brush.Surface"]);
        Assert.Equal(0xFF0066CCu, tokens["Brush.Accent"]);
        Assert.Equal(0x330066CCu, tokens["Brush.Accent.Subtle"]);                 // translucent where it is meant to be
        Assert.True(WickedTheme.Contrast(tokens["Brush.Hover"], tokens["Brush.Surface"]) > 1.02);   // worked out: a step toward the ink
        Assert.Equal(0xFFFFFFFFu, tokens["Brush.Text.OnAccent"]);
        Assert.Null(WickedTheme.ParseColor("red"));
        Assert.Equal(0x80112233u, WickedTheme.ParseColor("#11223380"));
    }

    [Fact]
    public async Task CtrlKCtrlTSwitchesTheWholeWindowAndEscapeTakesItBack()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            try
            {
                var start = shell.StartAsync();
                Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
                uint Surface() => Application.Current!.TryFindResource("Brush.Surface", out var b) && b is ISolidColorBrush s ? s.Color.ToUInt32() : 0;
                Assert.Equal(0xFF15171Bu, Surface());

                shell.Commands.First(c => c.Id == "workbench.action.selectTheme").Run();
                Assert.True(shell.QuickInput.IsOpen);
                var light = shell.QuickInput.Items.ToList().FindIndex(i => i.Label == "Wicked Light");
                shell.QuickInput.Selected = light;                                      // the arrows preview it
                Assert.True(Ui.PumpUntil(() => Surface() == 0xFFF5F6F8u, 5), "no preview");
                Assert.Equal(ThemeVariant.Light, Application.Current!.ActualThemeVariant);
                shell.QuickInput.Close();                                              // Escape: back as it was
                Assert.True(Ui.PumpUntil(() => Surface() == 0xFF15171Bu, 5), "Escape did not restore");

                shell.Commands.First(c => c.Id == "workbench.action.selectTheme").Run();
                var ember = shell.QuickInput.Items.ToList().FindIndex(i => i.Label == "Ember");
                var chosen = shell.QuickInput.ChooseAsync(ember);
                Assert.True(Ui.PumpUntil(() => chosen.IsCompleted && shell.Settings.ColorTheme == "Ember", 5));
                Assert.True(Ui.PumpUntil(() => Surface() == 0xFF1A1310u, 5));
                Assert.Equal(0xFF160F0Cu, EditorServices.Ready.Result.EditorTheme.Background);
                Assert.Contains("\"workbench.colorTheme\": \"Ember\"", File.ReadAllText(profile.Paths.SettingsFile), StringComparison.Ordinal);
            }
            finally
            {
                var services = EditorServices.Ready.Result;
                shell.Themes.Apply(shell.Themes.Builtins[0], services);
                services.Apply(new Wicked.Core.Settings.WickedSettings());
                window.Close();
            }

            return 0;
        });
    }
}
