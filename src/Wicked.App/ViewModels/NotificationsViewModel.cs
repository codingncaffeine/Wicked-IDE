using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core;

namespace Wicked.App.ViewModels;

/// <summary>One button beside a notice: its words and what it does.</summary>
public sealed record NoticeButton(string Label, IAsyncRelayCommand Command);

/// <summary>A notice in the Notifications list, with the buttons its owner gave it and a way to go where it is dealt with.</summary>
public sealed partial class NoticeRow : ObservableObject
{
    private readonly NotificationsViewModel _owner;

    public NoticeRow(Notice notice, NotificationsViewModel owner)
    {
        Notice = notice;
        _owner = owner;
        var buttons = new List<NoticeButton>();
        foreach (var action in notice.Actions ?? [])
        {
            var id = action.Id;
            buttons.Add(new NoticeButton(action.Label, new AsyncRelayCommand(() => owner.RunAsync(this, id))));
        }

        Buttons = buttons;
    }

    public Notice Notice { get; }

    public string Text => Notice.Text;

    public NoticeKind Kind => Notice.Kind;

    public bool HasProgress => Notice.Kind == NoticeKind.Progress;

    public double Progress => Notice.Progress ?? 0;

    public bool ProgressKnown => Notice.Progress is not null;

    public bool CanGo => Notice.Route is not null;

    /// <summary>A passing or finished notice can be cleared by hand; work under way cannot.</summary>
    public bool CanClear => Notice.Kind != NoticeKind.Progress;

    public IReadOnlyList<NoticeButton> Buttons { get; }

    public bool HasButtons => Buttons.Count > 0;

    [ObservableProperty]
    private bool _busy;

    [RelayCommand]
    private void Go() => _owner.Go(this);

    [RelayCommand]
    private void Clear() => _owner.Clear(this);
}

/// <summary>The notices of one kind, under a heading.</summary>
public sealed record NoticeGroup(string Heading, IReadOnlyList<NoticeRow> Rows);

/// <summary>
/// The list behind the footer's "+N" (D26): every waiting notice, grouped by kind with the worst first, each with the buttons its
/// owner gave it (Update, Open release notes, Dismiss, "Don't tell me about this model"). The board is read on the UI thread when
/// it changes and the list is open; what a button does runs on a worker.
/// </summary>
public sealed partial class NotificationsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _queued;

    public NotificationsViewModel(ShellViewModel shell) => _shell = shell;

    public ObservableCollection<NoticeGroup> Groups { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _heading = "Notifications";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _checking;

    [ObservableProperty]
    private bool _hasUpdates;

    partial void OnIsOpenChanged(bool value)
    {
        if (value) Refresh();
    }

    /// <summary>Reads the board again (coalesced: a download reports many times a second and the list only needs the last).</summary>
    public void Refresh()
    {
        if (!IsOpen)
        {
            HasUpdates = _shell.Models.Services.State.Updates?.Count > 0;
            return;
        }

        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(Rebuild, DispatcherPriority.Background);
    }

    private void Rebuild()
    {
        _queued = false;
        var all = _shell.Notices.All;
        Groups.Clear();
        foreach (var group in all.GroupBy(n => n.Kind is NoticeKind.Success or NoticeKind.Info ? NoticeKind.Info : n.Kind).OrderByDescending(g => g.Key))
        {
            var heading = group.Key switch
            {
                NoticeKind.Progress => "IN PROGRESS",
                NoticeKind.Error => "PROBLEMS",
                NoticeKind.Warning => "WARNINGS",
                NoticeKind.Action => "UPDATES AND SUGGESTIONS",
                _ => "RECENT",
            };
            Groups.Add(new NoticeGroup(heading, [.. group.Select(n => new NoticeRow(n, this))]));
        }

        IsEmpty = all.Count == 0;
        Heading = all.Count == 0 ? "Notifications" : $"Notifications ({all.Count})";
        HasUpdates = _shell.Models.Services.State.Updates?.Count > 0;
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    [RelayCommand]
    private void ShowWhatsNew()
    {
        IsOpen = false;
        _shell.WhatsNew.Open();
    }

    /// <summary>"Check now": the same look as the daily one, at once.</summary>
    [RelayCommand]
    private async Task CheckNow()
    {
        Checking = true;
        Status = "Checking…";
        try
        {
            Status = await _shell.Models.CheckNowAsync();
        }
        finally
        {
            Checking = false;
        }
    }

    internal void Go(NoticeRow row)
    {
        if (row.Notice.Route is { } route)
        {
            IsOpen = false;
            _shell.Navigate(route);
        }
    }

    internal void Clear(NoticeRow row) => _shell.Notices.Clear(row.Notice.Key);

    /// <summary>A notice's button: the app's own (open the release notes) here, the rest to whoever posted the notice.</summary>
    internal async Task RunAsync(NoticeRow row, string actionId)
    {
        if (row.Busy) return;
        row.Busy = true;
        try
        {
            if (actionId == "notes" && row.Notice.Route?.StartsWith(Wicked.Core.Models.UpdateScheduler.UrlRoutePrefix, StringComparison.Ordinal) == true)
            {
                _shell.Models.OpenExternal(row.Notice.Route[Wicked.Core.Models.UpdateScheduler.UrlRoutePrefix.Length..]);
                return;
            }

            var key = row.Notice.Key;
            await Task.Run(() => _shell.Models.Services.Updates.PerformAsync(key, actionId));
        }
        catch (Exception ex)
        {
            Log.Warn($"The {actionId} action of {row.Notice.Key} failed: {ex.Message}");
            _shell.Notices.Post(new Notice(row.Notice.Key, $"{row.Notice.Text} ({ex.Message})", NoticeKind.Error, row.Notice.Route));
        }
        finally
        {
            row.Busy = false;
        }
    }
}
