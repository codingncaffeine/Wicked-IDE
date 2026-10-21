using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Shell;

namespace Wicked.Tests.Themes;

/// <summary>Customisation and access: style packs, F6 between the window's parts, every button named for a screen reader.</summary>
public sealed class PolishWindowTests
{
    [Fact]
    public async Task AStylePackRestylesTheWindowAndABrokenOneIsReportedNotApplied()
    {
        using var profile = new TempProfile();
        var styles = Path.Combine(profile.Paths.Config, "styles");
        Directory.CreateDirectory(styles);
        File.WriteAllText(Path.Combine(styles, "square.axaml"), """
            <Styles xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <Style Selector="Border.panel"><Setter Property="CornerRadius" Value="0" /></Style>
            </Styles>
            """);
        File.WriteAllText(Path.Combine(styles, "broken.axaml"), "<Styles xmlns=\"https://github.com/avaloniaui\"><Style Selector=\"Border.panel\"><Setter Property=\"NoSuchProperty\" Value=\"1\" /></Style>");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            try
            {
                var start = shell.StartAsync();
                Assert.True(Ui.PumpUntil(() => start.IsCompleted && shell.StylePacks?.Loaded.Count > 0, 10), "no style pack");
                Assert.Equal(["square.axaml"], shell.StylePacks!.Loaded);
                var panel = window.FindControl<Border>("SidePanel")!;
                Assert.True(Ui.PumpUntil(() => panel.CornerRadius == new CornerRadius(0), 5), $"radius {panel.CornerRadius}");
                Assert.True(Ui.PumpUntil(() =>
                {
                    shell.Output.Flush();
                    return shell.Output.Channels.Any(c => c.Name == "Styles" && c.Document.Text.ToString().Contains("broken.axaml", StringComparison.Ordinal));
                }, 5), "the broken pack was not reported");
            }
            finally
            {
                foreach (var name in shell.StylePacks?.Loaded.ToList() ?? []) File.Delete(Path.Combine(styles, name));
                var reload = shell.StylePacks?.LoadAsync();
                Ui.PumpUntil(() => reload?.IsCompleted != false, 5);
                window.Close();
            }

            return 0;
        });
    }

    [Fact]
    public async Task F6MovesTheKeyboardBetweenTheWindowsPartsAndShiftF6Back()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "text\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "a.txt");
            Assert.True(tab.Editor!.IsFocused);
            bool In(string part) => window.FindControl<Control>(part)!.IsVisualAncestorOf((Visual)window.FocusManager!.GetFocusedElement()!);
            Input.Chord(window, Key.F6, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => In("Assistant"), 5), "F6 did not reach the assistant");
            Input.Chord(window, Key.F6, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => In("SidePanel"), 5), "F6 did not wrap to the side bar");
            Input.Chord(window, Key.F6, RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => In("Assistant"), 5), "Shift+F6 did not go back");
            Input.Chord(window, Key.F6, RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => tab.Editor.IsFocused, 5), "Shift+F6 did not reach the editor");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task EveryButtonInTheWindowHasANameAScreenReaderCanRead()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "text\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            WorkbenchWindowTests.OpenByClicks(window, shell, "a.txt");
            shell.BottomPanel.Show(PanelIds.Problems);
            Ui.Pump(0.3);
            var unnamed = AccessibleNames.Unnamed(window).Select(b => $"{b.GetType().Name} {b.Name ?? "(no name)"} in {b.FindAncestorOfType<UserControl>()?.GetType().Name ?? b.GetVisualParent()?.GetType().Name}").ToList();
            Assert.Empty(unnamed);
            window.Close();
            return 0;
        });
    }
}
