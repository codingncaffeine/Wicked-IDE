using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Views;
using Wicked.App.Workbench;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>The editor grid: splits any way, neighbours by direction, moving editors, closing and maximising groups.</summary>
public sealed class EditorGridTests
{
    [Fact]
    public void SplitsMakeRowsAndColumnsAndNeighboursFollowThem()
    {
        var bench = new Workbench(new Wicked.Core.NoticeBoard());
        var a = bench.ActiveGroup;
        var b = bench.Split(SplitDirection.Right, copyActive: false); // a | b
        var c = bench.Split(SplitDirection.Down, copyActive: false);  // a | (b over c)
        Assert.Equal([a, b, c], bench.Layout.Groups);
        Assert.Equal([a, b, c], bench.Groups);

        Assert.Equal(b, bench.Layout.Neighbour(a, SplitDirection.Right));
        Assert.Equal(a, bench.Layout.Neighbour(c, SplitDirection.Left));
        Assert.Equal(b, bench.Layout.Neighbour(c, SplitDirection.Up));
        Assert.Equal(c, bench.Layout.Neighbour(b, SplitDirection.Down));
        Assert.Null(bench.Layout.Neighbour(a, SplitDirection.Left));
        Assert.Null(bench.Layout.Neighbour(c, SplitDirection.Down));

        // Splitting left of b in its column makes a row inside it: a | ((d | b) over c).
        bench.Focus(b);
        var d = bench.Split(SplitDirection.Left, copyActive: false);
        Assert.Equal([a, d, b, c], bench.Layout.Groups);
        Assert.Equal(d, bench.Layout.Neighbour(b, SplitDirection.Left));
        Assert.Equal(a, bench.Layout.Neighbour(d, SplitDirection.Left));

        // Taking b out leaves d alone in that row, which becomes the row itself.
        bench.Layout.Remove(b);
        var column = Assert.IsType<LayoutBranch>(Assert.IsType<LayoutBranch>(bench.Layout.Root).Children[1]);
        Assert.False(column.Horizontal);
        Assert.Equal(d, Assert.IsType<LayoutLeaf>(column.Children[0]).Group);
    }

