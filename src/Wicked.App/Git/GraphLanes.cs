using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Wicked.Core.Git;

namespace Wicked.App.Git;

/// <summary>
/// The lines and dot of one row of the commit graph, drawn in the row's own height: branches passing by, lines joining the dot
/// from above, lines leaving it for the parents below. The row is drawn from a finished <see cref="GraphRow"/>; no layout work
/// happens here, only strokes.
/// </summary>
public sealed class GraphLanes : Control
{
    public const double LaneWidth = 14;

    public static readonly StyledProperty<GraphRow?> RowProperty = AvaloniaProperty.Register<GraphLanes, GraphRow?>(nameof(Row));

    public static readonly StyledProperty<int> LanesProperty = AvaloniaProperty.Register<GraphLanes, int>(nameof(Lanes), 1);

    private IBrush[]? _palette;

    static GraphLanes()
    {
        AffectsRender<GraphLanes>(RowProperty);
        AffectsMeasure<GraphLanes>(LanesProperty);
    }

    public GraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    /// <summary>The widest row of the list in lanes, so every row's text starts at the same place.</summary>
    public int Lanes
    {
        get => GetValue(LanesProperty);
        set => SetValue(LanesProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new((Lanes * LaneWidth) + 8, double.IsInfinity(availableSize.Height) ? 24 : availableSize.Height);

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        _palette = null;
    }

    private IBrush Colour(int index)
    {
        if (_palette is null)
        {
            _palette = new IBrush[8];
            for (var i = 0; i < _palette.Length; i++)
            {
                _palette[i] = this.TryFindResource($"Brush.Graph.{i}", ActualThemeVariant, out var found) && found is IBrush b ? b : Brushes.Gray;
            }
        }

        return _palette[((index % 8) + 8) % 8];
    }

    private static double X(int lane) => 8 + (lane * LaneWidth);

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row) return;
        var height = Bounds.Height;
        var mid = height / 2;
        var node = X(row.Lane);
        foreach (var edge in row.Edges)
        {
            var pen = new Pen(Colour(edge.Color), 1.6, lineCap: PenLineCap.Round);
            double x1, y1, x2, y2;
            switch (edge.Span)
            {
                case GraphSpan.Through:
                    x1 = x2 = X(edge.FromLane);
                    y1 = -1;
                    y2 = height + 1;
                    break;
                case GraphSpan.Upper:
                    x1 = X(edge.FromLane);
                    y1 = -1;
                    x2 = node;
                    y2 = mid;
                    break;
                default:
                    x1 = node;
                    y1 = mid;
                    x2 = X(edge.ToLane);
                    y2 = height + 1;
                    break;
            }

            if (Math.Abs(x1 - x2) < 0.01)
            {
                context.DrawLine(pen, new Point(x1, y1), new Point(x2, y2));
                continue;
            }

            var curve = new StreamGeometry();
            using (var g = curve.Open())
            {
                g.BeginFigure(new Point(x1, y1), false);
                var dy = (y2 - y1) * 0.55;
                g.CubicBezierTo(new Point(x1, y1 + dy), new Point(x2, y2 - dy), new Point(x2, y2));
                g.EndFigure(false);
            }

            context.DrawGeometry(null, pen, curve);
        }

        var dot = Colour(row.Color);
        if (row.Commit.IsMerge) context.DrawEllipse(null, new Pen(dot, 2), new Point(node, mid), 4, 4);
        else context.DrawEllipse(dot, null, new Point(node, mid), 4.2, 4.2);
    }
}
