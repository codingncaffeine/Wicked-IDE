using System.Collections.ObjectModel;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Wicked.Core;
using Wicked.Core.Settings;
using Wicked.Core.Terminal;

namespace Wicked.App.Terminal;

/// <summary>A tab: one or more terminals side by side.</summary>
public sealed partial class TerminalGroupViewModel : ObservableObject
{
    public ObservableCollection<TerminalInstanceViewModel> Terminals { get; } = [];

    [ObservableProperty]
    private TerminalInstanceViewModel? _active;

    public string Title => Active?.Title ?? "";

    internal void Touch() => OnPropertyChanged(nameof(Title));
}

/// <summary>What the shell opens for a file or address the user followed in a terminal.</summary>
public sealed record TerminalFileTarget(string Path, int? Line, int? Column);

/// <summary>
/// The terminal panel's state: tabs of terminals (each tab possibly split side by side), the shells to start, and the
/// calls the window makes (<see cref="NewTerminal"/>, <see cref="RunInActiveTerminal"/>, ...). Shells are started on
/// workers; nothing here waits for a process.
/// </summary>
public sealed partial class TerminalPanelViewModel : ObservableObject, IDisposable
{
    private readonly WickedPaths _paths;
    private readonly string _integrationDirectory;
    private readonly List<ShellProfile> _profiles;
    private WickedSettings _settings = new();
    private TerminalTheme _theme = TerminalTheme.Default;
    private bool _disposed;

    public TerminalPanelViewModel(WickedPaths paths, string? workingDirectory = null, IReadOnlyList<ShellProfile>? profiles = null)
    {
        _paths = paths;
        _integrationDirectory = Path.Combine(paths.Data, "shell-integration");
        WorkingDirectory = workingDirectory;
        _profiles = profiles?.ToList() ?? ShellProfiles.Detect();
        Profiles = _profiles;
    }

    /// <summary>The shells that can be started.</summary>
    public IReadOnlyList<ShellProfile> Profiles { get; }

    public ObservableCollection<TerminalGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    private TerminalGroupViewModel? _activeGroup;

    /// <summary>The folder new terminals start in: the open folder.</summary>
    public string? WorkingDirectory { get; set; }

    public TerminalInstanceViewModel? ActiveTerminal => ActiveGroup?.Active;

    /// <summary>The user followed a file reference; the window opens it in the editor.</summary>
    public event Action<TerminalFileTarget>? OpenFileRequested;

    /// <summary>The user followed an address; the window opens it in the browser.</summary>
    public event Action<string>? OpenUrlRequested;

    /// <summary>The last terminal closed.</summary>
    public event Action? AllClosed;

    /// <summary>A terminal was added, so the window can show the panel.</summary>
    public event Action<TerminalInstanceViewModel>? TerminalAdded;

    public WickedSettings Settings => _settings;

    // ── Settings and theme ───────────────────────────────────────────────────────────────────────

    /// <summary>Applies the terminal settings: new terminals use them at once, open ones follow for the font, cursor and selection.</summary>
    public void ApplySettings(WickedSettings settings)
    {
        var fontChanged = Math.Abs(settings.TerminalFontSize - _settings.TerminalFontSize) > 0.01;
        _settings = settings;
        foreach (var instance in AllTerminals())
        {
            if (instance.Pane is { } pane) ApplyViewSettings(pane.View, instance);
        }

        if (fontChanged) _ = ApplyFontsAsync();
    }

    public void ApplyTheme(TerminalTheme theme)
    {
        _theme = theme;
        foreach (var instance in AllTerminals()) instance.Pane?.View.ApplyTheme(theme);
    }

