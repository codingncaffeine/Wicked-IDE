using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using Wicked.Core;

namespace Wicked.App.Themes;

/// <summary>
/// Style packs (D16 layer 2): every <c>.axaml</c> in the profile's config/styles, a <c>&lt;Styles&gt;</c> of Avalonia
/// styles over Wicked's own, read when the window starts and again whenever one is saved (hot reload). A pack that
/// does not parse is left out and its error, with the line, goes to the Output panel's "Styles" channel.
/// </summary>
public sealed class StylePacks : IDisposable
{
    private readonly string _folder;
    private readonly Action<string> _report;
    private readonly Dictionary<string, IStyle> _loaded = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _settle;

    public StylePacks(WickedPaths paths, Action<string> report)
    {
        _folder = Path.Combine(paths.Config, "styles");
        _report = report;
    }

    public string Folder => _folder;

    /// <summary>The packs in force, by file name (tests).</summary>
    public IReadOnlyCollection<string> Loaded => _loaded.Keys;

    /// <summary>Reads every pack (the files on a worker, the parse on the UI thread, where styles are made).</summary>
    public async Task LoadAsync()
    {
        var folder = _folder;
        var files = await Task.Run(() => Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.axaml").Order(StringComparer.Ordinal).Select(f => (Path: f, Text: File.ReadAllText(f))).ToList()
            : []);
        if (Application.Current is not { } app) return;
        foreach (var old in _loaded.Values) app.Styles.Remove(old);
        _loaded.Clear();
        foreach (var (path, text) in files)
        {
            try
            {
                var parsed = AvaloniaRuntimeXamlLoader.Parse<object>(text, typeof(StylePacks).Assembly);
                if (parsed is not IStyle style)
                {
                    _report($"{Path.GetFileName(path)}: the root element must be <Styles> (or a <Style>), not {parsed?.GetType().Name}");
                    continue;
                }

                app.Styles.Add(style);
                _loaded[Path.GetFileName(path)] = style;
            }
            catch (Exception ex) when (ex is XamlLoadException or System.Xml.XmlException or InvalidOperationException or ArgumentException or InvalidCastException)
            {
                _report($"{Path.GetFileName(path)}: {ex.Message}");
                Log.Warn($"Style pack {Path.GetFileName(path)} was left out: {ex.Message}");
            }
        }
    }

    /// <summary>Watches config/styles: a pack saved, added or removed is read again a moment later.</summary>
    public void Watch()
    {
        _watcher?.Dispose();
        try
        {
            Directory.CreateDirectory(_folder);
            _watcher = new FileSystemWatcher(_folder, "*.axaml") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _settle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
                {
                    _settle!.Stop();
                    _ = LoadAsync();
                });
                _settle.Stop();
                _settle.Start();
            });
            _watcher.Changed += changed;
            _watcher.Created += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, e) => changed(this, e);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Cannot watch the styles folder: {ex.Message}");
        }
    }

    public void Dispose() => _watcher?.Dispose();
}
