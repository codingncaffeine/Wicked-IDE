using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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

    /// <summary>The colour the editor paints the token at <paramref name="column"/> of <paramref name="line"/> in.</summary>
    private static uint ColourAt(Wicked.Editor.Syntax.SyntaxSnapshot s, int line, int column)
    {
        var tokens = s[line] ?? throw new InvalidOperationException($"line {line} has no tokens");
        var index = 0;
        while (index + 1 < tokens.Count && tokens.Start(index + 1) <= column) index++;
        return s.Colors[tokens.Foreground(index)];
    }

    /// <summary>The colour a code theme on its own gives the token (an independent highlighter, settled).</summary>
    private static uint ExpectedColour(TextMateSharp.Grammars.ThemeName theme, string text, string file, int line, int column)
    {
        using var service = new Wicked.Editor.Syntax.SyntaxService(theme);
        var doc = new Wicked.Editor.Model.Document(text);
        Wicked.Editor.Syntax.SyntaxSnapshot? latest = null;
        service.Open(doc.Text, doc.Version, Wicked.Editor.Syntax.Languages.ForFile(file), s => Volatile.Write(ref latest, s));
        var until = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref latest) is not { } s || s.ValidLines != doc.Text.LineCount)
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("the reference highlighting did not settle");
            Thread.Sleep(20);
        }

        return ColourAt(latest!, line, column);
    }

    [Fact]
    public async Task PickingAWindowThemeRecoloursTheCodeAlreadyOpen()
    {
        const string Text = "public sealed class Totals\n{\n    // every line\n}\n";
        var darkPlus = ExpectedColour(TextMateSharp.Grammars.ThemeName.DarkPlus, Text, "a.cs", 0, 0);
        var dracula = ExpectedColour(TextMateSharp.Grammars.ThemeName.Dracula, Text, "a.cs", 0, 0);
        Assert.NotEqual(darkPlus, dracula);
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.cs"), Text);
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            try
            {
                var view = WorkbenchWindowTests.OpenByClicks(window, shell, "a.cs").Editor!.View;
                Assert.True(Ui.PumpUntil(() => view.Syntax is { } s && s.ValidLines == view.Document.Text.LineCount, 20), "the file was not highlighted");
                Assert.Equal(darkPlus, ColourAt(view.Syntax!, 0, 0));

                // The user's path: Settings, the theme box, Neon (whose code colours are Dracula's).
                shell.SettingsOpen = true;
                Ui.Pump(0.3);
                var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "WindowThemeBox");
                box.SelectedItem = box.Items.OfType<Wicked.App.ViewModels.SettingChoice>().First(c => c.Value == "Neon");
                Assert.True(Ui.PumpUntil(() => ColourAt(view.Syntax!, 0, 0) == dracula, 20), $"the keyword is still {ColourAt(view.Syntax!, 0, 0):X8}, Dracula's is {dracula:X8}");
            }
            finally
            {
                var services = Wicked.App.Editor.EditorServices.Ready.Result;
                shell.Themes.Apply(shell.Themes.Builtins[0], services);
                services.Apply(new Wicked.Core.Settings.WickedSettings());
                window.Close();
            }

            return 0;
        });
    }

    [Fact]
    public async Task TheComposerRowFitsTheAssistantWithEveryChipShowing()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a-file-with-a-long-name.cs"), "class A { }\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            try
            {
                WorkbenchWindowTests.OpenByClicks(window, shell, "a-file-with-a-long-name.cs");
                var agent = window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "AgentToggle");
                var file = window.GetVisualDescendants().OfType<ToggleButton>().First(b => b.Name == "FileChip");
                Assert.True(Ui.PumpUntil(() => agent.IsEffectivelyVisible && file.IsEffectivelyVisible, 5), "the agent toggle and the file chip did not show");
                Ui.Pump(0.3);
                var row = (Grid)agent.GetVisualParent()!;
                foreach (var chip in row.Children.Where(c => c.IsVisible && c.Name != "PromptChip"))
                {
                    Assert.True(chip.Bounds.Width >= chip.DesiredSize.Width - 0.5, $"{chip.Name} is squeezed: {chip.Bounds.Width:0.#} of {chip.DesiredSize.Width:0.#}");
                    Assert.True(chip.Bounds.Right <= row.Bounds.Width + 0.5, $"{chip.Name} runs past the row: {chip.Bounds.Right:0.#} of {row.Bounds.Width:0.#}");
                }

                var text = agent.GetVisualDescendants().OfType<TextBlock>().Single();
                Assert.Equal("Agent", text.Text);
                Assert.True(text.Bounds.Width >= text.DesiredSize.Width - 0.5, $"the label is cut: {text.Bounds.Width:0.#} of {text.DesiredSize.Width:0.#}");
            }
            finally
            {
                window.Close();
            }

            return 0;
        });
    }

    [Fact]
    public async Task ThePanelHasATitleBarButtonAndOpensAgainAsItWasLeft()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "text\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            Assert.False(shell.BottomPanel.IsVisible);
            var toggle = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PanelToggle");
            Input.Click(window, toggle);
            Assert.True(Ui.PumpUntil(() => shell.BottomPanel.IsVisible, 5), "the title bar button did not show the panel");
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext is PanelTab tab && tab.Id == PanelIds.Output));
            Assert.True(Ui.PumpUntil(() => shell.BottomPanel.Active?.Id == PanelIds.Output, 5), "the Output tab was not chosen");
            window.Close();
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SessionFile) && System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(profile.Paths.SessionFile), "panelVisible\"\\s*:\\s*true", System.Text.RegularExpressions.RegexOptions.IgnoreCase), 10), "the panel was not remembered");

            // Next time Wicked starts, the panel is open on the tab it was left on.
            var (again, next) = Ui.Start(profile.Paths);
            var start = next.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted && next.BottomPanel.IsVisible, 10), "the panel did not open again");
            Assert.Equal(PanelIds.Output, next.BottomPanel.Active?.Id);
            Input.Click(again, again.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PanelToggle"));
            Assert.True(Ui.PumpUntil(() => !next.BottomPanel.IsVisible, 5), "the button did not hide the panel");
            again.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheTitleBarSettingSwapsWickedsBarForTheDesktopsFrameAndBack()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "text\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            try
            {
                var captions = window.FindControl<Control>("TitleBar")!.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("caption")).ToList();
                Assert.Equal(3, captions.Count);
                Assert.True(window.ExtendClientAreaToDecorationsHint);
                Assert.All(captions, b => Assert.True(b.IsVisible));

                shell.SettingsOpen = true;
                Ui.Pump(0.3);
                var box = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "TitleBarBox");
                box.SelectedIndex = 1;
                Assert.True(Ui.PumpUntil(() => shell.Settings.TitleBarStyle == "native" && window.WindowDecorations == WindowDecorations.Full, 5), $"style {shell.Settings.TitleBarStyle}, decorations {window.WindowDecorations}");
                Assert.False(window.ExtendClientAreaToDecorationsHint);
                Assert.All(captions, b => Assert.False(b.IsVisible));

                box.SelectedIndex = 0;
                Assert.True(Ui.PumpUntil(() => shell.Settings.TitleBarStyle == "custom" && window.ExtendClientAreaToDecorationsHint, 5), "custom did not come back");
                Assert.All(captions, b => Assert.True(b.IsVisible));
            }
            finally
            {
                window.Close();
            }

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