    private async Task ApplyFontsAsync()
    {
        var fonts = await TerminalFonts.GetAsync(_settings.TerminalFontSize).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var instance in AllTerminals()) instance.Pane?.View.ApplyFonts(fonts);
        });
    }

    private void ApplyViewSettings(TerminalView view, TerminalInstanceViewModel instance)
    {
        view.CursorStyle = _settings.TerminalCursorStyle switch
        {
            "line" => CursorShape.Bar,
            "underline" => CursorShape.Underline,
            _ => CursorShape.Block,
        };
        view.CursorBlinking = _settings.TerminalCursorBlinking;
        view.CopyOnSelection = _settings.TerminalCopyOnSelection;
        view.Decorations = _settings.TerminalShellIntegration && instance.Profile.Kind is ShellKind.Bash or ShellKind.Zsh or ShellKind.Fish or ShellKind.PowerShell;
    }

    public IEnumerable<TerminalInstanceViewModel> AllTerminals() => Groups.SelectMany(g => g.Terminals);

    // ── Opening and closing ──────────────────────────────────────────────────────────────────────

    /// <summary>The profile the settings choose, or the first.</summary>
    public ShellProfile? DefaultProfile => ShellProfiles.Choose(_profiles, _settings.TerminalProfile);

    /// <summary>Opens a terminal in a new tab; the shell starts in the background.</summary>
    public TerminalInstanceViewModel? NewTerminal(ShellProfile? profile = null, string? workingDirectory = null)
    {
        profile ??= DefaultProfile;
        if (profile is null) return null;
        var instance = Create(profile, workingDirectory ?? ActiveTerminal?.WorkingDirectory ?? WorkingDirectory);
        var group = new TerminalGroupViewModel();
        group.Terminals.Add(instance);
        group.Active = instance;
        Groups.Add(group);
        ActiveGroup = group;
        TerminalAdded?.Invoke(instance);
        return instance;
    }

    /// <summary>Opens a terminal beside the active one, in the same tab, in the same folder.</summary>
    public TerminalInstanceViewModel? Split(ShellProfile? profile = null)
    {
        if (ActiveGroup is not { } group || group.Active is not { } current) return NewTerminal(profile);
        var instance = Create(profile ?? current.Profile, current.WorkingDirectory ?? WorkingDirectory);
        group.Terminals.Add(instance);
        group.Active = instance;
        OnPropertyChanged(nameof(ActiveTerminal));
        TerminalAdded?.Invoke(instance);
        return instance;
    }

    private TerminalInstanceViewModel Create(ShellProfile profile, string? workingDirectory)
    {
        var instance = new TerminalInstanceViewModel(profile, workingDirectory);
        _ = StartAsync(instance);
        return instance;
    }

    private async Task StartAsync(TerminalInstanceViewModel instance)
    {
        try
        {
            var settings = _settings;
            var fonts = await TerminalFonts.GetAsync(settings.TerminalFontSize).ConfigureAwait(false);
            var session = await Task.Run(() => StartSession(instance, settings)).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || !AllTerminals().Contains(instance))
                {
                    session.Dispose();
                    return;
                }

                instance.Session = session;
                instance.Wire(session, OnExited);
                var pane = new TerminalPane(fonts, _theme);
                ApplyViewSettings(pane.View, instance);
                pane.View.LinkActivated += link => _ = FollowLinkAsync(instance, link);
                pane.View.Attach(session);
                instance.Pane = pane;
            });
        }
        catch (Exception e)
        {
            Log.Warn($"Terminal could not start: {e.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => instance.StartError = e.Message);
        }
    }

    private TerminalSession StartSession(TerminalInstanceViewModel instance, WickedSettings settings)
    {
        var integration = settings.TerminalShellIntegration;
        if (integration)
        {
            try
            {
                ShellIntegration.Install(_integrationDirectory);
            }
            catch (IOException e)
            {
                Log.Warn($"Shell integration could not be written: {e.Message}");
                integration = false;
            }
        }

        var launch = ShellIntegration.Launch(instance.Profile, _integrationDirectory, integration, instance.WorkingDirectory, 80, 24);
        var options = new TerminalSessionOptions
        {
            Launch = launch,
            Terminal = new TerminalOptions
            {
                Scrollback = Math.Clamp(settings.TerminalScrollback, 0, 1_000_000),
                AllowClipboardWrite = settings.TerminalAllowClipboardWrite,
                Foreground = _theme.Foreground,
                Background = _theme.Background,
                Ansi = _theme.Palette[..16],
            },
        };
        var session = new TerminalSession(options);
        if (settings.TerminalAllowClipboardWrite) session.ClipboardWrite += text => Dispatcher.UIThread.Post(() => _ = SetClipboardAsync(instance, text));
        session.Start();
        return session;
    }

    private static async Task SetClipboardAsync(TerminalInstanceViewModel instance, string text)
    {
        if (instance.Pane is { } pane && Avalonia.Controls.TopLevel.GetTopLevel(pane)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    private void OnExited(TerminalInstanceViewModel instance, int code)
    {
        // A shell that ended normally takes its tab with it; one that failed stays, with what happened.
        if (code == 0) Close(instance);
        else instance.Session?.Print($"\r\n\u001b[2m[The terminal process \"{instance.Profile.Path}\" exited with code {code}]\u001b[0m\r\n");
    }

    public void Close(TerminalInstanceViewModel instance)
    {
        var group = Groups.FirstOrDefault(g => g.Terminals.Contains(instance));
        if (group is null) return;
        var index = group.Terminals.IndexOf(instance);
        group.Terminals.Remove(instance);
        instance.Dispose();
        if (group.Terminals.Count == 0)
        {
            var groupIndex = Groups.IndexOf(group);
            Groups.Remove(group);
            if (ActiveGroup == group) ActiveGroup = Groups.Count == 0 ? null : Groups[Math.Min(groupIndex, Groups.Count - 1)];
            if (Groups.Count == 0) AllClosed?.Invoke();
        }
        else if (group.Active == instance) group.Active = group.Terminals[Math.Min(index, group.Terminals.Count - 1)];

        OnPropertyChanged(nameof(ActiveTerminal));
    }

    /// <summary>Kills the active terminal (its shell and everything the shell started) and closes its pane.</summary>
    public void KillActive()
    {
        if (ActiveTerminal is { } active) Close(active);
    }

    public void Rename(TerminalInstanceViewModel instance, string? name)
    {
        instance.CustomName = name;
        foreach (var g in Groups) g.Touch();
    }

    public void Activate(TerminalGroupViewModel group)
    {
        ActiveGroup = group;
        OnPropertyChanged(nameof(ActiveTerminal));
    }

    public void Activate(TerminalInstanceViewModel instance)
    {
        var group = Groups.FirstOrDefault(g => g.Terminals.Contains(instance));
        if (group is null) return;
        group.Active = instance;
        ActiveGroup = group;
        OnPropertyChanged(nameof(ActiveTerminal));
    }

    /// <summary>The next tab (or the previous one), wrapping round.</summary>
    public void CycleGroup(int direction)
    {
        if (Groups.Count < 2 || ActiveGroup is null) return;
        Activate(Groups[(Groups.IndexOf(ActiveGroup) + direction + Groups.Count) % Groups.Count]);
    }

    /// <summary>Moves between the splits of the active tab.</summary>
    public void CycleSplit(int direction)
    {
        if (ActiveGroup is not { } g || g.Terminals.Count < 2 || g.Active is null) return;
        g.Active = g.Terminals[(g.Terminals.IndexOf(g.Active) + direction + g.Terminals.Count) % g.Terminals.Count];
        OnPropertyChanged(nameof(ActiveTerminal));
    }

    public void ClearActive() => ActiveTerminal?.Session?.Clear();

    // ── What the window calls ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Types <paramref name="text"/> into the active terminal and presses Enter ("Run Selected Text in Active Terminal",
    /// "Run Command"); a terminal is opened first if there is none. Several lines are pasted as one block.
    /// </summary>
    public async Task RunInActiveTerminal(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var terminal = ActiveTerminal ?? NewTerminal();
        if (terminal is null) return;
        var session = await WaitForSessionAsync(terminal).ConfigureAwait(false);
        if (session is null) return;
        var body = text.TrimEnd('\r', '\n');
        if (body.Contains('\n', StringComparison.Ordinal) || body.Contains('\r', StringComparison.Ordinal)) session.Paste(body);
        else session.Input(body);
        session.Input("\r");
    }

    /// <summary>Runs a command line in the active terminal, in <paramref name="directory"/> when given (a new terminal there).</summary>
    public async Task RunCommand(string command, string? directory = null)
    {
        if (!string.IsNullOrEmpty(directory) && ActiveTerminal?.WorkingDirectory != directory) NewTerminal(workingDirectory: directory);
        await RunInActiveTerminal(command).ConfigureAwait(false);
    }

    private static async Task<TerminalSession?> WaitForSessionAsync(TerminalInstanceViewModel terminal)
    {
        for (var i = 0; i < 400 && terminal.Session is null && terminal.StartError is null; i++) await Task.Delay(25).ConfigureAwait(false);
        return terminal.Session;
    }

    /// <summary>Sends text to the active terminal as typed, without Enter.</summary>
    public void SendText(string text) => ActiveTerminal?.Session?.Input(text);

    // ── Links ────────────────────────────────────────────────────────────────────────────────────

    private async Task FollowLinkAsync(TerminalInstanceViewModel instance, TerminalLinkActivation link)
    {
        if (link.Kind == LinkKind.Url)
        {
            if (link.Target.StartsWith("file://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(link.Target, UriKind.Absolute, out var uri))
            {
                var path = Uri.UnescapeDataString(uri.AbsolutePath);
                if (File.Exists(path)) await Dispatcher.UIThread.InvokeAsync(() => OpenFileRequested?.Invoke(new TerminalFileTarget(path, link.Line, link.Column)));
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => OpenUrlRequested?.Invoke(link.Target));
            return;
        }

        var resolved = await Task.Run(() => ResolvePath(link.Target, instance.WorkingDirectory ?? WorkingDirectory)).ConfigureAwait(false);
        if (resolved is null) return;
        await Dispatcher.UIThread.InvokeAsync(() => OpenFileRequested?.Invoke(new TerminalFileTarget(resolved, link.Line, link.Column)));
    }

    /// <summary>The file a path in terminal text names, from the terminal's folder (then the open folder), or null when no such file exists.</summary>
    public static string? ResolvePath(string written, string? directory)
    {
        var path = written;
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        try
        {
            var full = Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(directory ?? Environment.CurrentDirectory, path));
            return File.Exists(full) ? full : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var instance in AllTerminals().ToList()) instance.Dispose();
        Groups.Clear();
    }
}
