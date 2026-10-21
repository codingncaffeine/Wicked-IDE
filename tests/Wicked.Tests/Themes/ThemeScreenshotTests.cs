using Avalonia.Headless;
using Wicked.App.Editor;
using Wicked.App.Shell;

namespace Wicked.Tests.Themes;

/// <summary>
/// The window in every built-in theme, saved as pictures for a person to judge (D17: the look is the user's call).
/// Skipped unless WICKED_SCREENSHOT_THEMES names a folder.
/// </summary>
public sealed class ThemeScreenshotTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task EveryThemeIsPhotographed()
    {
        var folder = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_THEMES");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "set WICKED_SCREENSHOT_THEMES to a folder to save pictures of every theme");
        Directory.CreateDirectory(folder!);
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "Totals.cs"), """
            using System.Collections.Generic;

            namespace Shop;

            /// <summary>Adds up a basket.</summary>
            public sealed class Totals(IReadOnlyList<decimal> prices)
            {
                public decimal Sum()
                {
                    var total = 0m;
                    foreach (var price in prices) total += price; // every line
                    return total;
                }

                public string Label => $"Total: {Sum():0.00}";
            }
            """);
        File.WriteAllText(Path.Combine(root, "README.md"), "# Shop\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            try
            {
                WorkbenchWindowTests.OpenByClicks(window, shell, "src", "Totals.cs");
                shell.BottomPanel.Show(PanelIds.Problems);
                var services = EditorServices.Ready.Result;
                foreach (var theme in shell.Themes.Builtins)
                {
                    shell.Themes.Apply(theme, services);
                    services.Apply(shell.Settings);
                    foreach (var tab in shell.Workbench.AllTabs) tab.Editor?.ApplySettings(shell.Settings);
                    Ui.Pump(1.0);
                    using var frame = window.CaptureRenderedFrame();
                    frame?.Save(Path.Combine(folder!, theme.Name.Replace(' ', '-').ToLowerInvariant() + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                }
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
