using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Shell;
using Wicked.Editor.Model;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>The menus: the menu bar, the editor's and the tabs' right-click menus, and the status bar's items.</summary>
public sealed class MenuTests
{
    private static IEnumerable<MenuEntry> Walk(IEnumerable<MenuEntry> entries) =>
        entries.SelectMany(e => e is SubMenu s ? Walk(s.Entries()).Prepend(e) : [e]);

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task EveryMenuItemIsACommand()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        var missing = await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var open = shell.Workbench.OpenAsync(Path.Combine(root, "a.txt"));
            Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
            var tab = open.Result!;
            var entries = shell.MenuBar().SelectMany(m => Walk(m.Entries()))
                .Concat(Walk(shell.EditorContextMenu()))
                .Concat(Walk(shell.TabContextMenu(tab, shell.Workbench.ActiveGroup)));
            var gone = entries.OfType<MenuCommand>().Where(c => !shell.Commands.Any(x => x.Id == c.CommandId)).Select(c => c.CommandId).ToList();
            Assert.Equal(["_File", "_Edit", "_Selection", "_View", "_Go", "_Run", "_Terminal", "_Help"], shell.MenuBar().Select(m => m.Label));
            window.Close();
            return gone;
        });
        Assert.True(missing.Count == 0, "Menu items with no command: " + string.Join(", ", missing));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AltOpensTheMenusAndAnItemRunsItsCommand()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bar = window.FindControl<Menu>("MenuBar")!;
            Assert.True(bar.IsEffectivelyVisible);
            Assert.Equal(["Menu_File", "Menu_Edit", "Menu_Selection", "Menu_View", "Menu_Go", "Menu_Run", "Menu_Terminal", "Menu_Help"],
                bar.Items.OfType<MenuItem>().Select(m => m.Name));

            // Alt+F opens File with its first item, New Text File, ready; Enter runs it.
            // As a keyboard sends it: the key with the letter it types (access keys go by the letter).
            window.KeyPress(Key.F, RawInputModifiers.Alt, PhysicalKey.F, "f");
            window.KeyRelease(Key.F, RawInputModifiers.Alt, PhysicalKey.F, "f");
            var file = bar.Items.OfType<MenuItem>().First();
            Assert.True(Ui.PumpUntil(() => file.IsSubMenuOpen, 5), "Alt+F did not open the File menu");
            var first = file.Items.OfType<MenuItem>().First();
            Assert.Equal("Menu_workbench.action.files.newUntitledFile", first.Name);
            Assert.Contains(first.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Ctrl+N");
            Input.Chord(window, Key.Enter, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab is { Path: null }, 5), "New Text File did not run");
            Assert.False(file.IsSubMenuOpen);

            // Shift+Alt+F is Format Document's, not the File menu's.
            window.KeyPress(Key.F, RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.F, "F");
            window.KeyRelease(Key.F, RawInputModifiers.Alt | RawInputModifiers.Shift, PhysicalKey.F, "F");
            Ui.Pump(0.2);
            Assert.False(file.IsSubMenuOpen, "Shift+Alt+F opened the File menu");

            // Narrow, the menus fold into one ☰ menu that holds them.
            window.Width = 760;
            Assert.True(Ui.PumpUntil(() => bar.Items.Count == 1 && bar.Items.OfType<MenuItem>().First().Name == "Menu_Application", 5), "the menus did not fold");
            window.Width = 1440;
            Assert.True(Ui.PumpUntil(() => bar.Items.Count == 8, 5), "the menus did not come back");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ARightClickInTheTextPutsTheCaretThereAndOffersTheEditorsMenu()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "alpha beta\ngamma delta\n");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var tab = OpenByClicks(window, shell, "a.txt");
            var editor = tab.Editor!;
            var at = editor.RectOf(tab.Document.Text.LineStart(1) + 2).Center;
            var point = editor.TranslatePoint(at, window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Ui.Pump(0.2);
            Assert.Equal(tab.Document.Text.LineStart(1) + 2, tab.Document.Primary.Active);
            var items = window.GetVisualDescendants().OfType<MenuItem>().Where(m => m.IsEffectivelyVisible).Select(m => m.Name).ToList();
            Assert.Contains("Menu_editor.action.revealDefinition", items);
            Assert.Contains("Menu_editor.action.clipboardCopyAction", items);
            Assert.Contains("Menu_wicked.assistant.addSelectionToChat", items);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ATabsMenuActsOnThatTab()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(root, name), name);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            {
                var open = shell.Workbench.OpenAsync(Path.Combine(root, name));
                Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
            }

            // Right-click b.txt (c.txt is in front), Close Others: only b.txt is left.
            var tabB = window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tab") && b.Tag is Wicked.App.Workbench.EditorTab { Title: "b.txt" });
            var point = tabB.TranslatePoint(new Point(tabB.Bounds.Width / 2, tabB.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            MenuItem? closeOthers = null;
            Assert.True(Ui.PumpUntil(() => (closeOthers = window.GetVisualDescendants().OfType<MenuItem>().FirstOrDefault(m => m.Name == "Menu_workbench.action.closeOtherEditors" && m.IsEffectivelyVisible)) is not null, 5),
                "the tab's menu did not open");
            Input.Click(window, closeOthers!);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveGroup.Tabs.Count == 1, 5), "Close Others did not run");
            Assert.Equal("b.txt", shell.Workbench.ActiveGroup.Tabs[0].Title);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheStatusBarsIndentationItemPicksTabsForThisFile()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "x\n  y\n    z\n");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var tab = OpenByClicks(window, shell, "a.txt");
            // Two-space steps in the file: two spaces it is (editor.detectIndentation), whatever the settings say.
            Assert.Equal("Spaces: 2", shell.IndentText);

            Input.Click(window, window.FindControl<Button>("IndentStatus")!);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.IsOpen && shell.QuickInput.Items.Any(i => i.Label == "Indent Using Tabs"), 5), "no indentation actions");
            var tabs = shell.QuickInput.Items.First(i => i.Label == "Indent Using Tabs").Choose();
            Assert.True(Ui.PumpUntil(() => tabs.IsCompleted && shell.QuickInput.IsOpen && shell.QuickInput.Items.Count == 8, 5), "no widths offered");
            var four = shell.QuickInput.Items.First(i => i.Label == "4").Choose();
            Assert.True(Ui.PumpUntil(() => four.IsCompleted && shell.IndentText == "Tab Size: 4", 5), shell.IndentText);
            Assert.False(tab.Editor!.Options.InsertSpaces);
            window.Close();
            return 0;
        });
    }
}

