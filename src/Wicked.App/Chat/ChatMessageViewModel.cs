using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core.Chat;

namespace Wicked.App.Chat;

/// <summary>One turn of the conversation as the list shows it. The owner (the chat) does what its buttons ask.</summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    private readonly ChatViewModel _owner;
    private RenderFrame? _pendingFrame;
    private bool _userToggled;

    public ChatMessageViewModel(ChatViewModel owner, ChatRole role, string text, IReadOnlyList<ChatImage>? images = null, string? reasoning = null)
    {
        _owner = owner;
        Role = role;
        _text = text;
        Images = images ?? [];
        _reasoningText = reasoning ?? "";
        HasReasoning = !string.IsNullOrEmpty(reasoning);
        if (images is { Count: > 0 }) LoadThumbnails(images);
    }

    public ChatRole Role { get; }

    public bool IsUser => Role == ChatRole.User;

    public bool IsAssistant => Role == ChatRole.Assistant;

    public IReadOnlyList<ChatImage> Images { get; }

    /// <summary>Small previews of the attached images, decoded on a worker.</summary>
    public ObservableCollection<Bitmap> Thumbnails { get; } = [];

    public bool HasImages => Images.Count > 0;

    /// <summary>The message's text: what a user typed, or an assistant's finished answer.</summary>
    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private string _reasoningText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    private IReadOnlyList<MdBlock> _blocks = [];

    [ObservableProperty]
    private IReadOnlyList<MdBlock> _reasoningBlocks = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowReasoning), nameof(ShowActions), nameof(IsWaiting))]
    private bool _isStreaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThoughtLabel))]
    private bool _thinking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThoughtLabel))]
    private double _thoughtSeconds;

    [ObservableProperty]
    private bool _hasReasoning;

    [ObservableProperty]
    private bool _reasoningExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowActions))]
    private string? _error;

    [ObservableProperty]
    private string _footer = "";

    /// <summary>What the model is doing before its first words arrive ("Loading Qwen… 40%").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting))]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool _isEditing;

    [ObservableProperty]
    private string _editText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRegenerate))]
    private bool _isLastAssistant;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEdit), nameof(ShowRegenerate))]
    private bool _canChange = true;

    public bool HasError => Error is not null;

    public bool IsWaiting => IsStreaming && Blocks.Count == 0 && !HasReasoning;

    public bool ShowReasoning => HasReasoning;

    public bool ShowRegenerate => IsAssistant && IsLastAssistant && CanChange && !IsStreaming;

    public bool ShowEdit => IsUser && CanChange;

    public bool ShowActions => !IsStreaming && !IsEditing && (IsUser || !HasError || Text.Length > 0);

    public string ThoughtLabel => Thinking
        ? $"Thinking… {ThoughtSeconds.ToString("0", CultureInfo.InvariantCulture)} s"
        : ThoughtSeconds >= 0.5 ? $"Thought for {ThoughtSeconds.ToString("0", CultureInfo.InvariantCulture)} s" : "Thoughts";

    partial void OnHasReasoningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowReasoning));
        OnPropertyChanged(nameof(IsWaiting));
    }

    partial void OnIsLastAssistantChanged(bool value) => OnPropertyChanged(nameof(ShowRegenerate));

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ShowActions));

    /// <summary>Raised after each streamed frame is applied, with the milliseconds it took on the UI thread less any garbage collection pause inside it (the stall test listens).</summary>
    internal static event Action<double>? FrameApplied;

    /// <summary>The newest frame the renderer produced. Frames are coalesced: only the latest one is applied.</summary>
    internal void PostFrame(RenderFrame frame)
    {
        if (Interlocked.Exchange(ref _pendingFrame, frame) is not null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (Interlocked.Exchange(ref _pendingFrame, null) is not { } latest || !IsStreaming) return;
            var pause = GC.GetTotalPauseDuration();
            var started = Stopwatch.GetTimestamp();
            Apply(latest);
            FrameApplied?.Invoke(Stopwatch.GetElapsedTime(started).TotalMilliseconds - (GC.GetTotalPauseDuration() - pause).TotalMilliseconds);
        }, DispatcherPriority.Background);
    }

    internal void Apply(RenderFrame frame)
    {
        Blocks = frame.Content;
        if (frame.Reasoning is { } reasoning)
        {
            ReasoningBlocks = reasoning;
            if (!HasReasoning)
            {
                HasReasoning = true;
                // The thinking is visible while it streams; it folds away when the answer starts.
                if (!_userToggled) ReasoningExpanded = true;
            }
        }

        if (Thinking != frame.Thinking)
        {
            Thinking = frame.Thinking;
            if (!frame.Thinking && !_userToggled) ReasoningExpanded = false;
        }

        ThoughtSeconds = frame.ThoughtSeconds;
    }

    /// <summary>The reply is over: its final text, thinking and numbers.</summary>
    internal void Complete(string text, string reasoning, RenderFrame frame, string footer, string? error)
    {
        Interlocked.Exchange(ref _pendingFrame, null);
        IsStreaming = false;
        Text = text;
        ReasoningText = reasoning;
        Apply(frame);
        Thinking = false;
        if (!_userToggled) ReasoningExpanded = false;
        Footer = footer;
        Error = error;
        OnPropertyChanged(nameof(ShowActions));
    }

    /// <summary>For a message loaded from a saved conversation.</summary>
    internal void Show(IReadOnlyList<MdBlock> content, IReadOnlyList<MdBlock>? reasoning)
    {
        Blocks = content;
        if (reasoning is not null) ReasoningBlocks = reasoning;
    }

    private void LoadThumbnails(IReadOnlyList<ChatImage> images) => _ = Task.Run(() =>
    {
        foreach (var image in images)
        {
            try
            {
                using var stream = new MemoryStream(image.Data);
                var bitmap = Bitmap.DecodeToWidth(stream, 220);
                Dispatcher.UIThread.Post(() => Thumbnails.Add(bitmap));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
            {
                Wicked.Core.Log.Warn($"An attached image could not be shown: {ex.Message}");
            }
        }
    });

    [RelayCommand]
    private void ToggleReasoning()
    {
        _userToggled = true;
        ReasoningExpanded = !ReasoningExpanded;
    }

    [RelayCommand]
    private void Copy() => _owner.CopyMessage(this);

    [RelayCommand]
    private Task Regenerate() => _owner.RegenerateAsync(this);

    [RelayCommand]
    private void Edit()
    {
        EditText = Text;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private Task SaveEdit()
    {
        IsEditing = false;
        return _owner.EditAndResendAsync(this, EditText);
    }

    [RelayCommand]
    private Task Retry() => _owner.RegenerateAsync(this);
}
