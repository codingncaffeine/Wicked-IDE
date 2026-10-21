using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.ViewModels;
using Wicked.Core;
using Wicked.Core.Extensions;

namespace Wicked.App.Extensions;

/// <summary>One installed extension in the panel: what it is, whether it runs, what it offers, and its buttons.</summary>
public sealed partial class ExtensionRow : ObservableObject
{
    private readonly ExtensionsViewModel _owner;

    public ExtensionRow(ExtensionsViewModel owner, InstalledExtension info)
    {
        _owner = owner;
        Id = info.Id;
        Name = info.Name;
        Version = info.Manifest?.Version ?? "";
        Publisher = info.Manifest?.Publisher ?? "";
        Description = info.Manifest?.Description ?? "";
        Problem = info.Problem ?? "";
        IsEnabled = info.State is not (ExtensionState.Disabled or ExtensionState.Broken);
        CanToggle = info.State != ExtensionState.Broken;
        Provides = info.Provides.Count == 0 ? "" : string.Join("  ·  ", info.Provides);
        var waiting = info.Manifest?.ActivationLanguages is { Count: > 0 } languages ? string.Join(", ", languages) : "";
        StateText = info.State switch
        {
            ExtensionState.Active => info.RunsCode ? $"Active, started in {info.LoadMilliseconds + info.ActivateMilliseconds:0.#} ms" : "Active",
            ExtensionState.Inactive => waiting.Length > 0 ? $"Waiting for a {waiting} file" : "Enabled",
            ExtensionState.Disabled => "Disabled",
            ExtensionState.Failed => "Switched off after an error",
            _ => "Cannot be used",
        };
        IsFailed = info.State is ExtensionState.Failed or ExtensionState.Broken;
        RunsCode = info.RunsCode;
    }

    public string Id { get; }

    public string Name { get; }

    public string Version { get; }

    public string Publisher { get; }

    public string Description { get; }

    public string Problem { get; }

    public bool HasProblem => Problem.Length > 0;

    /// <summary>What it contributes, one line.</summary>
    public string Provides { get; }

    public bool HasProvides => Provides.Length > 0;

    public string StateText { get; }

    public bool IsEnabled { get; }

    public bool CanToggle { get; }

    public bool IsFailed { get; }

    public bool RunsCode { get; }

    public string ToggleText => IsEnabled ? "Disable" : "Enable";

    public string Byline => Publisher.Length == 0 ? Version : $"{Publisher}  ·  {Version}";

    [RelayCommand]
    private Task Toggle() => _owner.SetEnabledAsync(Id, !IsEnabled);

    [RelayCommand]
    private Task Uninstall() => _owner.UninstallAsync(this);
}

/// <summary>
/// The Extensions panel: the installed extensions with their state and contributions, enable and disable, uninstall, and
/// the three ways to install (a folder, a .zip, a GitHub release). Owns the extension manager and the init.csx runner for
/// the window; everything slow runs on workers and the rows are rebuilt on the interface thread when the manager says
/// something changed.
/// </summary>
public sealed partial class ExtensionsViewModel : ObservableObject, IAsyncDisposable
{
    private readonly ShellViewModel _shell;
    private readonly Action<Action> _post;
    private int _refreshPosted;

    public ExtensionsViewModel(ShellViewModel shell, Action<Action>? post = null)
    {
        _shell = shell;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        Bridge = new WickedHostBridge(shell);
        Manager = new ExtensionManager(Path.Combine(shell.Paths.Data, "extensions"), Bridge, shell.Notices);
        Init = new InitScript(shell.Paths, Bridge, shell.Notices, Manager.Registry);
        Manager.Changed += RequestRefresh;
    }

    public WickedHostBridge Bridge { get; }

    public ExtensionManager Manager { get; }

    public InitScript Init { get; }

    public ObservableCollection<ExtensionRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>The line under the buttons: what is happening, or why the last install failed.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _busy;

    /// <summary>The GitHub box (owner/name) is open.</summary>
    [ObservableProperty]
    private bool _gitHubOpen;

    [ObservableProperty]
    private string _repository = "";

    /// <summary>Loads the installed extensions, then runs init.csx (so a script can use what extensions registered).</summary>
    public async Task StartAsync()
    {
        try
        {
            await Manager.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _shell.Notices.Post(new Notice("extensions", $"Extensions could not be loaded: {ex.Message}", NoticeKind.Error, "panel/extensions"));
        }

        await Init.RunAsync().ConfigureAwait(false);
    }

    private void RequestRefresh()
    {
        if (Interlocked.Exchange(ref _refreshPosted, 1) == 1) return;
        _post(() =>
        {
            Volatile.Write(ref _refreshPosted, 0);
            Refresh();
        });
    }

    /// <summary>The rows from the manager's list. Interface thread.</summary>
    public void Refresh()
    {
        Rows.Clear();
        foreach (var info in Manager.Installed) Rows.Add(new ExtensionRow(this, info));
        OnPropertyChanged(nameof(HasRows));
    }

    // ── Enable, disable, uninstall ──

    internal async Task SetEnabledAsync(string id, bool enabled)
    {
        try
        {
            await Manager.SetEnabledAsync(id, enabled).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            Report($"{id} could not be {(enabled ? "enabled" : "disabled")}: {ex.Message}");
        }
    }