/// <summary>Reading a file's indentation, and converting it.</summary>
public sealed class IndentationGuessTests
{
    [Theory]
    [InlineData("a\n\tb\n\t\tc\n", false, 0)]
    [InlineData("a\n  b\n    c\n  d\n", true, 2)]
    [InlineData("a\n    b\n        c\n    d\n", true, 4)]
    [InlineData("class A\n{\n    int b;\n    void C()\n    {\n        d();\n    }\n}\n", true, 4)]
    [InlineData("no indentation\nat all\n", null, 0)]
    public void TheFileSaysHowItIsIndented(string text, bool? spaces, int size)
    {
        var guess = IndentationGuess.Guess(new Document(text).Text);
        Assert.Equal(spaces, guess?.InsertSpaces);
        if (spaces is not null) Assert.Equal(size, guess!.Value.TabSize);
    }

    [Fact]
    public void IndentationConvertsBothWays()
    {
        var doc = new Document("a\n    b\n        c\n      d\n");
        Assert.True(Edits.ConvertIndentation(doc, toSpaces: false, 4));
        Assert.Equal("a\n\tb\n\t\tc\n\t  d\n", doc.Text.ToString());
        Assert.True(Edits.ConvertIndentation(doc, toSpaces: true, 4));
        Assert.Equal("a\n    b\n        c\n      d\n", doc.Text.ToString());
        Assert.False(Edits.ConvertIndentation(doc, toSpaces: true, 4));
    }
}
