using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Wicked.App.Shell;

/// <summary>An icon's resource key (Icon.Folder, Icon.FileCode, ...) to its geometry.</summary>
public sealed class FileIcons : IValueConverter
{
    public static readonly FileIcons Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current?.TryGetResource(key, null, out var geometry) == true ? geometry as Geometry : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
