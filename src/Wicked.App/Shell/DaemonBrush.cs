using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Wicked.App.Shell;

/// <summary>The footer's daemon dot: green while it answers, muted while it does not or before the first look.</summary>
public sealed class DaemonBrush : IValueConverter
{
    public static readonly DaemonBrush Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "Brush.Success" : "Brush.Text.Muted";
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true
            ? brush as IBrush
            : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
