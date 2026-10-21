using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Wicked.App.Workbench;

namespace Wicked.App.Views;

/// <summary>
/// Dragging a tab, as VS Code's tabs drag: along a strip to reorder, onto another group's strip or middle to move, onto a
/// group's edge to split it and move there. The target is found from the groups' bounds (not the hit test), and the half
/// or whole the tab would take is shown while dragging.
/// </summary>
public partial class EditorGroupView
{
    private const double DragThreshold = 6;
    private EditorTab? _pressed;
    private Point _pressedAt;
    private bool _dragging;

    /// <summary>The group this view shows.</summary>
    internal EditorGroup? Group => _group;

    private void InitDrag()
    {
        TabStrip.AddHandler(PointerPressedEvent, OnTabPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        TabStrip.AddHandler(PointerMovedEvent, OnTabMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private static EditorTab? TabAt(object? source)
    {
        if (source is not Visual visual || visual.FindAncestorOfType<Button>(includeSelf: true) is not null) return null;
        var border = visual.FindAncestorOfType<Border>(includeSelf: true);
        while (border is not null && !border.Classes.Contains("tab")) border = border.FindAncestorOfType<Border>();
        return border?.Tag as EditorTab;
    }

    private void OnTabPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragging = false;
        _pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed ? TabAt(e.Source) : null;
        if (_pressed is not null && TopLevel.GetTopLevel(this) is { } top) _pressedAt = e.GetPosition(top);
    }

    private void OnTabMoved(object? sender, PointerEventArgs e)
    {
        if (_pressed is null || TopLevel.GetTopLevel(this) is not { } top) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = null;
            return;
        }

        var at = e.GetPosition(top);
        if (!_dragging)
        {
            if (Math.Abs(at.X - _pressedAt.X) < DragThreshold && Math.Abs(at.Y - _pressedAt.Y) < DragThreshold) return;
            _dragging = true;
            e.Pointer.Capture(TabStrip);
        }

        var (target, zone, _) = DropAt(top, at);
        foreach (var view in Views(top)) view.ShowZone(view == target ? zone : null);
    }

    /// <summary>The pointer let go: a drag in progress is dropped (true), else the release is an ordinary click (false).</summary>
    private bool FinishDrag(PointerReleasedEventArgs e)
    {
        var tab = _pressed;
        var dragging = _dragging;
        _pressed = null;
        _dragging = false;
        if (!dragging || tab is null || _group is null || TopLevel.GetTopLevel(this) is not { } top) return false;
        e.Pointer.Capture(null);
        var (target, zone, index) = DropAt(top, e.GetPosition(top));
        foreach (var view in Views(top)) view.ShowZone(null);
        if (target?.Group is { } group) _group.Workbench.DropTab(tab, _group, group, zone, index);
        e.Handled = true;
        return true;
    }

    private static IEnumerable<EditorGroupView> Views(TopLevel top) =>
        top.GetVisualDescendants().OfType<EditorGroupView>().Where(v => v.IsEffectivelyVisible);

    /// <summary>The group view under a point, and where in it: its tab strip (with the place to insert), its middle or an edge.</summary>
    internal static (EditorGroupView? View, TabDropZone Zone, int Index) DropAt(TopLevel top, Point at)
    {
        foreach (var view in Views(top))
        {
            if (view.TranslatePoint(default, top) is not { } origin) continue;
            var bounds = new Rect(origin, view.Bounds.Size);
            if (!bounds.Contains(at)) continue;
            var strip = view.TabStrip.TranslatePoint(default, top) is { } stripAt ? new Rect(stripAt, view.TabStrip.Bounds.Size) : default;
            var stripBand = new Rect(bounds.X, bounds.Y, bounds.Width, Math.Max(strip.Bottom - bounds.Y, 30));
            if (stripBand.Contains(at)) return (view, TabDropZone.Strip, view.InsertIndex(top, at.X));

            // Below the strip: the outer quarters are edges (the nearest one wins), the rest the middle.
            var area = new Rect(bounds.X, stripBand.Bottom, bounds.Width, Math.Max(1, bounds.Bottom - stripBand.Bottom));
            var x = (at.X - area.X) / area.Width;
            var y = (at.Y - area.Y) / area.Height;
            var edges = new (double Distance, TabDropZone Zone)[] { (x, TabDropZone.Left), (1 - x, TabDropZone.Right), (y, TabDropZone.Up), (1 - y, TabDropZone.Down) };
            var nearest = edges.MinBy(e => e.Distance);
            return (view, nearest.Distance < 0.25 ? nearest.Zone : TabDropZone.Middle, -1);
        }

        return (null, TabDropZone.Middle, -1);
    }

    /// <summary>Where on this strip a tab let go at <paramref name="x"/> goes: before the first tab whose middle is to its right.</summary>
    private int InsertIndex(TopLevel top, double x)
    {
        var tabs = TabStrip.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("tab") && b.IsEffectivelyVisible).ToList();
        for (var i = 0; i < tabs.Count; i++)
        {
            if (tabs[i].TranslatePoint(new Point(tabs[i].Bounds.Width / 2, 0), top) is { } middle && x < middle.X) return i;
        }

        return tabs.Count;
    }

    /// <summary>Shows the part of this group a dragged tab would take, or nothing.</summary>
    private void ShowZone(TabDropZone? zone)
    {
        DropZone.IsVisible = zone is { } z && z != TabDropZone.Strip;
        if (!DropZone.IsVisible) return;
        var width = Host.Bounds.Width;
        var height = Host.Bounds.Height;
        (DropZone.HorizontalAlignment, DropZone.VerticalAlignment, DropZone.Width, DropZone.Height) = zone switch
        {
            TabDropZone.Left => (HorizontalAlignment.Left, VerticalAlignment.Stretch, width / 2, double.NaN),
            TabDropZone.Right => (HorizontalAlignment.Right, VerticalAlignment.Stretch, width / 2, double.NaN),
            TabDropZone.Up => (HorizontalAlignment.Stretch, VerticalAlignment.Top, double.NaN, height / 2),
            TabDropZone.Down => (HorizontalAlignment.Stretch, VerticalAlignment.Bottom, double.NaN, height / 2),
            _ => (HorizontalAlignment.Stretch, VerticalAlignment.Stretch, double.NaN, double.NaN),
        };
    }
}
