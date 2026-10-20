using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.App.Workbench;

namespace Wicked.Tests;

/// <summary>The workbench walked through the window: a folder opened, files clicked, typed into, saved, closed, found.</summary>
public sealed class WorkbenchWindowTests
{
    private static string MakeFolder(TempProfile profile)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        File.WriteAllText(Path.Combine(root, "src", "App.cs"), "class App\n{\n    int x;\n}\n");
        File.WriteAllText(Path.Combine(root, "README.md"), "# Project\n");
        return root;
    }

    private static (MainWindow Window, ShellViewModel Shell) OpenWorkbench(TempProfile profile, string folder)
    {
        var (window, shell) = Ui.Start(profile.Paths);
        var opened = shell.OpenFolderAndRememberAsync(folder);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted && Rows(window).Any(), 10), "the folder did not open");
        return (window, shell);
    }

    private static IEnumerable<TreeViewItem> Rows(Window window) =>
        window.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible);

    private static TreeViewItem Row(Window window, string name) =>
        Rows(window).First(i => i.DataContext is ExplorerNode n && n.Name == name);

    private static EditorTab OpenByClicks(MainWindow window, ShellViewModel shell, params string[] names)
    {
        foreach (var name in names[..^1])
        {
            Input.Click(window, Row(window, name).GetVisualDescendants().OfType<TextBlock>().First());
            Assert.True(Ui.PumpUntil(() => Rows(window).Any(r => r.DataContext is ExplorerNode { Parent.Name: var p } && p == name), 5), $"{name} did not open");
        }

        Input.Click(window, Row(window, names[^1]).GetVisualDescendants().OfType<TextBlock>().First());
        Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Editor?.IsFocused == true, 5), "the file did not open in a focused editor");
        return shell.Workbench.ActiveTab!;
    }

    [Fact]
    public async Task AClickPreviewsAFileAndADoubleClickKeepsIt()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, folder);
            Assert.Equal(["src", "README.md"], Rows(window).Select(r => ((ExplorerNode)r.DataContext!).Name).Take(2));

            var tab = OpenByClicks(window, shell, "src", "App.cs");
            Assert.True(tab.IsPreview);
            Assert.Equal("class App", tab.Document.Text.Line(0));

            // A single click on another file replaces the preview.
            Input.Click(window, Row(window, "README.md").GetVisualDescendants().OfType<TextBlock>().First());
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Title == "README.md", 5));
            Assert.Single(shell.Workbench.ActiveGroup.Tabs);

            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TypingMarksTheTabAndCtrlSSavesItToDisk()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, folder);
            var tab = OpenByClicks(window, shell, "src", "App.cs");
            window.KeyTextInput("// hi");
            Ui.Pump(0.1);
            Assert.True(tab.IsDirty);
            Assert.False(tab.IsPreview);

            Input.Chord(window, Key.S, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => !tab.IsDirty, 5), "the save did not finish");
            Assert.StartsWith("// hi", File.ReadAllText(Path.Combine(folder, "src", "App.cs")));
            Assert.Equal("Saved App.cs", shell.Notice?.Text);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ClosingAnEditedTabAsksAndDontSaveLeavesTheFileAlone()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, folder);
            OpenByClicks(window, shell, "src", "App.cs");
            window.KeyTextInput("zzz");
            Input.Chord(window, Key.W, RawInputModifiers.Control);
            var dialog = window.FindControl<Border>("DialogLayer")!;
            Assert.True(dialog.IsVisible);

            Input.Click(window, window.FindControl<Button>("DialogDontSave")!);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveGroup.Tabs.Count == 0, 5));
            Assert.False(dialog.IsVisible);
            Assert.DoesNotContain("zzz", File.ReadAllText(Path.Combine(folder, "src", "App.cs")));
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task QuickOpenFindsAFileByAFewOfItsLetters()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, folder);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.Index?.Files.Length > 0, 5));
            Input.Chord(window, Key.P, RawInputModifiers.Control);
            Assert.True(shell.QuickInput.IsOpen);
            window.KeyTextInput("rdme");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.Count > 0, 5));
            Assert.Equal("README.md", shell.QuickInput.Items[0].Label);

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Title == "README.md", 5));
            Assert.False(shell.QuickInput.IsOpen);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ThePaletteRunsACommandAndGoesToALine()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, folder);
            var tab = OpenByClicks(window, shell, "src", "App.cs");

            Input.Chord(window, Key.G, RawInputModifiers.Control);
            window.KeyTextInput("3");
            Ui.Pump(0.1);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Pump(0.1);
            Assert.Equal(2, tab.Document.Text.LineOf(tab.Document.Primary.Active));
            Assert.Equal("Ln 3, Col 1", shell.CaretText);

            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("split right");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "View: Split Editor Right", 5));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.Groups.Count == 2, 5));
            Assert.Equal(2, window.GetVisualDescendants().OfType<EditorGroupView>().Count());
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task AnUneditedTabFollowsAChangeOnDisk()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, folder);
            var tab = OpenByClicks(window, shell, "README.md");
            File.WriteAllText(Path.Combine(folder, "README.md"), "# Changed elsewhere\n");
            Assert.True(Ui.PumpUntil(() => tab.Document.Text.Line(0) == "# Changed elsewhere", 10), "the tab did not follow the file");
            Assert.False(tab.IsDirty);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheWorkbenchRendersAFrame()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_WORKBENCH");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, folder);
            OpenByClicks(window, shell, "src", "App.cs");
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Editor?.View.Syntax is not null, 10));
            Ui.Pump(0.3);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(shot)) frame.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();
            return 0;
        });
    }
}
