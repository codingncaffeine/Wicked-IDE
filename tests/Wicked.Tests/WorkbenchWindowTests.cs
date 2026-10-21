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

    internal static (MainWindow Window, ShellViewModel Shell) OpenWorkbench(TempProfile profile, string folder)
    {
        var (window, shell) = Ui.Start(profile.Paths);
        var opened = shell.OpenFolderAndRememberAsync(folder);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted && Rows(window).Any(), 10), "the folder did not open");
        return (window, shell);
    }

    internal static IEnumerable<TreeViewItem> Rows(Window window) =>
        window.GetVisualDescendants().OfType<TreeViewItem>().Where(i => i.IsEffectivelyVisible);

    internal static TreeViewItem Row(Window window, string name) =>
        Rows(window).First(i => i.DataContext is ExplorerNode n && n.Name == name);

    internal static EditorTab OpenByClicks(MainWindow window, ShellViewModel shell, params string[] names)
    {
        foreach (var name in names[..^1])
        {
            Input.Click(window, Row(window, name).GetVisualDescendants().OfType<TextBlock>().First());
            Assert.True(Ui.PumpUntil(() => Rows(window).Any(r => r.DataContext is ExplorerNode { Parent.Name: var p } && p == name), 5), $"{name} did not open");
        }

        Input.Click(window, Row(window, names[^1]).GetVisualDescendants().OfType<TextBlock>().First());
        // The clicked file's own editor: the one open before it is attached already.
        Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab is { Path: { } path, Editor: { } shown } && Path.GetFileName(path) == Path.GetFileName(names[^1]) && shown.IsAttachedToVisualTree(), 5), $"{names[^1]} did not open in an editor");

        // As a user would, click into the text to type there (focus that follows the open is not every platform's promise).
        var editor = shell.Workbench.ActiveTab!.Editor!;
        if (!editor.IsFocused) Input.Click(window, editor);
        Assert.True(Ui.PumpUntil(() => editor.IsFocused, 5), "the editor did not take the keyboard");
        return shell.Workbench.ActiveTab!;
    }

    [Trait("Tier", "Window")]
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

    [Trait("Tier", "Smoke")]
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

    [Trait("Tier", "Window")]
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

    [Trait("Tier", "Window")]
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

    [Trait("Tier", "Smoke")]
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

    [Trait("Tier", "Window")]
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

    [Trait("Tier", "Window")]
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

/// <summary>Find and replace through the window.</summary>
public sealed class FindWindowTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task CtrlFFindsCtrlHReplacesAndEscapeCloses()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "p");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "cat dog cat bird cat\n");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var open = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
            var opening = shell.Workbench.OpenAsync(Path.Combine(root, "a.txt"));
            Assert.True(Ui.PumpUntil(() => opening.IsCompleted && shell.Workbench.ActiveTab?.Editor?.IsAttachedToVisualTree() == true, 5));
            var editor = shell.Workbench.ActiveTab!.Editor!;
            Input.Click(window, editor);

            Input.Chord(window, Avalonia.Input.Key.F, Avalonia.Input.RawInputModifiers.Control);
            Assert.True(editor.Find.IsOpen);
            window.KeyTextInput("cat");
            Assert.True(Ui.PumpUntil(() => editor.Find.Status == "1 of 3", 5), editor.Find.Status);
            Assert.Equal(new Wicked.Editor.Model.Selection(0, 3), editor.Document.Primary);
            if (Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_FIND") is { Length: > 0 } shot)
            {
                editor.Find.ShowReplace = true;
                Ui.Pump(0.3);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                editor.Find.ShowReplace = false;
            }


            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Ui.Pump(0.1);
            Assert.Equal("2 of 3", editor.Find.Status);
            Assert.Equal(8, editor.Document.Primary.Start);

            editor.Find.ShowReplace = true;
            editor.Find.Replacement = "lion";
            editor.Find.ReplaceAll();
            Assert.True(Ui.PumpUntil(() => editor.Find.Status == "No results", 5), editor.Find.Status);
            Assert.Equal("lion dog lion bird lion\n", editor.Document.Text.ToString());

            window.KeyPress(Avalonia.Input.Key.Escape, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Escape, null);
            Ui.Pump(0.1);
            Assert.False(editor.Find.IsOpen);
            Assert.True(editor.IsFocused);
            window.Close();
            return 0;
        });
    }
}

/// <summary>The explorer's file actions through the window.</summary>
public sealed class ExplorerActionsTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task NewFileRenameAndDeleteFromTheExplorer()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the freedesktop Trash");
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "proj");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "keep.txt"), "");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            shell.Workbench.TrashEnvironment = v => v == "XDG_DATA_HOME" ? Path.Combine(profile.Root, "xdg") : null;
            var open = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));

            // New File from the toolbar: a name typed into the prompt, the file made and opened.
            Input.Click(window, window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().First(b => b.Name == "NewFileButton"));
            Assert.True(shell.QuickInput.IsOpen);
            window.KeyTextInput("hello.py");
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Title == "hello.py", 5), "the new file did not open");
            Assert.True(File.Exists(Path.Combine(root, "hello.py")));

            // Rename: the open tab follows the file.
            var rename = shell.Workbench.RenameAsync(Path.Combine(root, "hello.py"));
            Ui.Pump(0.1);
            shell.QuickInput.Query = "world.py";
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => rename.IsCompleted, 5));
            Assert.True(File.Exists(Path.Combine(root, "world.py")));
            Assert.Equal("world.py", shell.Workbench.ActiveTab?.Title);

            // Delete: asked, then into the Trash (the test's own).
            var trash = shell.Workbench.TrashAsync(Path.Combine(root, "keep.txt"));
            Ui.Pump(0.1);
            Assert.Equal("Move to Trash", window.FindControl<Avalonia.Controls.Button>("DialogSave")!.Content);
            Input.Click(window, window.FindControl<Avalonia.Controls.Button>("DialogSave")!);
            Assert.True(Ui.PumpUntil(() => trash.IsCompleted, 5));
            Assert.False(File.Exists(Path.Combine(root, "keep.txt")));
            Assert.True(File.Exists(Path.Combine(profile.Root, "xdg", "Trash", "files", "keep.txt")));
            window.Close();
            return 0;
        });
    }
}

