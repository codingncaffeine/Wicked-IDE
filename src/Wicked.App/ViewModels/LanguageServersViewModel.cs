using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Lsp;

namespace Wicked.App.ViewModels;

/// <summary>One server in Settings → Languages: what it serves, whether it is here, and the way to install it.</summary>
public sealed partial class ServerRow(LanguageServerSpec spec, Func<LanguageServerSpec, Task> install) : ObservableObject
{
    public LanguageServerSpec Spec { get; } = spec;

    public string Name => Spec.Name;

    public string Languages => string.Join(", ", Spec.Languages);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string _state = "Looking…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _installing;

    /// <summary>Why Wicked cannot install it here (a system package, a missing npm), shown instead of the button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string? _blocked;

    public bool CanInstall => !Installed && !Installing && Blocked is null;

    [RelayCommand]
    private async Task Install()
    {
        Installing = true;
        State = "Installing…";
        try
        {
            await install(Spec);
        }
        finally
        {
            Installing = false;
        }
    }
}

/// <summary>Settings → Languages: every server the catalog knows, found or not, read on a worker when the sheet opens.</summary>
public sealed partial class LanguageServersViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public LanguageServersViewModel(ShellViewModel shell)
    {
        _shell = shell;
        foreach (var spec in LanguageServerCatalog.All) Servers.Add(new ServerRow(spec, InstallAsync));
    }

    public ObservableCollection<ServerRow> Servers { get; } = [];

    public IReadOnlyList<SettingChoice> InlayModes { get; } =
    [
        new("on", "On"),
        new("off", "Off"),
    ];

    private async Task InstallAsync(LanguageServerSpec spec)
    {
        await _shell.Language.InstallAsync(spec);
        await RefreshAsync();
    }

    /// <summary>Servers that extensions added (or took back) since the sheet was made.</summary>
    private void SyncWithCatalog()
    {
        var all = LanguageServerCatalog.All;
        foreach (var gone in Servers.Where(r => !all.Contains(r.Spec)).ToList()) Servers.Remove(gone);
        foreach (var spec in all.Where(s => Servers.All(r => r.Spec != s))) Servers.Add(new ServerRow(spec, InstallAsync));
    }

    /// <summary>Which servers are here: the user's command, Wicked's folder, the PATH (file checks, on a worker).</summary>
    public async Task RefreshAsync()
    {
        SyncWithCatalog();
        var locator = new ServerLocator(_shell.Language.ServersFolder,
            userCommands: _shell.Settings.Servers?.Where(kv => kv.Value.Command is { Count: > 0 }).ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Command!));
        var found = await Task.Run(() => Servers.Select(r => (Row: r, Command: locator.Find(r.Spec), Blocked: LanguageServerInstaller.MissingTool(r.Spec))).ToList());
        foreach (var (row, command, blocked) in found)
        {
            if (row.Installing) continue;
            row.Installed = command is not null;
            row.Blocked = command is null ? blocked : null;
            row.State = command is not null ? command.Executable : "Not installed";
        }
    }
}
