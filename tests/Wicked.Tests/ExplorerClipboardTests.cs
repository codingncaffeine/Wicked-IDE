using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Workbench;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>The Explorer's Cut, Copy and Paste of files and folders, and what its right-click menu offers.</summary>
public sealed class ExplorerClipboardTests
{
    [Fact]
    public async Task CopiesTakeAFreeNameCutsMoveOnceAndAFolderNeverGoesInsideItself()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        var sub = Path.Combine(root, "sub");
        Directory.CreateDirectory(Path.Combine(sub, "deep"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(sub, "deep", "x.txt"), "x");
        var notices = new Wicked.Core.NoticeBoard();
        var bench = new Workbench(notices);

        // Copied into its own folder twice: "a copy.txt", then "a copy 2.txt".
        bench.CopyFiles([Path.Combine(root, "a.txt")], cut: false);
        await bench.PasteFilesAsync(root);
        await bench.PasteFilesAsync(root);
        Assert.True(File.Exists(Path.Combine(root, "a copy.txt")));
        Assert.True(File.Exists(Path.Combine(root, "a copy 2.txt")));
        Assert.True(File.Exists(Path.Combine(root, "a.txt")));

        // A folder copies whole.
        bench.CopyFiles([sub], cut: false);
        await bench.PasteFilesAsync(root);
        Assert.Equal("x", File.ReadAllText(Path.Combine(root, "sub copy", "deep", "x.txt")));

        // Cut moves once: the clipboard is empty after.
        bench.CopyFiles([Path.Combine(root, "a copy.txt")], cut: true);
        await bench.PasteFilesAsync(sub);
        Assert.False(File.Exists(Path.Combine(root, "a copy.txt")));
        Assert.True(File.Exists(Path.Combine(sub, "a copy.txt")));
        Assert.Null(bench.FileClipboard);

        // A folder into itself: refused with a notice, nothing made.
        bench.CopyFiles([sub], cut: false);
        await bench.PasteFilesAsync(Path.Combine(sub, "deep"));
        Assert.False(Directory.Exists(Path.Combine(sub, "deep", "sub")));
        Assert.Contains(notices.All, n => n.Text.Contains("inside itself", StringComparison.Ordinal));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task CtrlCAndCtrlVInTheTreeCopyAFileAndTheMenuOffersTheTerminalAndCompare()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            Input.Click(window, Row(window, "a.txt").GetVisualDescendants().OfType<TextBlock>().First());
            Input.Chord(window, Key.C, RawInputModifiers.Control);
            Input.Chord(window, Key.V, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => File.Exists(Path.Combine(root, "a copy.txt")), 5), "Ctrl+C, Ctrl+V made no copy");

            // Right-click a.txt: Select for Compare; right-click b.txt: Compare with a.txt opens a diff tab.
            void RightClick(string name)
            {
                var row = Row(window, name).GetVisualDescendants().OfType<TextBlock>().First();
                var at = row.TranslatePoint(new Avalonia.Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseDown(at, MouseButton.Right);
                window.MouseUp(at, MouseButton.Right);
                Ui.Pump(0.2);
            }

            MenuItem Item(string header) =>
                window.GetVisualDescendants().OfType<MenuItem>().First(m => m.IsEffectivelyVisible && m.Header as string == header);

            RightClick("a.txt");
            Assert.NotNull(Item("Open in Integrated Terminal"));
            Input.Click(window, Item("Select for Compare"));
            RightClick("b.txt");
            Input.Click(window, Item("Compare with a.txt"));
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Title == "a.txt ↔ b.txt", 5), "no comparison opened");
            window.Close();
            return 0;
        });
    }
}
