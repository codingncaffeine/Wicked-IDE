using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Wicked.App.Shell;
using Wicked.App.ViewModels;
using Wicked.Tests.Models;

namespace Wicked.Tests;

/// <summary>Help: Get Started, the links, the shortcuts reference, About and Check for Updates, through the window.</summary>
public sealed class HelpWindowTests
{
    private static string? PanelHeading(Window window) =>
        window.FindControl<Border>("SidePanel") is { IsEffectivelyVisible: true } panel
            ? panel.GetVisualDescendants().OfType<TextBlock>().First(t => t.Classes.Contains("heading")).Text
            : null;

    private static Button Labelled(Window window, string label) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && Avalonia.Automation.AutomationProperties.GetName(b) == label);

    [Trait("Tier", "Window")]
    [Fact]
    public async Task GetStartedTicksWhatIsDoneAndItsButtonsRunTheirCommands()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");

            // The welcome page's link opens the walkthrough as a tab.
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().First(b => b.Name == "WelcomeGetStarted" && b.IsEffectivelyVisible));
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Title == "Get Started", 5), shell.Workbench.ActiveTab?.Title);
            Ui.Pump(0.2);
            var titles = window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.Name == "StepTitle" && t.IsEffectivelyVisible).Select(t => t.Text).ToList();
            Assert.Equal(["Choose a model", "Open a project", "Ask the assistant", "Run and debug", "Make it yours", "Learn the keys"], titles);
            GetStartedStep Step(string title) => (GetStartedStep)window.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "StepTitle" && t.Text == title).DataContext!;
            Assert.False(Step("Open a project").IsDone);

            // Opening a folder ticks its step; the step's buttons carry the keys that run them.
            var opened = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && Step("Open a project").IsDone, 10), "the project step was not ticked");
            Assert.False(Step("Choose a model").IsDone);
            Assert.Equal("Ctrl+Shift+P", Step("Learn the keys").Actions.Single(a => a.Label == "Show All Commands").Keys);

            // A button runs its command: Choose a model opens the Models panel.
            Input.Click(window, Labelled(window, "Choose a model"));
            Assert.True(Ui.PumpUntil(() => PanelHeading(window) == "MODELS", 5), PanelHeading(window));

            // Help: Get Started again brings the open tab forward instead of a second one.
            var again = shell.RunCommandAsync("workbench.action.openWalkthrough");
            Assert.True(Ui.PumpUntil(() => again.IsCompleted, 5));
            Assert.Single(shell.Workbench.AllTabs, t => t.Title == "Get Started");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task HelpLinksOpenTheRightPagesAndTheReferenceListsTheKeysInForce()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
            var opened = new ConcurrentQueue<string>();
            shell.Models.OpenPath = opened.Enqueue;
            string Run(string id)
            {
                var before = opened.Count;
                var run = shell.RunCommandAsync(id);
                Assert.True(Ui.PumpUntil(() => run.IsCompleted && opened.Count > before, 5), $"{id} opened nothing");
                return opened.Last();
            }

            var version = SystemInfo.ShortVersion;
            Assert.Equal(SystemInfo.Repository + "#readme", Run("workbench.action.openDocumentationUrl"));
            Assert.Equal($"{SystemInfo.Repository}/releases/tag/v{version}", Run("update.showCurrentReleaseNotes"));
            Assert.Equal(SystemInfo.Repository + "/blob/main/LICENSE", Run("workbench.action.openLicenseUrl"));
            var issue = Run("workbench.action.openIssueReporter");
            Assert.StartsWith(SystemInfo.Repository + "/issues/new?body=", issue, StringComparison.Ordinal);
            var body = Uri.UnescapeDataString(issue[(issue.IndexOf('=', StringComparison.Ordinal) + 1)..]);
            Assert.Contains($"Wicked: {version}", body, StringComparison.Ordinal);
            Assert.Contains("\nOS: ", body, StringComparison.Ordinal);
            Assert.Contains("\nGPU: ", body, StringComparison.Ordinal);

            // The reference is the keys in force: a binding from keybindings.json shows, written as people read keys.
            File.WriteAllText(profile.Paths.KeybindingsFile, """[ { "key": "ctrl+alt+9", "command": "workbench.action.files.saveAll" } ]""");
            Assert.True(Ui.PumpUntil(() => shell.Keys.KeysFor("workbench.action.files.saveAll") == "Ctrl+Alt+D9", 5), shell.Keys.KeysFor("workbench.action.files.saveAll"));
            var page = Run("workbench.action.keybindingsReference");
            Assert.Equal(Path.Combine(profile.Paths.Cache, "keyboard-shortcuts.html"), page);
            var html = File.ReadAllText(page);
            Assert.Contains("<td>Save All</td><td class=\"k\"><kbd>Ctrl+Alt+9</kbd>", html, StringComparison.Ordinal);
            Assert.Contains("<td>Zoom In</td><td class=\"k\"><kbd>Ctrl+=</kbd>", html, StringComparison.Ordinal);
            Assert.Contains("<kbd>Ctrl+K</kbd> <kbd>Ctrl+O</kbd>", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Oem", html, StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AboutSaysWhatIsRunningAndCopiesIt()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var copied = new ConcurrentQueue<string>();
            shell.Clipboard = text =>
            {
                copied.Enqueue(text);
                return Task.CompletedTask;
            };

            var about = shell.RunCommandAsync("workbench.action.showAboutDialog");
            var layer = window.FindControl<Border>("DialogLayer")!;
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 5), "no About box");
            Assert.Equal($"Wicked {SystemInfo.ShortVersion}", window.FindControl<TextBlock>("DialogMessage")!.Text);
            var detail = window.FindControl<TextBlock>("DialogDetail")!.Text!;
            Assert.Contains("\nOS: ", detail, StringComparison.Ordinal);
            Assert.Contains("\n.NET: ", detail, StringComparison.Ordinal);
            Assert.Contains("GNU General Public License", detail, StringComparison.Ordinal);
            var copy = window.FindControl<Button>("DialogSave")!;
            Assert.Equal("Copy", copy.Content);

            Input.Click(window, copy);
            Assert.True(Ui.PumpUntil(() => about.IsCompleted && !copied.IsEmpty, 5), "nothing was copied");
            Assert.StartsWith($"Wicked: {SystemInfo.ShortVersion}", copied.Single(), StringComparison.Ordinal);
            Assert.DoesNotContain("GNU", copied.Single(), StringComparison.Ordinal);

            // Escape answers Cancel: the box closes and nothing more is copied.
            about = shell.RunCommandAsync("workbench.action.showAboutDialog");
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 5), "no About box the second time");
            Input.Chord(window, Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => about.IsCompleted && !layer.IsVisible, 5), "Escape left the About box open");
            Assert.Single(copied);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task CheckForUpdatesSaysWhatThisInstallHasToDo()
    {
        using var profile = new TempProfile();
        await using var world = await ModelWorld.StartAsync();
        world.AppTag = "v9.0.0";
        await Ui.Run(() =>
        {
            var (window, shell) = world.Start(profile.Paths);
            var start = shell.Models.StartAsync(allowNetwork: true);
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 20));
            string? Notice(string key) => shell.Notices.All.FirstOrDefault(n => n.Key == key)?.Text;

            // A newer release: the notice names it and says how this kind of install is updated (a source build here).
            var check = shell.RunCommandAsync("update.checkForUpdates");
            Assert.True(Ui.PumpUntil(() => check.IsCompleted && Notice("app-update") is not null, 15), Notice("app-check"));
            Assert.StartsWith("Wicked 9.0.0 is available. ", Notice("app-update"), StringComparison.Ordinal);
            Assert.EndsWith(SystemInfo.HowToUpdate(SystemInfo.Install(), "9.0.0"), Notice("app-update"), StringComparison.Ordinal);

            // None newer: it says so.
            world.AppTag = "v0.0.0";
            shell.Notices.Clear("app-update");
            check = shell.RunCommandAsync("update.checkForUpdates");
            Assert.True(Ui.PumpUntil(() => check.IsCompleted, 15));
            Assert.Equal($"Wicked {SystemInfo.ShortVersion} is the newest version.", Notice("app-check"));
            Assert.Null(Notice("app-update"));
            window.Close();
            return 0;
        });
    }
}

