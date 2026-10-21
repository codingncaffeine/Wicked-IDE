using Wicked.Editor.Layout;

namespace Wicked.App.Terminal;

/// <summary>The terminal's type: the editor's typeface at the terminal's size, no ligatures. Loaded on a worker, shared by every terminal.</summary>
public static class TerminalFonts
{
    private static readonly Lazy<Task<EditorFonts>> Base = new(() => Task.Run(() => EditorFonts.LoadDefault(new EditorFontSettings(14, 1f, false))));

    public static async Task<EditorFonts> GetAsync(double size)
    {
        var fonts = await Base.Value.ConfigureAwait(false);
        return fonts.With(new EditorFontSettings((float)Math.Clamp(size, 6, 72), 1f, false));
    }
}
