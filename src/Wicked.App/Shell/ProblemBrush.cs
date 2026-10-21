using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Wicked.App.Shell;

/// <summary>The muted text colour, or the theme's danger colour when a line is describing a problem.</summary>
public sealed class ProblemBrush : IValueConverter
{
    public static readonly ProblemBrush Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value is true ? "Brush.Danger" : "Brush.Text.Muted";
        return Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var brush) == true ? brush as IBrush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
