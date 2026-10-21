using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Wicked.App.Debug;

/// <summary>A row's depth as the left margin that indents it.</summary>
public sealed class IndentConverter : IValueConverter
{
    public double Step { get; set; } = 14;

    /// <summary>The margin every row has before its depth is added.</summary>
    public double Base { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        new Thickness(Base + (value is int depth ? depth * Step : 0), 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Small converters of the debugger's rows.</summary>
public static class DebugConverters
{
    /// <summary>True: semi-bold text.</summary>
    public static readonly IValueConverter BoldWhen = new FuncConverter<bool, FontWeight>(b => b ? FontWeight.SemiBold : FontWeight.Normal);

    /// <summary>True: drawn quieter.</summary>
    public static readonly IValueConverter DimWhen = new FuncConverter<bool, double>(b => b ? 0.6 : 1);

    private sealed class FuncConverter<TIn, TOut>(Func<TIn, TOut> convert) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is TIn v ? convert(v) : default(TOut);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}
