using Wicked.App.Editor;
using Wicked.App.Terminal;
using Wicked.App.Themes;

namespace Wicked.App.ViewModels;

/// <summary>The window theme: put in force with the settings, picked with a preview, imported from VS Code, hot-reloaded.</summary>
public sealed partial class ShellViewModel
{
    private ThemeService? _themes;
    private string? _themeApplied;

    /// <summary>The themes (made with the first settings: it reads the application's resources).</summary>
    public ThemeService Themes => _themes ??= MakeThemes();

    private ThemeService MakeThemes()
    {
        var service = new ThemeService(Paths);
        service.Applied += _ => Terminal?.ApplyTheme(TerminalTheme.FromResources());
        service.Watch(() => _ = ReloadThemesAsync());
        return service;
    }

    private async Task ReloadThemesAsync()
    {
        await Themes.LoadUserThemesAsync();
        _themeApplied = null; // the file in use may be the one that changed
        await ApplySettingsAsync(Settings);
        OnPropertyChanged(nameof(Themes));
    }

    /// <summary>The theme the settings name, when it is not the one in force (a whole repaint is not free).</summary>
    private void ApplyTheme(string name, EditorServices services)
    {
        if (_themeApplied == name) return;
        _themeApplied = name;
        Themes.Apply(Themes.Find(name), services);
    }

    /// <summary>Ctrl+K Ctrl+T: the themes, each shown as the selection moves, kept on Enter, the one before back on Escape.</summary>
    private void PickTheme()
    {
        var before = Settings.ColorTheme;
        async Task Preview(string name)
        {
            var services = await EditorServices.Ready;
            Themes.Apply(Themes.Find(name), services);
            _themeApplied = name;
            services.Apply(Settings);
            foreach (var tab in Workbench.AllTabs) tab.Editor?.ApplySettings(Settings);
        }

        var all = Themes.All;
        var items = all.Select(t => new QuickItem(t.Name, t.Path is null ? (t.IsDark ? "dark" : "light") + (t.Name == before ? " · current" : "") : "from config/themes", "",
            () => UpdateSettingsAsync(s => s with { ColorTheme = t.Name }),
            () => _ = Preview(t.Name))).ToList();
        items.Add(new QuickItem("Import a VS Code color theme…", "a theme .json file", "", ImportThemeAsync));
        var current = Math.Max(0, all.ToList().FindIndex(t => t.Name == before));
        QuickInput.OpenList("Select a color theme (arrows preview it)", items, current, () => _ = Preview(before));
        PaletteRequested?.Invoke("");
    }

    /// <summary>Import: a theme .json picked, copied into config/themes and put in force.</summary>
    private async Task ImportThemeAsync()
    {
        if (Workbench.Dialogs is not { } dialogs) return;
        var files = await dialogs.PickFilesAsync();
        if (files.FirstOrDefault(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) is not { } file) return;
        try
        {
            if (await Themes.ImportAsync(file) is { } name) await UpdateSettingsAsync(s => s with { ColorTheme = name });
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Notices.Post(new Wicked.Core.Notice("theme-import", $"{Path.GetFileName(file)} is not a theme that could be read: {ex.Message}", Wicked.Core.NoticeKind.Error));
        }
    }
}