/// <summary>The explorer follows the active file, the breadcrumbs say where it is, the welcome page lists recent folders.</summary>
public sealed class WayfindingTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task OpeningAFileRevealsItAndTheBreadcrumbsNameItsPlace()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "proj");
        Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
        File.WriteAllText(Path.Combine(root, "src", "deep", "Thing.cs"), "class Thing { }\n");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var open = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
            var file = shell.Workbench.OpenAsync(Path.Combine(root, "src", "deep", "Thing.cs"));
            Assert.True(Ui.PumpUntil(() => file.IsCompleted && shell.Workbench.Revealed?.Name == "Thing.cs", 5), "the file was not revealed");

            var tree = window.GetVisualDescendants().OfType<Avalonia.Controls.TreeView>().First(t => t.Name == "Tree");
            Assert.True(Ui.PumpUntil(() => (tree.SelectedItem as Wicked.App.Workbench.ExplorerNode)?.Name == "Thing.cs", 5));
            var crumbs = window.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
                .Where(t => t.Classes.Contains("crumb") || t.Classes.Contains("crumb-last")).Select(t => t.Text).ToList();
            Assert.Equal(["src", "deep", "Thing.cs"], crumbs);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheWelcomePageReopensARecentFolder()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "earlier");
        Directory.CreateDirectory(root);
        await Ui.Run(() =>
        {
            // A first run opens the folder; a second run, with restoring off, shows it under Recent.
            var (first, firstShell) = Ui.Start(profile.Paths);
            var open = firstShell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => open.IsCompleted && File.Exists(profile.Paths.SessionFile), 5));
            first.Close();
            Directory.CreateDirectory(profile.Paths.Config);
            File.WriteAllText(profile.Paths.SettingsFile, "{ \"window.restoreWindows\": false }");

            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
            Ui.Pump(0.2);
            var recent = window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().FirstOrDefault(b => b.Classes.Contains("recent") && b.IsEffectivelyVisible);
            Assert.NotNull(recent);
            Input.Click(window, recent);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.Root == root, 5), "the recent folder did not open");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheNextStartReopensTheFolderAndItsOpenFiles()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        await Ui.Run(() =>
        {
            // The first run opens the folder and two files (kept, not previews), b.txt in front, and closes.
            var (first, firstShell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            foreach (var name in new[] { "a.txt", "b.txt" })
            {
                var open = firstShell.Workbench.OpenAsync(Path.Combine(root, name));
                Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
            }

            Assert.Equal(2, firstShell.Workbench.AllTabs.Count());
            first.Close();
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SessionFile) && File.ReadAllText(profile.Paths.SessionFile).Contains("b.txt", StringComparison.Ordinal), 5),
                "the open files were not remembered");

            // The next start shows them again, b.txt in front, in its editor.
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Editor?.IsAttachedToVisualTree() == true, 5), "no editor is shown");
            Assert.Equal(["a.txt", "b.txt"], shell.Workbench.ActiveGroup.Tabs.Select(t => t.Title));
            Assert.Equal("b.txt", shell.Workbench.ActiveTab!.Title);
            window.Close();
            return 0;
        });
    }
}

/// <summary>A project's .editorconfig governs its files: indentation, line endings and tidying on save.</summary>
public sealed class EditorConfigWindowTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheProjectsEditorConfigShapesIndentationAndSaves()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "proj");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, ".editorconfig"), "root = true\n\n[*.py]\nindent_size = 2\nend_of_line = crlf\ntrim_trailing_whitespace = true\n");
        File.WriteAllText(Path.Combine(root, "a.py"), "def f():\n    pass\n");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var open = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
            var opening = shell.Workbench.OpenAsync(Path.Combine(root, "a.py"));
            Assert.True(Ui.PumpUntil(() => opening.IsCompleted && shell.Workbench.ActiveTab?.Editor is not null, 5));
            var tab = shell.Workbench.ActiveTab!;
            Assert.Equal(2, tab.Editor!.Options.TabSize);

            tab.Document.SetSelections([Wicked.Editor.Model.Selection.Caret(tab.Document.Text.Length)]);
            Wicked.Editor.Model.Edits.Type(tab.Document, "x = 1   ", Wicked.Editor.Model.EditOptions.Default);
            var save = shell.Workbench.SaveAsync(tab);
            Assert.True(Ui.PumpUntil(() => save.IsCompleted, 5));
            Assert.Equal("def f():\r\n    pass\r\nx = 1", File.ReadAllText(Path.Combine(root, "a.py")));
            window.Close();
            return 0;
        });
    }
}
