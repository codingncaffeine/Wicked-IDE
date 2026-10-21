using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Wicked.App.Assistant;

/// <summary>An icon's resource key ("Icon.Search") as its geometry, for views that pick the icon from data.</summary>
public sealed class IconKey : IValueConverter
{
    public static readonly IconKey Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryFindResource(key, out var found) == true ? found as Geometry : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
