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

    /// <summary>The editor's surface colours, from the window theme (the theme service sets them).</summary>
    public Wicked.Editor.View.EditorTheme EditorTheme { get; set; } = Wicked.Editor.View.EditorTheme.WickedDark;

    /// <summary>The window theme's own code colours: a bundled palette, or an imported theme's tokenColors.</summary>
    public ThemeName WindowCodeTheme { get; set; } = ThemeName.DarkPlus;

    public string? WindowCodeThemeJson { get; set; }

    /// <summary>Applies the editor-wide settings: the font size, line height and ligatures, and the code colours.</summary>
    public void Apply(WickedSettings settings)
    {
        var wanted = new EditorFontSettings((float)Math.Clamp(settings.FontSize, 6, 72), (float)Math.Clamp(settings.LineHeight, 1, 3), settings.FontLigatures);
        if (wanted != Fonts.Settings) Layout.Fonts = Fonts.With(wanted);
        // Code colours chosen apart from the window theme win (D28); empty follows the theme.
        var own = settings.CodeColorTheme.Length > 0;
        Syntax.Theme = own ? CodeTheme(settings.CodeColorTheme) : WindowCodeTheme;
        Syntax.CustomTheme = own ? null : WindowCodeThemeJson;
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
