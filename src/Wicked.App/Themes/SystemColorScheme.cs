using Avalonia;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Wicked.App.Themes;

/// <summary>Whether the desktop asks for dark or light, and when that changes (window.autoDetectColorScheme follows it).</summary>
public interface ISystemColorScheme
{
    /// <summary>True for dark, false for light; null when the platform does not say.</summary>
    bool? PrefersDark { get; }

    /// <summary>The desktop's choice changed (raised on the UI thread).</summary>
    event Action? Changed;
}

/// <summary>
/// The desktop's light or dark preference as Avalonia reads it: the freedesktop portal's <c>org.freedesktop.appearance
/// color-scheme</c> on Linux (GNOME, KDE and the rest), the "apps use light theme" setting on Windows.
/// </summary>
public sealed class PlatformColorScheme : ISystemColorScheme
{
    private IPlatformSettings? _settings;

    public bool? PrefersDark => Settings?.GetColorValues().ThemeVariant switch
    {
        PlatformThemeVariant.Dark => true,
        PlatformThemeVariant.Light => false,
        _ => null,
    };

    public event Action? Changed;

    private IPlatformSettings? Settings
    {
        get
        {
            if (_settings is null && Application.Current?.PlatformSettings is { } settings)
            {
                _settings = settings;
                settings.ColorValuesChanged += (_, _) => Dispatcher.UIThread.Post(() => Changed?.Invoke());
            }

            return _settings;
        }
    }
}