    [Fact]
    public void ARowLeftInsideARowJoinsIt()
    {
        var bench = new Workbench(new Wicked.Core.NoticeBoard());
        var a = bench.ActiveGroup;
        var b = bench.Split(SplitDirection.Right, copyActive: false); // a | b
        var c = bench.Split(SplitDirection.Down, copyActive: false);  // a | (b over c)
        var d = bench.Split(SplitDirection.Right, copyActive: false); // a | (b over (c | d))

        // Without b the column holds only the row c | d, which joins the outer row: a | c | d, as VS Code's grid flattens.
        bench.Layout.Remove(b);
        var row = Assert.IsType<LayoutBranch>(bench.Layout.Root);
        Assert.True(row.Horizontal);
        Assert.Equal([a, c, d], row.Children.Select(n => Assert.IsType<LayoutLeaf>(n).Group));
        Assert.All(row.Children, n => Assert.Same(row, n.Parent));
        Assert.Equal(2, row.Children.Sum(n => n.Size), 6);
        Assert.Equal(c, bench.Layout.Neighbour(a, SplitDirection.Right));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task SplittingFocusingMovingAndMaximisingThroughTheKeys()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            void Keys(params (Key Key, RawInputModifiers Mods)[] presses)
            {
                foreach (var (key, mods) in presses) Input.Chord(window, key, mods);
                Ui.Pump(0.2);
            }

            int Shown() => window.GetVisualDescendants().OfType<EditorGroupView>().Count(v => v.IsEffectivelyVisible);
            var ctrl = RawInputModifiers.Control;
            OpenByClicks(window, shell, "a.txt");
            var first = bench.ActiveGroup;

            // Ctrl+\ splits right with the same file; Ctrl+K Ctrl+\ splits that one down.
            Keys((Key.OemPipe, ctrl));
            var right = bench.ActiveGroup;
            Keys((Key.K, ctrl), (Key.OemPipe, ctrl));
            var below = bench.ActiveGroup;
            Assert.Equal(3, bench.Groups.Count);
            Assert.Equal(3, Shown());
            Assert.Equal("a.txt", below.Active!.Title);

            // Ctrl+K Ctrl+Arrow moves the keyboard between them.
            Keys((Key.K, ctrl), (Key.Up, ctrl));
            Assert.Same(right, bench.ActiveGroup);
            Keys((Key.K, ctrl), (Key.Left, ctrl));
            Assert.Same(first, bench.ActiveGroup);

            // b.txt opened on the left, then Ctrl+Alt+Right moves it, its editor and all, into the next group.
            var open = bench.OpenAsync(Path.Combine(root, "b.txt"));
            Assert.True(Ui.PumpUntil(() => open.IsCompleted && open.Result?.Editor is not null, 5));
            var b = open.Result!;
            var editor = b.Editor;
            Keys((Key.Right, ctrl | RawInputModifiers.Alt));
            Assert.Contains(b, right.Tabs);
            Assert.DoesNotContain(b, first.Tabs);
            Assert.Same(editor, b.Editor);
            Assert.True(Ui.PumpUntil(() => editor!.IsAttachedToVisualTree(), 5), "the moved editor is not shown");

            // Ctrl+K Ctrl+M shows the active group alone, and again shows them all.
            Keys((Key.K, ctrl), (Key.M, ctrl));
            Assert.Equal(1, Shown());
            Keys((Key.K, ctrl), (Key.M, ctrl));
            Assert.Equal(3, Shown());
            // The keyboard stays where it was: drawing the groups again does not hand it to another one.
            Assert.Same(right, bench.ActiveGroup);
            Assert.True(Ui.PumpUntil(() => b.Editor!.IsFocused, 5), "the active editor lost the keyboard");

            // The group below emptied (its only tab closed) leaves the grid.
            bench.Focus(below);
            var close = bench.CloseAsync(below.Active!, below);
            Assert.True(Ui.PumpUntil(() => close.IsCompleted, 5));
            Assert.Equal(2, bench.Groups.Count);
            Assert.True(Ui.PumpUntil(() => Shown() == 2, 5));

            // An empty split shows no welcome page of its own: that is the first group's alone.
            bench.Split(SplitDirection.Right, copyActive: false);
            Assert.True(Ui.PumpUntil(() => Shown() == 3, 5));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<StackPanel>(), p => p.Name == "Welcome" && p.IsEffectivelyVisible);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public void JoiningGroupsKeepsTheActiveEditorAndEvenSizesShareTheRoom()
    {
        using var profile = new TempProfile();
        var bench = new Workbench(new Wicked.Core.NoticeBoard());
        var a = bench.ActiveGroup;
        var b = bench.Split(SplitDirection.Right, copyActive: false);
        var c = bench.Split(SplitDirection.Down, copyActive: false);
        foreach (var (group, name) in new[] { (a, "a.txt"), (b, "b.txt"), (c, "c.txt") })
        {
            group.Add(new EditorTab(Path.Combine(profile.Root, name), new Wicked.Editor.Model.Document(name), null, Wicked.Core.Files.FileEncoding.Utf8, DateTime.UtcNow));
        }

        // Sizes dragged unevenly come back even, and the view is told to draw them rather than read back its own.
        Assert.IsType<LayoutBranch>(bench.Layout.Root).Children[0].Size = 1.6;
        bench.EvenGroupSizes();
        Assert.All(Assert.IsType<LayoutBranch>(bench.Layout.Root).Children, n => Assert.Equal(1, n.Size));
        Assert.True(bench.Layout.SizesReset);

        // b joined with the next group (c): c's editor joins b's, and b's editor is still the one in front.
        bench.Focus(b);
        bench.JoinWithNextGroup();
        Assert.Equal([a, c], bench.Groups);
        Assert.Equal(["c.txt", "b.txt"], c.Tabs.Select(t => t.Title));
        Assert.Equal("b.txt", bench.ActiveTab!.Title);

        // Everything into a: one group left.
        bench.Focus(a);
        bench.JoinAllGroups();
        Assert.Equal([a], bench.Groups);
        Assert.Equal(3, a.Tabs.Count);
        Assert.Equal("a.txt", bench.ActiveTab!.Title);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TabsMoveAlongTheirStripAndAltPicksThemByPlace()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(root, name), name);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            {
                var open = bench.OpenAsync(Path.Combine(root, name));
                Assert.True(Ui.PumpUntil(() => open.IsCompleted && open.Result?.Editor is not null, 5));
            }

