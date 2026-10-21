using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.App.Workbench;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>Zen Mode, full screen, the status bar, the panel's place, pinned tabs, Ctrl+Tab and preview tabs, through the window.</summary>
public sealed class LayoutWindowTests
{
    private static void Run(ShellViewModel shell, string id)
    {
        var run = shell.RunCommandAsync(id);
        Assert.True(Ui.PumpUntil(() => run.IsCompleted, 5), $"{id} did not finish");
        Ui.Pump(0.2);
    }

    private static string Folder(TempProfile profile)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(root, name), name);
        return root;
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ZenModeFullScreenTheStatusBarAndThePanelsPlace()
    {
        using var profile = new TempProfile();
        var root = Folder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
            var fullScreen = new List<bool>();
            shell.FullScreenRequested += fullScreen.Add;
            var statusBar = window.FindControl<Grid>("StatusBar")!;
            var rail = window.FindControl<DockPanel>("Rail")!;
            shell.BottomPanel.Toggle();
            Assert.True(shell.SidebarVisible && shell.AssistantVisible && shell.BottomPanel.IsVisible && statusBar.IsVisible && rail.IsVisible);

            // F11: full screen and back.
            Input.Chord(window, Key.F11, RawInputModifiers.None);
            Input.Chord(window, Key.F11, RawInputModifiers.None);
            Assert.Equal([true, false], fullScreen);

            // Ctrl+K Z: only the editor, full screen; Escape twice brings everything back as it was.
            Input.Chord(window, Key.K, RawInputModifiers.Control);
            Input.Chord(window, Key.Z, RawInputModifiers.None);
            Assert.True(shell.ZenMode);
            Assert.False(shell.SidebarVisible || shell.AssistantVisible || shell.BottomPanel.IsVisible || statusBar.IsVisible || rail.IsVisible);
            Assert.True(fullScreen[^1]);
            Input.Chord(window, Key.Escape, RawInputModifiers.None);
            Input.Chord(window, Key.Escape, RawInputModifiers.None);
            Assert.False(shell.ZenMode);
            Assert.True(shell.SidebarVisible && shell.AssistantVisible && shell.BottomPanel.IsVisible && statusBar.IsVisible && rail.IsVisible);
            Assert.False(fullScreen[^1]);

            // The status bar can be hidden (a setting, kept).
            Run(shell, "workbench.action.toggleStatusbarVisibility");
            Assert.False(statusBar.IsVisible);
            Assert.Contains("\"workbench.statusBar.visible\": false", File.ReadAllText(profile.Paths.SettingsFile), StringComparison.Ordinal);
            Run(shell, "workbench.action.toggleStatusbarVisibility");
            Assert.True(statusBar.IsVisible);

            // The panel to the right of the editors, then to the left, then below again.
            var editor = window.FindControl<Border>("Editor")!;
            var panel = window.FindControl<Border>("PanelArea")!;
            double X(Control c) => c.TranslatePoint(default, window)!.Value.X;
            double Y(Control c) => c.TranslatePoint(default, window)!.Value.Y;
            Run(shell, "workbench.action.positionPanelRight");
            Assert.True(X(panel) > X(editor) && Math.Abs(Y(panel) - Y(editor)) < 1, $"panel at {X(panel)},{Y(panel)}; editor at {X(editor)},{Y(editor)}");
            Run(shell, "workbench.action.positionPanelLeft");
            Assert.True(X(panel) < X(editor), $"panel at {X(panel)}; editor at {X(editor)}");
            Run(shell, "workbench.action.positionPanelBottom");
            Assert.True(Y(panel) > Y(editor), $"panel at {Y(panel)}; editor at {Y(editor)}");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task PinnedTabsCtrlTabAndPreviewTabs()
    {
        using var profile = new TempProfile();
        var root = Folder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            var group = bench.ActiveGroup;
            string Titles() => string.Join(",", group.Tabs.Select(t => (t.IsPinned ? "*" : "") + t.Title));
            EditorTab Open(string name)
            {
                var open = bench.OpenAsync(Path.Combine(root, name));
                Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
                return open.Result!;
            }

            var a = Open("a.txt");
            var b = Open("b.txt");
            var c = Open("c.txt");

            // Pinning c moves it to the front; Close Others and Ctrl+W leave it; its pin unpins it.
            Run(shell, "workbench.action.pinEditor");
            Assert.Equal("*c.txt,a.txt,b.txt", Titles());
            group.Activate(a);
            Run(shell, "workbench.action.closeOtherEditors");
            Assert.Equal("*c.txt,a.txt", Titles());
            group.Activate(c);
            Input.Chord(window, Key.W, RawInputModifiers.Control);
            Assert.Equal("*c.txt,a.txt", Titles());
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(x => x.Name == "TabPin" && x.IsEffectivelyVisible));
            Assert.Equal("c.txt,a.txt", Titles());

            // Ctrl+Tab: the editor used before this one; Tab again while Ctrl is held goes one further; letting go opens it.
            b = Open("b.txt");
            group.Activate(c);
            group.Activate(a);
            Assert.Equal([a, c, b], group.Recent);
            window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Ui.Pump(0.2);
            Assert.True(shell.QuickInput.IsOpen && shell.QuickInput.IsSwitcher);
            Assert.Equal("c.txt", shell.QuickInput.Items[shell.QuickInput.Selected].Label);
            window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Ui.Pump(0.1);
            Assert.Equal("b.txt", shell.QuickInput.Items[shell.QuickInput.Selected].Label);
            window.KeyRelease(Key.LeftCtrl, RawInputModifiers.None, PhysicalKey.ControlLeft, null);
            Assert.True(Ui.PumpUntil(() => !shell.QuickInput.IsOpen && bench.ActiveTab == b, 5), bench.ActiveTab?.Title);

            // With workbench.editor.enablePreview off, a single click in the explorer keeps the tab.
            var off = shell.UpdateSettingsAsync(s => s with { EnablePreview = false });
            Assert.True(Ui.PumpUntil(() => off.IsCompleted, 5));
            var preview = bench.OpenAsync(Path.Combine(root, "a.txt"), preview: true);
            Assert.True(Ui.PumpUntil(() => preview.IsCompleted, 5));
            File.WriteAllText(Path.Combine(root, "d.txt"), "d");
            var d = bench.OpenAsync(Path.Combine(root, "d.txt"), preview: true);
            Assert.True(Ui.PumpUntil(() => d.IsCompleted, 5));
            Assert.False(d.Result!.IsPreview);
            window.Close();
            return 0;
        });
    }
}
