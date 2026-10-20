using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Wicked.Core;

namespace Wicked.App.Shell;

/// <summary>A notice kind's colour, taken from the theme's tokens so a theme recolours the footer too.</summary>
public sealed class NoticeBrush : IValueConverter
{
    public static readonly NoticeBrush Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            NoticeKind.Error => "Brush.Danger",
            NoticeKind.Warning => "Brush.Warning",
            NoticeKind.Success => "Brush.Success",
            NoticeKind.Action or NoticeKind.Progress => "Brush.Accent",
            _ => "Brush.Text.Muted",
        };
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true
            ? brush as IBrush
            : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
