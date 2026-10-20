using TextMateSharp.Grammars;
using Wicked.Core;
using Wicked.Core.Settings;
using Wicked.Editor.Layout;
using Wicked.Editor.Syntax;

namespace Wicked.App.Editor;

/// <summary>
/// What every editor in the window shares: the fonts, the layout thread and the syntax thread. Started on a worker at
/// launch (reading the fonts touches the disk); an editor opened before that finishes waits for <see cref="Ready"/>.
/// </summary>
public sealed class EditorServices : IDisposable
{
    private EditorServices(EditorFonts fonts)
    {
        Layout = new LayoutService(fonts);
        Syntax = new SyntaxService();
    }

    /// <summary>The fonts in use (the layout thread's); a size or ligature change replaces them.</summary>
    public EditorFonts Fonts => Layout.Fonts;

    public LayoutService Layout { get; }

    public SyntaxService Syntax { get; }

    /// <summary>The services, once started; any thread may await it.</summary>
    public static Task<EditorServices> Ready => _ready.Value;

    private static readonly Lazy<Task<EditorServices>> _ready = new(() => Task.Run(() =>
    {
        var services = new EditorServices(EditorFonts.LoadDefault());
        Log.Info($"Editor ready: {services.Fonts.Regular.FamilyName} {services.Fonts.Size} px, line height {services.Fonts.LineHeight} px");
        return services;
    }));

    /// <summary>Applies the editor-wide settings: the font size, line height and ligatures, and the code colours.</summary>
    public void Apply(WickedSettings settings)
    {
        var wanted = new EditorFontSettings((float)Math.Clamp(settings.FontSize, 6, 72), (float)Math.Clamp(settings.LineHeight, 1, 3), settings.FontLigatures);
        if (wanted != Fonts.Settings) Layout.Fonts = Fonts.With(wanted);
        Syntax.Theme = CodeTheme(settings.CodeColorTheme);
    }

    /// <summary>The code colour theme a setting names; empty (the window theme's own) is Dark+, which Wicked Dark is built around.</summary>
    public static ThemeName CodeTheme(string name) => Enum.TryParse<ThemeName>(name, ignoreCase: true, out var theme) ? theme : ThemeName.DarkPlus;

    /// <summary>Starts the services in the background now, so the first file opens without waiting.</summary>
    public static void Start() => _ = Ready;

    public void Dispose()
    {
        Layout.Dispose();
        Syntax.Dispose();
    }
}
