using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;

namespace Wicked.App.Terminal;

/// <summary>Hands a finished frame to the render thread, which paints it with Skia.</summary>
internal sealed class TerminalDrawOperation(Rect bounds, TerminalPaintState state) : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => Bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    /// <summary>Milliseconds each paint took, for tests; painting belongs to the render thread, not the UI thread.</summary>
    internal static Action<double>? PaintSlice { get; set; }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        var started = PaintSlice is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        using var lease = feature.Lease();
        TerminalPainter.Paint(lease.SkCanvas, state);
        if (PaintSlice is { } slice) slice(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
}