/// <summary>How an install is recognised and what it is told to do; key names as people read them.</summary>
public sealed class SystemInfoTests
{
    [Theory]
    [InlineData("/home/me/src/Wicked/src/Wicked.App/bin/Release/net10.0/", false, false, InstallKind.Development)]
    [InlineData("/opt/WickedIDE/", false, false, InstallKind.Tarball)]
    [InlineData("/usr/lib/wicked-ide/", true, false, InstallKind.Deb)]
    [InlineData("/usr/lib/wicked-ide/", false, true, InstallKind.Aur)]
    public void TheInstallIsToldApartByWhereItLives(string dir, bool dpkg, bool pacman, InstallKind expected)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows() && expected != InstallKind.Development, "a Linux layout");
        Assert.Equal(expected, SystemInfo.Install(dir, f => dpkg && f == "/var/lib/dpkg/info/wicked-ide.list", d => pacman && d == "/var/lib/pacman/local"));
    }

    [Fact]
    public void EachInstallIsToldHowToUpdate()
    {
        Assert.Contains("yay -Syu wicked-ide-bin", SystemInfo.HowToUpdate(InstallKind.Aur, "1.2.3"), StringComparison.Ordinal);
        Assert.Contains("sudo apt install ./wicked-ide_1.2.3_amd64.deb", SystemInfo.HowToUpdate(InstallKind.Deb, "1.2.3"), StringComparison.Ordinal);
        Assert.Contains("tarball", SystemInfo.HowToUpdate(InstallKind.Tarball, "1.2.3"), StringComparison.Ordinal);
        Assert.Contains("zip", SystemInfo.HowToUpdate(InstallKind.WindowsZip, "1.2.3"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Ctrl+OemPlus", "Ctrl+=")]
    [InlineData("Ctrl+K Ctrl+OemComma", "Ctrl+K Ctrl+,")]
    [InlineData("Ctrl+D1", "Ctrl+1")]
    [InlineData("Ctrl+Shift+OemTilde", "Ctrl+Shift+`")]
    [InlineData("Ctrl+OemPipe", "Ctrl+\\")]
    [InlineData("Shift+Alt+F", "Shift+Alt+F")]
    [InlineData("F12", "F12")]
    [InlineData("", "")]
    public void KeysAreWrittenAsPeopleReadThem(string avalonia, string readable) => Assert.Equal(readable, KeyNames.Readable(avalonia));
}
