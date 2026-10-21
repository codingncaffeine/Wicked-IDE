using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.Editor;
using Wicked.App.Language;
using Wicked.App.Shell;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Tests.Lsp;

/// <summary>Getting around (Phase 12): references and callers in their panel, the outline, peek, bookmarks — on a real clangd.</summary>
public sealed class NavigationWindowTests
{
    private static string? Clangd => new[] { "/usr/bin/clangd", "/usr/local/bin/clangd" }.FirstOrDefault(File.Exists);

    private const string Source = """
        static int twice(int value) { return value * 2; }

        static int four(void) { return twice(2); }

        int main(void) {
            return twice(four());
        }
        """;

    private static (App.Views.MainWindow Window, App.ViewModels.ShellViewModel Shell, App.Workbench.EditorTab Tab) Open(TempProfile profile)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.c"), Source);
        var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
        var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "main.c");
        Assert.True(Ui.PumpUntil(() => shell.Language.DocumentFor(tab)?.Client is not null, 30), "clangd did not take the document");
        return (window, shell, tab);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ShiftF12ListsTheReferencesInThePanelAndShiftAltHTheCallers()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab) = Open(profile);
            tab.Document.SetSelections([Selection.Caret(Source.IndexOf("twice", StringComparison.Ordinal) + 1)]);
            Input.Chord(window, Key.F12, RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => shell.BottomPanel.Active?.Id == PanelIds.References && shell.Language.References.Rows.OfType<ReferenceRow>().Count() == 3, 15), "no references");
            Assert.Equal("3 references to twice", shell.Language.References.Title);
            Assert.All(shell.Language.References.Rows.OfType<ReferenceRow>(), r => Assert.Equal("twice", r.Hit));

            Input.Chord(window, Key.H, RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.True(Ui.PumpUntil(() => shell.Language.References.Rows.OfType<CallRow>().Count() >= 3, 15), "no callers");
            Assert.Equal("Callers of twice", shell.Language.References.Title);
            Assert.Equal(["four", "main"], shell.Language.References.Rows.OfType<CallRow>().Where(c => c.Depth == 1).Select(c => c.Name).Order());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheOutlineListsTheFilesSymbolsAndAClickGoesThere()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab) = Open(profile);
            Input.Click(window, window.FindControl<Button>("RailOutline")!);
            Assert.True(shell.IsOutline);
            Assert.True(Ui.PumpUntil(() => shell.Outline.Rows.Count == 3, 15), "no outline");
            Assert.Equal(["twice", "four", "main"], shell.Outline.Rows.Select(r => r.Name));
            shell.Outline.OpenCommand.Execute(shell.Outline.Rows[2]);
            Assert.True(Ui.PumpUntil(() => tab.Document.Text.LineOf(tab.Document.Primary.Active) == 4, 5));
            shell.Outline.Filter = "fo";
            Assert.Equal(["four"], shell.Outline.Rows.Select(r => r.Name));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AltF12PeeksTheDefinitionWithoutLeavingTheFile()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab) = Open(profile);
            var call = Source.IndexOf("twice(four", StringComparison.Ordinal) + 1;
            tab.Document.SetSelections([Selection.Caret(call)]);
            Input.Chord(window, Key.F12, RawInputModifiers.Alt);
            var ui = shell.Language.UiFor(tab.Editor)!;
            Assert.True(Ui.PumpUntil(() => ui.PeekOpen && ui.PeekEditor is not null, 15), "no peek");
            var peek = ui.PeekEditor!;
            Assert.True(Ui.PumpUntil(() => peek.Document.Text.LineOf(peek.Document.Primary.Active) == 0, 5));
            Assert.True(peek.Document.IsReadOnly);
            Assert.Equal(call, tab.Document.Primary.Active);                                      // still here
            ui.ClosePeek();
            Assert.False(ui.PeekOpen);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task BookmarksMarkLinesFollowEditsAndCtrlAltLGoesToTheNext()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "notes.txt"), "one\ntwo\nthree\nfour\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "notes.txt");
            var doc = tab.Document;
            doc.SetSelections([Selection.Caret(doc.Text.LineStart(1))]);
            Input.Chord(window, Key.K, RawInputModifiers.Control | RawInputModifiers.Alt);
            doc.SetSelections([Selection.Caret(doc.Text.LineStart(3))]);
            Input.Chord(window, Key.K, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal(2, tab.Editor!.Decorations[Bookmarks.Layer].Gutter.Count(g => g.Glyph == GutterGlyph.Bookmark));

            // A line typed above both: they move with their lines.
            doc.SetSelections([Selection.Caret(0)]);
            window.KeyTextInput("zero");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Pump(0.05);
            Input.Chord(window, Key.L, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal("two", doc.Text.Line(doc.Text.LineOf(doc.Primary.Active)));
            Input.Chord(window, Key.L, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal("four", doc.Text.Line(doc.Text.LineOf(doc.Primary.Active)));
            Input.Chord(window, Key.L, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal("two", doc.Text.Line(doc.Text.LineOf(doc.Primary.Active)));           // wraps
            window.Close();
            return 0;
        });
    }
}