            var group = bench.ActiveGroup;
            string Titles() => string.Join(",", group.Tabs.Select(t => t.Title));
            void Press(Key key, RawInputModifiers mods)
            {
                Input.Chord(window, key, mods);
                Ui.Pump(0.2);
            }

            // Alt+1 picks the first tab, Alt+0 the last, with the caret in an editor.
            Press(Key.D1, RawInputModifiers.Alt);
            Assert.Equal("a.txt", bench.ActiveTab!.Title);
            Press(Key.D0, RawInputModifiers.Alt);
            Assert.Equal("c.txt", bench.ActiveTab!.Title);

            // Ctrl+Shift+PageUp moves c.txt left twice (to the front, no further); Ctrl+Shift+PageDown one back.
            var ctrlShift = RawInputModifiers.Control | RawInputModifiers.Shift;
            Press(Key.PageUp, ctrlShift);
            Assert.Equal("a.txt,c.txt,b.txt", Titles());
            Press(Key.PageUp, ctrlShift);
            Press(Key.PageUp, ctrlShift);
            Assert.Equal("c.txt,a.txt,b.txt", Titles());
            Press(Key.PageDown, ctrlShift);
            Assert.Equal("a.txt,c.txt,b.txt", Titles());
            Assert.Equal("c.txt", bench.ActiveTab!.Title);
            window.Close();
            return 0;
        });
    }
}

/// <summary>Dragging tabs with the mouse: along the strip, into another group, onto an edge to split.</summary>
public sealed class TabDragTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task ATabDraggedReordersMovesAndSplits()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(root, name), name);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            {
                var open = bench.OpenAsync(Path.Combine(root, name));
                Assert.True(Ui.PumpUntil(() => open.IsCompleted, 5));
            }

            var group = bench.ActiveGroup;
            string Titles(EditorGroup g) => string.Join(",", g.Tabs.Select(t => t.Title));
            Border TabOf(string title) => window.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("tab") && b.Tag is EditorTab t && t.Title == title && b.IsEffectivelyVisible);
            Avalonia.Point Centre(Control c) => c.TranslatePoint(new Avalonia.Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;
            void Drag(Avalonia.Point from, Avalonia.Point to)
            {
                window.MouseDown(from, MouseButton.Left);
                for (var i = 1; i <= 6; i++) window.MouseMove(new Avalonia.Point(from.X + (to.X - from.X) * i / 6, from.Y + (to.Y - from.Y) * i / 6), RawInputModifiers.LeftMouseButton);
                Ui.Pump(0.05);
                window.MouseUp(to, MouseButton.Left);
                Ui.Pump(0.2);
            }

            // c dragged to the front of the strip.
            var aTab = TabOf("a.txt");
            Drag(Centre(TabOf("c.txt")), aTab.TranslatePoint(new Avalonia.Point(4, aTab.Bounds.Height / 2), window)!.Value);
            Assert.Equal("c.txt,a.txt,b.txt", Titles(group));

            // b dragged to the right edge of the editor: a new group to the right takes it.
            var view = window.GetVisualDescendants().OfType<EditorGroupView>().Single(v => v.IsEffectivelyVisible);
            var host = view.TranslatePoint(new Avalonia.Point(view.Bounds.Width - 10, view.Bounds.Height / 2), window)!.Value;
            Drag(Centre(TabOf("b.txt")), host);
            Assert.Equal(2, bench.Groups.Count);
            Assert.Equal("c.txt,a.txt", Titles(bench.Groups[0]));
            Assert.Equal("b.txt", Titles(bench.Groups[1]));

            // a dragged into the middle of the right group joins it.
            var rightView = window.GetVisualDescendants().OfType<EditorGroupView>().Single(v => v.IsEffectivelyVisible && v.DataContext == bench.Groups[1]);
            Drag(Centre(TabOf("a.txt")), Centre(rightView));
            Assert.Equal("c.txt", Titles(bench.Groups[0]));
            Assert.Equal("b.txt,a.txt", Titles(bench.Groups[1]));
            window.Close();
            return 0;
        });
    }
}
