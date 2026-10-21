using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Wicked.Core;

namespace Wicked.App.Previews;

/// <summary>
/// An image opened from the explorer: decoded on a worker, fitted to the tab (a click shows it at its own size and back),
/// with its size in pixels and on disk underneath, as VS Code's image preview does.
/// </summary>
public sealed class ImagePreviewView : UserControl
{
    /// <summary>The kinds of image the preview opens (what the drawing library decodes); anything else opens as text or is refused.</summary>
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico" };

    private readonly Image _image = new() { Name = "PreviewImage", Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
    private readonly TextBlock _info = new() { Name = "PreviewInfo", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8), Classes = { "muted" } };
    private readonly ScrollViewer _scroll;

    public ImagePreviewView(string path)
    {
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = _image,
        };
        Content = new DockPanel { Children = { _info, _scroll } };
        DockPanel.SetDock(_info, Dock.Bottom);
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        _image.Cursor = new Cursor(StandardCursorType.Hand);
        _image.PointerPressed += (_, _) => Fit = !Fit;
        _ = LoadAsync(path);
    }

    /// <summary>Fitted to the tab (true) or at the image's own size.</summary>
    public bool Fit
    {
        get => _image.Stretch == Stretch.Uniform;
        set => _image.Stretch = value ? Stretch.Uniform : Stretch.None;
    }

    /// <summary>The decoded image, once it is loaded.</summary>
    public Bitmap? Bitmap => _image.Source as Bitmap;

    private async Task LoadAsync(string path)
    {
        try
        {
            var (bitmap, bytes) = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                return (new Bitmap(stream), stream.Length);
            });
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _image.Source = bitmap;
                _info.Text = string.Create(CultureInfo.InvariantCulture, $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}    {Size(bytes)}");
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Log.Warn($"The image {path} could not be shown: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => _info.Text = $"This image could not be shown: {ex.Message}");
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
    };
}
