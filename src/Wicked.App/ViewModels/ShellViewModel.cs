using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Shell;
using Wicked.Core;

namespace Wicked.App.ViewModels;

/// <summary>The side panels the rail opens.</summary>
public enum SidePanel
{
    Explorer,
    Search,
    SourceControl,
    Models,
}

/// <summary>
/// The window's frame: which side panel is open, whether the assistant is showing, the footer's notice and the daemon's
/// state. Work that waits on anything (the daemon, the disk) runs on the thread pool and posts its result here.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<Action> _post;
    private readonly TimeSpan _daemonPoll;

    public ShellViewModel(WickedPaths paths, NoticeBoard? notices = null, Action<Action>? post = null, TimeSpan? daemonPoll = null)
    {
        Paths = paths;
        Notices = notices ?? new NoticeBoard();
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _daemonPoll = daemonPoll ?? TimeSpan.FromSeconds(5);
        Notices.Changed += () => _post(RefreshNotice);
        _ = Task.Run(() => WatchDaemonAsync(_stop.Token));
    }

    public WickedPaths Paths { get; }

    public NoticeBoard Notices { get; }

    public string Title => "Wicked";

    public string VersionText { get; } = "Wicked " + BuildInfo.Version.Split('+')[0];

    // ── Side panel and assistant ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorer), nameof(IsSearch), nameof(IsSourceControl), nameof(IsModels), nameof(PanelTitle), nameof(PanelEmptyTitle), nameof(PanelEmptyText))]
    private SidePanel _panel = SidePanel.Explorer;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExplorer), nameof(IsSearch), nameof(IsSourceControl), nameof(IsModels))]
    private bool _sidebarVisible = true;

    [ObservableProperty]
    private bool _assistantVisible = true;

    public bool IsExplorer => SidebarVisible && Panel == SidePanel.Explorer;

    public bool IsSearch => SidebarVisible && Panel == SidePanel.Search;

    public bool IsSourceControl => SidebarVisible && Panel == SidePanel.SourceControl;

    public bool IsModels => SidebarVisible && Panel == SidePanel.Models;

    public string PanelTitle => Panel switch
    {
        SidePanel.Search => "SEARCH",
        SidePanel.SourceControl => "SOURCE CONTROL",
        SidePanel.Models => "MODELS",
        _ => "EXPLORER",
    };

    public string PanelEmptyTitle => Panel switch
    {
        SidePanel.Search => "Nothing to search yet",
        SidePanel.SourceControl => "No repository",
        SidePanel.Models => "No models installed",
        _ => "No folder open",
    };

    public string PanelEmptyText => Panel switch
    {
        SidePanel.Search => "Open a folder to search across its files.",
        SidePanel.SourceControl => "Open a folder that is a Git repository to see its changes.",
        SidePanel.Models => "Models you install appear here, with their updates.",
        _ => "Open a folder to see its files here.",
    };

    /// <summary>The rail's buttons: the open panel's button closes the side bar, any other opens its panel.</summary>
    [RelayCommand]
    private void ShowPanel(SidePanel panel)
    {
        if (SidebarVisible && Panel == panel)
        {
            SidebarVisible = false;
            return;
        }

        Panel = panel;
        SidebarVisible = true;
    }

    [RelayCommand]
    private void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    [RelayCommand]
    private void ToggleAssistant() => AssistantVisible = !AssistantVisible;

    /// <summary>Takes the user to <paramref name="route"/> (see <see cref="ShellRoutes"/>); false for a place this build does not know.</summary>
    public bool Navigate(string route)
    {
        switch (route)
        {
            case ShellRoutes.Explorer: Panel = SidePanel.Explorer; SidebarVisible = true; return true;
            case ShellRoutes.Search: Panel = SidePanel.Search; SidebarVisible = true; return true;
            case ShellRoutes.SourceControl: Panel = SidePanel.SourceControl; SidebarVisible = true; return true;
            case ShellRoutes.Models: Panel = SidePanel.Models; SidebarVisible = true; return true;
            case ShellRoutes.Assistant: AssistantVisible = true; return true;
            default:
                Log.Warn($"No place called '{route}' to go to");
                return false;
        }
    }

    // ── The footer's notice ──────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private Notice? _notice;

    [ObservableProperty]
    private string _moreNotices = "";

    public bool IsProgressNotice => Notice?.Kind == NoticeKind.Progress;

    public string? NoticeTip => Notice?.Route is null ? null : "Go to where this is dealt with";

    partial void OnNoticeChanged(Notice? value)
    {
        OnPropertyChanged(nameof(IsProgressNotice));
        OnPropertyChanged(nameof(NoticeTip));
    }

    /// <summary>A click on the footer's notice: go where it is dealt with.</summary>
    [RelayCommand]
    private void OpenNotice()
    {
        if (Notice?.Route is { } route) Navigate(route);
    }

    private void RefreshNotice()
    {
        Notice = Notices.Top;
        var more = Notices.Count - 1;
        MoreNotices = more > 0 ? $"+{more}" : "";
    }

    // ── The daemon ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DaemonText))]
    private bool? _daemonRunning;

    public string DaemonText => DaemonRunning switch
    {
        true => "Daemon running",
        false => "Daemon not running",
        null => "Looking for the daemon…",
    };

    private async Task WatchDaemonAsync(CancellationToken ct)
    {
        using var client = new DaemonClient(Paths.DaemonSocket, TimeSpan.FromSeconds(3));
        using var timer = new PeriodicTimer(_daemonPoll);
        try
        {
            do
            {
                var running = await client.GetHealthAsync(ct).ConfigureAwait(false) is not null;
                _post(() => DaemonRunning = running);
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
