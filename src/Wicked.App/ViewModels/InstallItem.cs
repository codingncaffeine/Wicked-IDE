using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Wicked.App.ViewModels;

/// <summary>
/// One model's install as every screen shows it: busy or not, paused, how far along, what it is doing. The Models panel's
/// card, the Browse page's row and the footer all follow the same item, so they never disagree.
/// </summary>
public sealed partial class InstallItem(string id, ModelsViewModel owner) : ObservableObject
{
    public string Id { get; } = id;

    /// <summary>What the footer calls it ("Qwen3.8 27B").</summary>
    public string Name { get; set; } = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume))]
    private bool _busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPause), nameof(CanResume))]
    private bool _paused;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "";

    public bool CanPause => Busy && !Paused;

    public bool CanResume => Busy && Paused;

    [RelayCommand]
    private Task Pause() => owner.PauseAsync(this);

    [RelayCommand]
    private void Resume() => owner.Resume(this);

    [RelayCommand]
    private Task Cancel() => owner.CancelAsync(this);
}

/// <summary>A question the window asks before going on: the licence to accept, a delete to confirm.</summary>
public sealed partial class ModelPrompt(
    ModelsViewModel owner, string title, string body, string confirmText, Func<Task> onConfirm,
    string? linkText = null, string? linkUrl = null, bool danger = false) : ObservableObject
{
    public string Title { get; } = title;

    public string Body { get; } = body;

    public string ConfirmText { get; } = confirmText;

    public string? LinkText { get; } = linkText;

    public string? LinkUrl => linkUrl;

    public bool HasLink => linkUrl is not null;

    public bool Danger { get; } = danger;

    [RelayCommand]
    private Task Confirm()
    {
        owner.Prompt = null;
        return onConfirm();
    }

    [RelayCommand]
    private void Cancel() => owner.Prompt = null;

    [RelayCommand]
    private void OpenLink()
    {
        if (linkUrl is { } url) owner.OpenExternal(url);
    }
}
