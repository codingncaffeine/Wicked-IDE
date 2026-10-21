using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Wicked.App.Chat;

/// <summary>The monospaced face the chat's code is set in: the editor's own JetBrains Mono, registered with Avalonia once.</summary>
internal static class ChatFonts
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    /// <summary>The code face; falls back to the system's monospace if the font could not be registered.</summary>
    public static FontFamily Mono { get; private set; } = new("monospace");

    /// <summary>Called once the app exists (fonts are an app-level service).</summary>
    public static void Register()
    {
        lock (Gate)
        {
            if (_registered) return;
            _registered = true;
            try
            {
                FontManager.Current.AddFontCollection(new EmbeddedFontCollection(new Uri("fonts:WickedMono", UriKind.Absolute), new Uri("avares://wicked/Assets/Fonts", UriKind.Absolute)));
                Mono = new FontFamily("fonts:WickedMono#JetBrains Mono");
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
            {
                Wicked.Core.Log.Warn($"The code font could not be registered: {ex.Message}");
            }
        }
    }
}
