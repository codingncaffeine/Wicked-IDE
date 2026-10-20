using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Wicked.Editor.View;

namespace Wicked.App.Editor;

/// <summary>Hands a finished <see cref="EditorFrame"/> to the render thread, which paints it with Skia.</summary>
internal sealed class EditorDrawOperation(Rect bounds, EditorFrame frame) : ICustomDrawOperation
{
    public Rect Bounds { get; } = bounds;

    public bool HitTest(Point p) => Bounds.Contains(p);

    public bool Equals(ICustomDrawOperation? other) => false;

    public void Dispose()
    {
    }

    public void Render(ImmediateDrawingContext context)
    {
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
        using var lease = feature.Lease();
        EditorPainter.Paint(lease.SkCanvas, frame);
    }
}
