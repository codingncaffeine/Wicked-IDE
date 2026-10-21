using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using TextMateSharp.Grammars;
using Wicked.App.Editor;
using Wicked.Core;
using Wicked.Core.Themes;
using Wicked.Editor.View;

namespace Wicked.App.Themes;

/// <summary>
/// The window's themes (D16, D17): six built in, and every theme.json in the profile's config/themes (a VS Code colour
/// theme copied there imports as is). Applying one replaces every token brush the views take their colours from, sets
/// the controls' light or dark look, the editor's surface and the code colours, and the terminal's palette — at once, no
/// restart. Files are read on a worker; a change to the theme in use is applied again (hot reload).
/// </summary>
public sealed class ThemeService : IDisposable
{
    private static readonly string[] BuiltinFiles = ["wicked-dark", "wicked-light", "ember", "neon", "paper", "high-contrast"];

    private readonly string _folder;
    private readonly Dictionary<string, uint> _base;
    private FileSystemWatcher? _watcher;
    private List<WickedTheme> _user = [];

    public ThemeService(WickedPaths paths)
    {
        _folder = Path.Combine(paths.Config, "themes");
        Builtins = [.. BuiltinFiles.Select(ReadBuiltin)];
        // What Tokens.axaml defines: the floor every theme stands on (the terminal's palette, a key a theme forgets).
        _base = Application.Current?.Resources is { } resources ? ReadTokens(resources) : [];
    }

    public IReadOnlyList<WickedTheme> Builtins { get; }

    public IReadOnlyList<WickedTheme> All => [.. Builtins, .. _user];

    public WickedTheme Current { get; private set; } = null!;

    /// <summary>A theme was applied (the terminal and anything drawn from the tokens follow).</summary>
    public event Action<WickedTheme>? Applied;

    /// <summary>Where user themes live (Import copies there).</summary>
    public string Folder => _folder;

    private static WickedTheme ReadBuiltin(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Wicked.App.Themes.Builtin.{name}.json")
            ?? throw new InvalidOperationException($"The built-in theme {name} is missing");
        using var reader = new StreamReader(stream);
        return WickedTheme.Parse(reader.ReadToEnd());
    }

    private static Dictionary<string, uint> ReadTokens(IResourceDictionary resources)
    {
        var tokens = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var dictionary in resources.MergedDictionaries.OfType<IResourceDictionary>().Prepend(resources))
        {
            foreach (var (key, value) in dictionary)
            {
                if (key is string name && name.StartsWith("Brush.", StringComparison.Ordinal) && value is ISolidColorBrush brush) tokens[name] = brush.Color.ToUInt32();
            }
        }

        return tokens;
    }

    /// <summary>The user's themes, read again (on a worker); a file that does not parse is skipped with a warning.</summary>
    public async Task LoadUserThemesAsync()
    {
        var folder = _folder;
        _user = await Task.Run(() =>
        {
            var list = new List<WickedTheme>();
            if (!Directory.Exists(folder)) return list;
            foreach (var file in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    list.Add(WickedTheme.Parse(File.ReadAllText(file), file));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
                {
                    Log.Warn($"Theme {Path.GetFileName(file)} could not be read: {ex.Message}");
                }
            }

            return list;
        });
    }

    public WickedTheme Find(string name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? Builtins[0];

    /// <summary>The built-in theme a theme of this kind falls back on for what it does not say.</summary>
    private WickedTheme FallbackFor(WickedTheme theme) => theme.Kind switch
    {
        ThemeKind.Light => Builtins[1],
        ThemeKind.HighContrast => Builtins[5],
        _ => Builtins[0],
    };

    /// <summary>The tokens a theme gives the window, every key filled.</summary>
    public IReadOnlyDictionary<string, uint> TokensOf(WickedTheme theme)
    {
        var fallback = FallbackFor(theme);
        var floor = ReferenceEquals(fallback, theme) ? _base : fallback.Tokens(_base);
        return theme.Tokens(floor);
    }

    /// <summary>Puts a theme in force (UI thread): the token brushes, the controls' look, the editor's colours, the code colours.</summary>
    public void Apply(WickedTheme theme, EditorServices? editor)
    {
        if (Application.Current is not { } app) return;
        foreach (var (key, color) in TokensOf(theme)) app.Resources[key] = new SolidColorBrush(Color.FromUInt32(color));
        app.RequestedThemeVariant = theme.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (editor is not null)
        {
            editor.EditorTheme = (theme.IsDark ? EditorTheme.WickedDark : EditorTheme.Light).WithPalette(theme.Colors);
            editor.WindowCodeTheme = Enum.TryParse<ThemeName>(theme.CodeTheme, ignoreCase: true, out var code) ? code : theme.IsDark ? ThemeName.DarkPlus : ThemeName.LightPlus;
            editor.WindowCodeThemeJson = theme.TokenColorsJson is { } tokens ? $$"""{"name":{{System.Text.Json.JsonSerializer.Serialize(theme.Name)}},"tokenColors":{{tokens}}}""" : null;
        }

        Current = theme;
        Applied?.Invoke(theme);
    }

    /// <summary>Watches config/themes: a theme file saved is read again, and re-applied when it is the one in use.</summary>
    public void Watch(Action changed)
    {
        _watcher?.Dispose();
        try
        {
            Directory.CreateDirectory(_folder);
            _watcher = new FileSystemWatcher(_folder, "*.json") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler handler = (_, _) => Dispatcher.UIThread.Post(changed, DispatcherPriority.Background);
            _watcher.Changed += handler;
            _watcher.Created += handler;
            _watcher.Deleted += handler;
            _watcher.Renamed += (_, e) => handler(this, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Cannot watch the themes folder: {ex.Message}");
        }
    }

    /// <summary>Import: a VS Code colour theme (or a Wicked one) copied into config/themes; its name, once read.</summary>
    public async Task<string?> ImportAsync(string file)
    {
        var text = await File.ReadAllTextAsync(file);
        var theme = WickedTheme.Parse(text, file);
        Directory.CreateDirectory(_folder);
        var target = Path.Combine(_folder, Path.GetFileName(file));
        await Wicked.Core.Files.TextFile.WriteAsync(target, text, Wicked.Core.Files.FileEncoding.Utf8, CancellationToken.None);
        await LoadUserThemesAsync();
        return theme.Name;
    }

    public void Dispose() => _watcher?.Dispose();
}