    internal async Task UninstallAsync(ExtensionRow row)
    {
        if (_shell.Workbench.Dialogs is not { } dialogs) return;
        if (!await dialogs.ConfirmAsync($"Uninstall {row.Name}?", row.RunsCode ? "Its code stops running and its files are deleted." : "Its files are deleted.", "Uninstall")) return;
        try
        {
            await Manager.UninstallAsync(row.Id).ConfigureAwait(false);
            Say($"{row.Name} was uninstalled");
        }
        catch (IOException ex)
        {
            Report(ex.Message);
        }
    }

    // ── Install ──

    [RelayCommand]
    private async Task InstallFromFolder()
    {
        if (_shell.Workbench.Dialogs is not { } dialogs || await dialogs.PickFolderAsync() is not { } folder) return;
        await InstallAsync(token => Manager.InstallFromFolderAsync(folder, AskAsync, token));
    }

    [RelayCommand]
    private async Task InstallFromZip()
    {
        if (_shell.Workbench.Dialogs is not { } dialogs) return;
        var files = await dialogs.PickFilesAsync();
        if (files.Count == 0) return;
        await InstallAsync(token => Manager.InstallFromZipAsync(files[0], AskAsync, token));
    }

    [RelayCommand]
    private void ToggleGitHub() => GitHubOpen = !GitHubOpen;

    [RelayCommand]
    private async Task InstallFromGitHub()
    {
        var repository = Repository.Trim();
        if (repository.Length == 0) return;
        await InstallAsync(token => Manager.InstallFromGitHubAsync(repository, AskAsync, token));
        if (Status.Contains("installed", StringComparison.Ordinal)) GitHubOpen = false;
    }

    private async Task InstallAsync(Func<CancellationToken, Task<InstalledExtension?>> install)
    {
        if (Busy) return;
        Busy = true;
        Status = "Installing…";
        try
        {
            var installed = await install(CancellationToken.None);
            Status = installed is null ? "Nothing was installed." : installed.State == ExtensionState.Failed
                ? $"{installed.Name} was installed but could not start: {installed.Problem}"
                : $"{installed.Name} {installed.Manifest?.Version} installed.";
            if (installed is not null) _shell.Notices.Post(new Notice($"ext:{installed.Id}:installed", Status, installed.State == ExtensionState.Failed ? NoticeKind.Warning : NoticeKind.Success, "panel/extensions"));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Report(ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>The question before anything is installed: what it is, who from, and what it can do.</summary>
    private Task<bool> AskAsync(InstallRequest request)
    {
        if (_shell.Workbench.Dialogs is not { } dialogs) return Task.FromResult(false);
        var manifest = request.Manifest;
        var offers = new List<string>();
        var c = manifest.Contributes;
        if (c.Themes.Count > 0) offers.Add($"{c.Themes.Count} theme{(c.Themes.Count == 1 ? "" : "s")}");
        if (c.Grammars.Count > 0) offers.Add($"{c.Grammars.Count} grammar{(c.Grammars.Count == 1 ? "" : "s")}");
        if (c.Snippets.Count > 0) offers.Add($"{c.Snippets.Count} snippet file{(c.Snippets.Count == 1 ? "" : "s")}");
        if (c.LanguageServers.Count > 0) offers.Add($"{c.LanguageServers.Count} language server{(c.LanguageServers.Count == 1 ? "" : "s")}");
        if (c.Keybindings.Count > 0) offers.Add($"{c.Keybindings.Count} key binding{(c.Keybindings.Count == 1 ? "" : "s")}");
        var who = $"{manifest.Name} {manifest.Version} by {manifest.Publisher}";
        var detail = manifest.Main is not null
            ? $"This extension runs code on your computer: {who}. It can read and change your files, use the network and start programs, with your permissions. Install only extensions you trust.\nFrom {request.Source}."
            : $"{who} adds {(offers.Count == 0 ? "settings" : string.Join(", ", offers))} and runs no code of its own.\nFrom {request.Source}.";
        if (manifest.Main is null && c.LanguageServers.Count > 0) detail += "\nA language server it names is a program that runs on your computer when you open files of its languages.";
        if (request.Source.StartsWith("github.com/", StringComparison.Ordinal) && !request.Verified) detail += "\nThe release published no checksum, so the download could not be verified.";
        if (request.Source.StartsWith("github.com/", StringComparison.Ordinal) && request.Verified) detail += "\nThe download matched the checksum published for it.";
        return Dispatcher.UIThread.InvokeAsync(() => dialogs.ConfirmAsync($"Install {manifest.Name}?", detail, "Install"));
    }

    private void Say(string text) => _post(() => Status = text);

    private void Report(string text)
    {
        _post(() => Status = text);
        _shell.Notices.Post(new Notice("extensions:error", text, NoticeKind.Error, "panel/extensions"));
    }

    public async ValueTask DisposeAsync()
    {
        Manager.Changed -= RequestRefresh;
        await Init.DisposeAsync().ConfigureAwait(false);
        await Manager.DisposeAsync().ConfigureAwait(false);
        Bridge.Dispose();
    }
}
