using Wicked.Core;
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
        Fonts = fonts;
        Layout = new LayoutService(fonts);
        Syntax = new SyntaxService();
    }

    public EditorFonts Fonts { get; }

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

    /// <summary>Starts the services in the background now, so the first file opens without waiting.</summary>
    public static void Start() => _ = Ready;

    public void Dispose()
    {
        Layout.Dispose();
        Syntax.Dispose();
    }
}
