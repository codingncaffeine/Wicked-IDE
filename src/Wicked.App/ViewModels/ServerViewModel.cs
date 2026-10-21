using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Contracts;
using Wicked.Core;
using Wicked.Core.Server;
using Wicked.Core.Settings;

namespace Wicked.App.ViewModels;

/// <summary>An API key in the list: its name and the start of it, and a way to take it back.</summary>
public sealed partial class ApiKeyRow(ApiKeyInfo info, ServerViewModel owner) : ObservableObject
{
    public ApiKeyInfo Info { get; } = info;

    public string Name => Info.Name;

    public string Detail => $"{Info.Prefix}  ·  made {Info.Created.LocalDateTime:d MMM yyyy}";

    [RelayCommand]
    private Task Revoke() => owner.RevokeAsync(this);
}

/// <summary>A "Connect …" recipe with its copy button.</summary>
public sealed partial class SnippetRow(ConnectSnippet snippet, ServerViewModel owner) : ObservableObject
{
    public string Program => snippet.Program;

    public string Where => snippet.Where;

    public string Text => snippet.Text;

    public string? Note => snippet.Note;

    public bool HasNote => snippet.Note is not null;

    [RelayCommand]
    private Task Copy() => owner.CopyAsync(snippet.Text);
}

/// <summary>
/// Settings → Server (D12): whether the daemon serves the OpenAI and Anthropic APIs, on which port, to whom (this machine, or the
/// network with a warning), with which keys and which browser origins, loading models on the first request or not; the daemon's own
/// state with a way to start and stop it; the recipes that connect other programs; and "start at login". The settings are written to
/// settings.json and the daemon told to read them again; everything that touches the daemon, a file or systemd runs on a worker.
/// </summary>
public sealed partial class ServerViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly ApiKeyStore _keys;
    private bool _loading;
    private CancellationTokenSource? _typing;
    private int _status;

    public ServerViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _keys = new ApiKeyStore(shell.Paths.ApiKeysFile);
        ConfigHome = SystemdUnit.DefaultConfigHome();
        shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ShellViewModel.DaemonRunning)) _ = RefreshAsync();
            if (e.PropertyName == nameof(ShellViewModel.SettingsOpen) && shell.SettingsOpen) _ = RefreshAsync();
        };
        Load(shell.Settings);
        RebuildSnippets();
    }

    /// <summary>Starts a daemon in the background and says whether one is up (replaced in tests, which start theirs in-process).</summary>
    public Func<CancellationToken, Task<bool>> Launch { get; set; } = DefaultLaunchAsync;

    /// <summary>The user's XDG config home, where the systemd unit goes (a test points it at a folder of its own).</summary>
    public string ConfigHome { get; set; }

    /// <summary>What runs systemctl for the start-at-login switch (null runs the real one; tests record the commands).</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<int>>? SystemctlRunner { get; set; }

    /// <summary>The daemon program the unit starts (null looks for it beside this program, then on the PATH).</summary>
    public string? DaemonPath { get; set; }

    public ObservableCollection<ApiKeyRow> Keys { get; } = [];

    public ObservableCollection<SnippetRow> Snippets { get; } = [];

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private double _port;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanWarning))]
    private bool _listenOnLan;

    [ObservableProperty]
    private bool _autoLoad;

    /// <summary>Allowed browser origins, one per line.</summary>
    [ObservableProperty]
    private string _cors = "";

    [ObservableProperty]
    private double _maxRequestMB;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop))]
    private bool _daemonRunning;

    [ObservableProperty]
    private string _daemonText = "Looking for the daemon…";

    [ObservableProperty]
    private string _gatewayText = "";

    [ObservableProperty]
    private bool _gatewayProblem;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(CanStop))]
    private bool _busy;

    [ObservableProperty]
    private string _newKeyName = "";

    /// <summary>The key just made, shown once: only its hash is kept.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNewKey))]
    private string? _newKey;

    [ObservableProperty]
    private bool _startAtLogin;

    [ObservableProperty]
    private string _startAtLoginText = "";

    public bool HasNewKey => NewKey is not null;

    public bool CanStart => !DaemonRunning && !Busy;

    public bool CanStop => DaemonRunning && !Busy;

    public string LanWarning => ListenOnLan
        ? "Anyone on your network can reach this server. Create an API key below: without one, only this machine is answered."
        : "";

    public bool HasLanWarning => ListenOnLan;

    public string BaseUrl => string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{(int)Port}/v1");

    /// <summary>Puts the settings on the controls (called when settings.json changes).</summary>
    public void Load(WickedSettings s)
    {
        _loading = true;
        Enabled = s.ServerEnabled;
        Port = s.ServerPort;
        ListenOnLan = s.ServerListenOnLan;
        AutoLoad = s.ServerAutoLoad;
        Cors = string.Join('\n', s.ServerCors);
        MaxRequestMB = s.ServerMaxRequestMB;
        _loading = false;
        OnPropertyChanged(nameof(HasLanWarning));
        OnPropertyChanged(nameof(BaseUrl));
    }

    partial void OnEnabledChanged(bool value) => Change(s => s with { ServerEnabled = value });

    partial void OnListenOnLanChanged(bool value)
    {
        OnPropertyChanged(nameof(HasLanWarning));
        Change(s => s with { ServerListenOnLan = value });
    }

    partial void OnAutoLoadChanged(bool value) => Change(s => s with { ServerAutoLoad = value });

    partial void OnPortChanged(double value)
    {
        OnPropertyChanged(nameof(BaseUrl));
        RebuildSnippets();
        Typed(() => Change(s => s with { ServerPort = (int)Math.Clamp(Math.Round(value), 0, 65535) }));
    }

    partial void OnMaxRequestMBChanged(double value) => Typed(() => Change(s => s with { ServerMaxRequestMB = (int)Math.Clamp(Math.Round(value), 1, 1024) }));

    partial void OnCorsChanged(string value) => Typed(() => Change(s => s with { ServerCors = [.. value.Split(['\n', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] }));

    /// <summary>A number or a list is typed in one go: written a moment after the last keystroke, not once per key.</summary>
    private void Typed(Action apply)
    {
        if (_loading) return;
        _typing?.Cancel();
        var typing = _typing = new CancellationTokenSource();
        _ = Task.Delay(TimeSpan.FromMilliseconds(400), typing.Token).ContinueWith(
            _ => Dispatcher.UIThread.Post(apply), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void Change(Func<WickedSettings, WickedSettings> change)
    {
        if (_loading) return;
        _ = ChangeAsync(change);
    }

    private async Task ChangeAsync(Func<WickedSettings, WickedSettings> change)
    {
        await _shell.UpdateSettingsAsync(change);
        await ApplyAsync();
    }

    /// <summary>Tells a running daemon to read settings.json and the keys again, then looks at how the gateway stands.</summary>
    private async Task ApplyAsync()
    {
        var status = await Task.Run(async () =>
        {
            using var client = new DaemonClient(_shell.Paths.DaemonSocket, TimeSpan.FromSeconds(15));
            return await client.ApplyAsync(CancellationToken.None);
        });
        Show(status);
    }

    // ── The daemon ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Asks the daemon how it stands and what keys exist (on a worker), and refreshes everything that depends on it.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _status, 1) == 1) return;
        try
        {
            var (status, keys, unit) = await Task.Run(async () =>
            {
                using var client = new DaemonClient(_shell.Paths.DaemonSocket, TimeSpan.FromSeconds(3));
                return (await client.GetStatusAsync(CancellationToken.None), await _keys.ListAsync(), new SystemdUnit(ConfigHome).IsInstalled);
            });
            Show(status);
            SyncKeys(keys);
            _loading = true;
            StartAtLogin = unit;
            _loading = false;
            StartAtLoginText = unit ? "The daemon starts when you log in." : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not read the server state: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _status, 0);
        }
    }

    private void Show(DaemonStatus? status)
    {
        DaemonRunning = status is not null;
        if (status is null)
        {
            DaemonText = "The daemon is not running. Start it here, or start it at login below.";
            GatewayText = "";
            GatewayProblem = false;
            return;
        }

        DaemonText = $"The daemon is running (process {status.ProcessId}).";
        var gateway = status.Gateway;
        GatewayProblem = gateway.Error is not null;
        GatewayText = gateway.Running ? $"Serving {gateway.Address}/v1 · {status.Installed} model{(status.Installed == 1 ? "" : "s")} · {status.KeyCount} API key{(status.KeyCount == 1 ? "" : "s")}"
            : gateway.Error ?? (gateway.Enabled ? (status.Ready ? "The server is not listening." : "The daemon is starting…") : "The server is switched off.");
    }

    [RelayCommand]
    private async Task StartDaemon()
    {
        Busy = true;
        DaemonText = "Starting the daemon…";
        try
        {
            var up = await Task.Run(() => Launch(CancellationToken.None));
            if (!up) DaemonText = "The daemon could not be started: wickedd was not found next to this program.";
        }
        finally
        {
            Busy = false;
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task StopDaemon()
    {
        Busy = true;
        try
        {
            await Task.Run(async () =>
            {
                using var client = new DaemonClient(_shell.Paths.DaemonSocket, TimeSpan.FromSeconds(5));
                try
                {
                    using var response = await client.Http.PostAsync("v1/wicked/shutdown", null, CancellationToken.None);
                }
                catch (HttpRequestException)
                {
                    // Already gone.
                }

                for (var i = 0; i < 100 && await client.GetHealthAsync(CancellationToken.None) is not null; i++) await Task.Delay(100);
            });
        }
        finally
        {
            Busy = false;
            await RefreshAsync();
        }
    }

    private static async Task<bool> DefaultLaunchAsync(CancellationToken ct)
    {
        var daemon = DaemonLocator.Find();
        if (daemon is null) return false;
        var setsid = File.Exists("/usr/bin/setsid") ? "/usr/bin/setsid" : null;
        var info = new System.Diagnostics.ProcessStartInfo(setsid ?? daemon) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (setsid is not null) info.ArgumentList.Add(daemon);
        try
        {
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        using var client = new DaemonClient(WickedPaths.ForCurrentUser().DaemonSocket, TimeSpan.FromSeconds(2));
        for (var i = 0; i < 100; i++)
        {
            if (await client.GetHealthAsync(ct).ConfigureAwait(false) is not null) return true;
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        return false;
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────

    private void SyncKeys(IReadOnlyList<ApiKeyInfo> keys)
    {
        Keys.Clear();
        foreach (var key in keys) Keys.Add(new ApiKeyRow(key, this));
        RebuildSnippets();
    }

    [RelayCommand]
    private async Task CreateKey()
    {
        var name = NewKeyName;
        var (_, key) = await Task.Run(() => _keys.CreateAsync(name));
        NewKey = key;
        NewKeyName = "";
        await RefreshKeysAsync();
        await ApplyAsync();
    }

    internal async Task RevokeAsync(ApiKeyRow row)
    {
        await Task.Run(() => _keys.RevokeAsync(row.Info.Id));
        await RefreshKeysAsync();
        await ApplyAsync();
    }

    private async Task RefreshKeysAsync() => SyncKeys(await Task.Run(() => _keys.ListAsync()));

    /// <summary>The new key is on the clipboard; the box that shows it goes away (it cannot be shown again).</summary>
    [RelayCommand]
    private async Task CopyNewKey()
    {
        if (NewKey is { } key) await CopyAsync(key);
    }

    [RelayCommand]
    private void DismissNewKey()
    {
        NewKey = null;
        RebuildSnippets();
    }

    partial void OnNewKeyChanged(string? value) => RebuildSnippets();

    // ── Recipes ──────────────────────────────────────────────────────────────────────────────────

    internal Task CopyAsync(string text) => _shell.Clipboard?.Invoke(text) ?? Task.CompletedTask;

    private void RebuildSnippets()
    {
        var installed = _shell.Models.Services.State.Installed;
        var model = installed.Select(m => m.Manifest?.CatalogId).FirstOrDefault(id => id is not null) ?? installed.FirstOrDefault()?.Variants.FirstOrDefault()?.Id;
        Snippets.Clear();
        foreach (var snippet in ConnectSnippets.Build((int)Port, model, NewKey)) Snippets.Add(new SnippetRow(snippet, this));
    }

    // ── Start at login ───────────────────────────────────────────────────────────────────────────

    partial void OnStartAtLoginChanged(bool value)
    {
        if (_loading) return;
        _ = SetStartAtLoginAsync(value);
    }

    private async Task SetStartAtLoginAsync(bool on)
    {
        var unit = new SystemdUnit(ConfigHome, SystemctlRunner);
        try
        {
            if (on)
            {
                var daemon = DaemonPath ?? DaemonLocator.Find() ?? throw new IOException("wickedd was not found next to this program");
                await Task.Run(() => unit.InstallAsync(daemon, _shell.Paths.Isolated ? Path.GetDirectoryName(_shell.Paths.Config) : null));
                StartAtLoginText = "The daemon starts when you log in.";
            }
            else
            {
                await Task.Run(() => unit.RemoveAsync());
                StartAtLoginText = "";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Start at login: {ex.Message}");
            StartAtLoginText = $"Could not change it: {ex.Message}";
            _loading = true;
            StartAtLogin = unit.IsInstalled;
            _loading = false;
        }
    }
}
