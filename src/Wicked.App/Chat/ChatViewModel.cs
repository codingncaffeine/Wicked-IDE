using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Settings;

namespace Wicked.App.Chat;

/// <summary>An installed model in the chat's model chip, with the state the runtime reports.</summary>
public sealed partial class ModelChoice(string id, ChatViewModel owner) : ObservableObject
{
    public string Id { get; } = id;

    [ObservableProperty]
    private string _name = id;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(IsLoaded), nameof(IsFailed), nameof(IsLoading))]
    private RunState _state;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private double? _progress;

    [ObservableProperty]
    private bool _acceptsImages;

    [ObservableProperty]
    private bool _reasons;

    [ObservableProperty]
    private int? _contextSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private string? _error;

    [ObservableProperty]
    private bool _isSelected;

    public bool IsLoaded => State is RunState.Loaded;

    public bool IsLoading => State is RunState.Loading;

    public bool IsFailed => State is RunState.Failed;

    public string StateText => State switch
    {
        RunState.Loaded => "Loaded",
        RunState.Loading => Progress is { } p ? $"Loading {p.ToString("0%", CultureInfo.InvariantCulture)}" : "Loading…",
        RunState.Sleeping => "Sleeping",
        RunState.Failed => Error is { Length: > 0 } e ? $"Failed: {e}" : "Failed",
        _ => "Not loaded",
    };

    [RelayCommand]
    private void Select() => owner.SelectModel(this);
}

/// <summary>A saved conversation in the history list.</summary>
public sealed partial class ConversationItem(Guid id, ChatViewModel owner) : ObservableObject
{
    public Guid Id { get; } = id;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private DateTimeOffset _updated;

    [ObservableProperty]
    private bool _isCurrent;

    public string When => ChatViewModel.Ago(Updated);

    partial void OnUpdatedChanged(DateTimeOffset value) => OnPropertyChanged(nameof(When));

    [RelayCommand]
    private Task Open() => owner.OpenAsync(Id);

    [RelayCommand]
    private Task Rename() => owner.RenameAsync(this);

    [RelayCommand]
    private Task Delete() => owner.DeleteAsync(this);
}

/// <summary>A picture waiting in the composer to go with the next message.</summary>
public sealed class AttachmentViewModel
{
    public AttachmentViewModel(ChatViewModel owner, string name, ChatImage image, Bitmap? thumbnail)
    {
        Name = name;
        Image = image;
        Thumbnail = thumbnail;
        Remove = new RelayCommand(() => owner.RemoveAttachment(this));
    }

    public string Name { get; }

    public ChatImage Image { get; }

    public Bitmap? Thumbnail { get; }

    public IRelayCommand Remove { get; }
}

/// <summary>A choice in the system prompt picker.</summary>
public sealed partial class PromptChoice(ChatViewModel owner, PromptPreset? preset) : ObservableObject
{
    public PromptPreset? Preset { get; } = preset;

    public string Name => Preset?.Name ?? "No system prompt";

    [ObservableProperty]
    private bool _isCurrent;

    [RelayCommand]
    private void Select() => owner.SelectPrompt(Preset);
}

/// <summary>
/// The Assistant panel: the conversation on screen, the composer, the model chip, the history and the system prompt. Every
/// call into the services (the runtime, the engine, the store) runs on a worker and its result is posted back here; nothing
/// on this class blocks the UI thread.
/// </summary>
public sealed partial class ChatViewModel : ObservableObject, ICodeActions, IDisposable
{
    public const string NewTitle = "New chat";
    private const int MaxImageBytes = 25 * 1024 * 1024;

    private readonly AssistantServices _services;
    private readonly IChatHost _host;
    private Guid _conversationId = Guid.NewGuid();
    private DateTimeOffset _created = DateTimeOffset.UtcNow;
    private string _title = NewTitle;
    private string _systemPrompt = "";
    private string? _modelId;
    private CancellationTokenSource? _cts;
    private Task? _turn;
    private Task _saves = Task.CompletedTask;
    private int _promptTokens;
    private int? _contextSize;
    private bool _started;

    public ChatViewModel(AssistantServices services, IChatHost host)
    {
        _services = services;
        _host = host;
        _services.Runtime.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshModels);
        _host.CanInsertChanged += () => Dispatcher.UIThread.Post(() =>
        {
            CanInsertChanged?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(CanUseAgent));
            ActiveFileChanged();
            if (!CanUseAgent) AgentMode = false;
        });
        Attachments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CanSend));
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        ApplySettings(host.Settings);
        RefreshModels();
    }

    /// <summary>The runtime the window runs models on (the Models panel loads and unloads through it too).</summary>
    public IModelRuntime Runtime => _services.Runtime;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];

    public ObservableCollection<ConversationItem> History { get; } = [];

    public ObservableCollection<ModelChoice> Models { get; } = [];

    public ObservableCollection<AttachmentViewModel> Attachments { get; } = [];

    public ObservableCollection<PromptChoice> PromptChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    private string _draft = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(IsIdle))]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _historyOpen;

    [ObservableProperty]
    private string _currentTitle = NewTitle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend), nameof(ModelLabel), nameof(AcceptsImages), nameof(HasSelectedModel), nameof(ComposerHint))]
    private ModelChoice? _selectedModel;

    [ObservableProperty]
    private string _contextText = "";

    [ObservableProperty]
    private double _contextFraction;

    [ObservableProperty]
    private bool _hasContext;

    [ObservableProperty]
    private bool _contextHigh;

    [ObservableProperty]
    private string _promptLabel = "No system prompt";

    [ObservableProperty]
    private string _composerNote = "";

    [ObservableProperty]
    private SystemPromptsViewModel? _promptEditor;

    /// <summary>An installed model that only completes code, named when no chat model is installed: the empty state says why the chat is empty.</summary>
    private string? _completionOnly;

    public bool IsIdle => !IsStreaming;

    public bool HasModels => Models.Count > 0;

    public bool HasSelectedModel => SelectedModel is not null;

    public bool AcceptsImages => SelectedModel?.AcceptsImages == true;

    public bool CanSend => !IsStreaming && SelectedModel is not null && (Draft.Trim().Length > 0 || Attachments.Count > 0);

    public bool IsEmpty => Messages.Count == 0;

    public string ModelLabel => SelectedModel is null ? "No model" : SelectedModel.Name;

    public string ComposerHint => SelectedModel is not null ? "Ask anything · Enter sends · /clear starts over"
        : _completionOnly is not null ? "Install a chat model to start chatting" : "Install a model to start chatting";

    public string NoModelTitle => _completionOnly is null ? "No model yet" : "No chat model yet";

    public string NoModelText => _completionOnly is { } name
        ? $"{name} only completes code as you type: it cannot hold a conversation. Choose a chat model that fits this PC."
        : "Wicked will suggest two or three models that fit this PC, and keep them up to date.";

    // ── Starting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lists the saved conversations on a worker and reopens the newest.</summary>
    /// <summary>The chat open now, as the session keeps it: its id, or "" when it is a fresh one with nothing said yet.</summary>
    public string OpenConversation => Messages.Count == 0 ? "" : _conversationId.ToString();

    /// <summary>
    /// Lists the saved conversations and reopens the one open when Wicked last closed (<paramref name="reopen"/>): "" keeps a
    /// fresh chat (the user had cleared it), null (nothing recorded) opens the newest, as before.
    /// </summary>
    public async Task StartAsync(string? reopen = null)
    {
        if (_started) return;
        _started = true;
        IReadOnlyList<ConversationSummary> list;
        try
        {
            list = await Task.Run(() => _services.Conversations.ListAsync());
        }
        catch (Exception ex)
        {
            Log.Error("The saved conversations could not be listed", ex);
            return;
        }

        foreach (var summary in list.OrderByDescending(s => s.Updated)) History.Add(new ConversationItem(summary.Id, this) { Title = summary.Title, Updated = summary.Updated });
        if (Messages.Count > 0 || History.Count == 0) return;
        var wanted = reopen switch
        {
            null => History[0].Id,
            "" => (Guid?)null,
            _ => Guid.TryParse(reopen, out var id) && History.Any(h => h.Id == id) ? id : null,
        };
        if (wanted is { } open) await OpenAsync(open);
    }

    // ── Models ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Follows the runtime's snapshot: each chat model's state, and which one the chat is using. Completion-only models are not listed.</summary>
    internal void RefreshModels()
    {
        var all = _services.Runtime.Models;
        var snapshot = all.Where(m => m.Chats).ToList();
        _completionOnly = snapshot.Count == 0 ? all.FirstOrDefault(m => m.Completes)?.DisplayName : null;
        for (var i = Models.Count - 1; i >= 0; i--)
        {
            if (snapshot.All(m => m.Id != Models[i].Id)) Models.RemoveAt(i);
        }

        foreach (var model in snapshot)
        {
            var choice = Models.FirstOrDefault(c => c.Id == model.Id);
            if (choice is null) Models.Add(choice = new ModelChoice(model.Id, this));
            choice.Name = model.DisplayName;
            choice.State = model.State;
            choice.Progress = model.LoadProgress;
            choice.AcceptsImages = model.AcceptsImages;
            choice.Reasons = model.Reasons;
            choice.ContextSize = model.ContextSize;
            choice.Error = model.Error;
        }

        // The chat keeps the model it has; a missing one gives way to a loaded model, else the first.
        var wanted = Models.FirstOrDefault(m => m.Id == _modelId) ?? Models.FirstOrDefault(m => m.IsLoaded) ?? Models.FirstOrDefault();
        foreach (var m in Models) m.IsSelected = ReferenceEquals(m, wanted);
        if (!ReferenceEquals(wanted, SelectedModel)) SelectedModel = wanted;
        else
        {
            OnPropertyChanged(nameof(ModelLabel));
            OnPropertyChanged(nameof(AcceptsImages));
        }

        if (wanted is not null && _modelId is null) _modelId = wanted.Id;
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(NoModelTitle));
        OnPropertyChanged(nameof(NoModelText));
        OnPropertyChanged(nameof(ComposerHint));
        OnPropertyChanged(nameof(CanSend));
        if (IsStreaming && Messages.LastOrDefault() is { IsAssistant: true, IsStreaming: true } last) last.Status = WaitingText();
        if (SelectedModel?.ContextSize is { } size && _promptTokens > 0) ShowContext(_promptTokens, size);
    }

    internal void SelectModel(ModelChoice choice)
    {
        _modelId = choice.Id;
        foreach (var m in Models) m.IsSelected = ReferenceEquals(m, choice);
        SelectedModel = choice;
        if (!AcceptsImages && Attachments.Count > 0)
        {
            foreach (var a in Attachments.ToList()) RemoveAttachment(a);
            ComposerNote = $"{choice.Name} does not take images, so the attached pictures were removed.";
        }

        if (Messages.Count > 0) Save();
    }

    [RelayCommand]
    private void ShowModels() => _host.ShowModels();

    private string WaitingText() => SelectedModel switch
    {
        { State: RunState.Loading } m => $"Loading {m.Name}… {(m.Progress is { } p ? p.ToString("0%", CultureInfo.InvariantCulture) : "")}".TrimEnd(),
        { State: RunState.Failed } m => $"{m.Name} could not be loaded",
        _ => "Waiting for the model…",
    };

    // ── Sending ──────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Send()
    {
        // /clear (or /new), as in VS Code's chat: a fresh conversation, an empty context; the old one stays in Past chats.
        if (Draft.Trim() is "/clear" or "/new" && !IsStreaming)
        {
            Draft = "";
            await NewChat();
            return;
        }

        if (!CanSend) return;
        var text = Draft.Trim();
        var images = Attachments.Select(a => a.Image).ToList();
        Draft = "";
        Attachments.Clear();
        ComposerNote = "";
        var user = new ChatMessageViewModel(this, ChatRole.User, text, images);
        Messages.Add(user);
        var sent = await _host.WithContextAsync(text, IncludeActiveFile && _host.ActiveFileName is not null);
        if (sent != text) user.SentText = sent;
        if (Messages.Count == 1 && _title == NewTitle) SetTitle(TitleFrom(text));
        await RunReplyAsync();
    }

    /// <summary>Esc or the Stop button: the reply stops where it is and what arrived stays.</summary>
    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>Stops the reply under way and waits for it to finish.</summary>
    public async Task StopAndWaitAsync()
    {
        _cts?.Cancel();
        if (_turn is { } turn) await turn;
    }

    private async Task RunReplyAsync()
    {
        if (SelectedModel is not { } model) return;
        var assistant = new ChatMessageViewModel(this, ChatRole.Assistant, "") { IsStreaming = true, Status = WaitingText() };
        Messages.Add(assistant);
        IsStreaming = true;
        RefreshFlags();
        var request = BuildRequest(model, assistant);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var turn = AgentMode && _host.Agent is { } tools ? AgentPumpAsync(request, assistant, tools, ct) : PumpAsync(request, assistant, ct);
        _turn = turn;
        TurnResult result;
        try
        {
            result = await turn;
        }
        finally
        {
            _turn = null;
        }

        var footer = FooterFor(result);
        assistant.Complete(result.Content, result.Reasoning, result.Frame, footer, result.Error);
        assistant.Status = "";
        IsStreaming = false;
        if (result.Usage is { } usage)
        {
            _contextSize = usage.ContextSize ?? model.ContextSize;
            if (_contextSize is { } size) ShowContext(usage.PromptTokens, size);
        }

        RefreshFlags();
        Save();
    }

    private ChatRequest BuildRequest(ModelChoice model, ChatMessageViewModel upTo)
    {
        var messages = new List<ChatMessage>();
        if (_systemPrompt.Length > 0) messages.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        foreach (var m in Messages)
        {
            if (ReferenceEquals(m, upTo)) break;
            if (m.IsAssistant && m.Text.Length == 0 && m.Transcript.Count == 0) continue;
            // An agent turn: its tool calls and their answers before what it said.
            if (m.IsAssistant) messages.AddRange(m.Transcript);
            if (m.IsAssistant && m.Text.Length == 0) continue;
            // A model that cannot see does not get the pictures an earlier turn carried.
            messages.Add(new ChatMessage(m.Role, m.SentText ?? m.Text, model.AcceptsImages && m.Images.Count > 0 ? m.Images : null));
        }

        return new ChatRequest(model.Id, messages);
    }

    private sealed record TurnResult(string Content, string Reasoning, ChatUsage? Usage, string Reason, string? Error, RenderFrame Frame, TimeSpan Took);

    /// <summary>Runs the stream on a worker: deltas into the renderer, which parses and hands the UI a frame per refresh.</summary>
    private Task<TurnResult> PumpAsync(ChatRequest request, ChatMessageViewModel target, CancellationToken ct) => Task.Run(async () =>
    {
        var renderer = new StreamRenderer(target.PostFrame);
        renderer.Start();
        var content = new StringBuilder();
        var reasoning = new StringBuilder();
        ChatUsage? usage = null;
        var reason = "stop";
        string? error = null;
        var clock = Stopwatch.StartNew();
        try
        {
            await foreach (var chunk in _services.Chat.StreamAsync(request, ct).WithCancellation(ct).ConfigureAwait(false))
            {
                switch (chunk)
                {
                    case ContentDelta delta:
                        content.Append(delta.Text);
                        renderer.AppendContent(delta.Text);
                        break;
                    case ReasoningDelta delta:
                        reasoning.Append(delta.Text);
                        renderer.AppendReasoning(delta.Text);
                        break;
                    case ChatUsage u:
                        usage = u;
                        break;
                    case ChatFinished finished:
                        reason = finished.Reason;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            reason = "cancelled";
        }
        catch (ChatException ex)
        {
            error = ex.Message;
            reason = "error";
        }
        catch (Exception ex)
        {
            Log.Error("A chat reply failed", ex);
            error = $"The reply failed: {ex.Message}";
            reason = "error";
        }

        var frame = await renderer.CompleteAsync().ConfigureAwait(false);
        return new TurnResult(content.ToString(), reasoning.ToString(), usage, reason, error, frame, clock.Elapsed);
    });

    private static string FooterFor(TurnResult result)
    {
        var parts = new List<string>();
        if (result.Usage is { } usage)
        {
            var rate = usage.TokensPerSecond ?? (result.Took.TotalSeconds > 0.2 ? usage.CompletionTokens / result.Took.TotalSeconds : (double?)null);
            if (rate is { } r && r > 0) parts.Add($"{r.ToString("0.0", CultureInfo.InvariantCulture)} tok/s");
            parts.Add($"{usage.CompletionTokens.ToString(CultureInfo.InvariantCulture)} tokens");
        }

        if (result.Reason == "cancelled") parts.Add("stopped");
        else if (result.Reason == "length") parts.Add("cut off at the length limit");
        return string.Join(" · ", parts);
    }

    private void ShowContext(int promptTokens, int contextSize)
    {
        _promptTokens = promptTokens;
        HasContext = contextSize > 0 && promptTokens > 0;
        ContextFraction = contextSize > 0 ? Math.Clamp(promptTokens / (double)contextSize, 0, 1) : 0;
        ContextHigh = ContextFraction >= 0.85;
        ContextText = $"{Compact(promptTokens)} of {Compact(contextSize)} tokens";
    }

    private static string Compact(int n) => n >= 10_000 ? $"{(n / 1000.0).ToString("0", CultureInfo.InvariantCulture)}k" : n >= 1000 ? $"{(n / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)}k" : n.ToString(CultureInfo.InvariantCulture);

    private void RefreshFlags()
    {
        var lastAssistant = Messages.Count > 0 && Messages[^1].IsAssistant ? Messages[^1] : null;
        foreach (var m in Messages)
        {
            m.CanChange = !IsStreaming;
            m.IsLastAssistant = ReferenceEquals(m, lastAssistant);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    // ── Per-message actions ──────────────────────────────────────────────────────────────────────

    internal void CopyMessage(ChatMessageViewModel message) => _ = _host.SetClipboardAsync(message.Text);

    /// <summary>Regenerate (the last reply) and Retry (a reply that failed): the reply is dropped and asked for again.</summary>
    internal async Task RegenerateAsync(ChatMessageViewModel message)
    {
        if (IsStreaming) return;
        var index = Messages.IndexOf(message);
        if (index < 0) return;
        while (Messages.Count > index) Messages.RemoveAt(Messages.Count - 1);
        if (Messages.Count > 0 && Messages[^1].IsUser) await RunReplyAsync();
    }

    /// <summary>Edit and resend: the turns after the edited message are dropped and the model answers the new text.</summary>
    internal async Task EditAndResendAsync(ChatMessageViewModel message, string text)
    {
        text = text.Trim();
        if (IsStreaming || text.Length == 0) return;
        var index = Messages.IndexOf(message);
        if (index < 0) return;
        while (Messages.Count > index + 1) Messages.RemoveAt(Messages.Count - 1);
        var oldTitle = TitleFrom(message.Text);
        message.Text = text;
        if (index == 0 && _title == oldTitle) SetTitle(TitleFrom(text));
        await RunReplyAsync();
    }

    // ── The composer: pictures ───────────────────────────────────────────────────────────────────

    /// <summary>Puts a picture with the next message; refused, with a note, when the model cannot see.</summary>
    public bool AddImage(string name, byte[] data, string mediaType)
    {
        if (!AcceptsImages)
        {
            ComposerNote = SelectedModel is null ? "Choose a model first." : $"{SelectedModel.Name} does not take images.";
            return false;
        }

        if (data.Length > MaxImageBytes)
        {
            ComposerNote = $"{name} is too large to attach (25 MB at most).";
            return false;
        }

        Bitmap? thumbnail = null;
        try
        {
            using var stream = new MemoryStream(data);
            thumbnail = Bitmap.DecodeToWidth(stream, 120);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException or IOException)
        {
            ComposerNote = $"{name} is not a picture Wicked can read.";
            return false;
        }

        ComposerNote = "";
        Attachments.Add(new AttachmentViewModel(this, name, new ChatImage(mediaType, data), thumbnail));
        return true;
    }

    /// <summary>Pictures dropped or chosen from disk: read on a worker, then attached.</summary>
    public async Task AddImageFilesAsync(IEnumerable<string> paths)
    {
        if (!AcceptsImages)
        {
            ComposerNote = SelectedModel is null ? "Choose a model first." : $"{SelectedModel.Name} does not take images.";
            return;
        }

        foreach (var path in paths)
        {
            var type = MediaTypeOf(path);
            if (type is null)
            {
                ComposerNote = $"{Path.GetFileName(path)} is not a picture type Wicked can attach.";
                continue;
            }

            byte[] data;
            try
            {
                data = await Task.Run(() => File.ReadAllBytes(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ComposerNote = $"{Path.GetFileName(path)} could not be read: {ex.Message}";
                continue;
            }

            AddImage(Path.GetFileName(path), data, type);
        }
    }

    public static string? MediaTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        _ => null,
    };

    internal void RemoveAttachment(AttachmentViewModel attachment) => Attachments.Remove(attachment);

    // ── Conversations ────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleHistory() => HistoryOpen = !HistoryOpen;

    /// <summary>Ctrl+N in the panel: an empty conversation. The one being left was saved when its reply finished.</summary>
    [RelayCommand]
    public async Task NewChat()
    {
        await StopAndWaitAsync();
        _conversationId = Guid.NewGuid();
        _created = DateTimeOffset.UtcNow;
        Messages.Clear();
        Attachments.Clear();
        SetTitle(NewTitle);
        _systemPrompt = DefaultPromptText();
        _promptTokens = 0;
        HasContext = false;
        ComposerNote = "";
        HistoryOpen = false;
        MarkCurrent();
        RefreshPromptState();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public async Task OpenAsync(Guid id)
    {
        await StopAndWaitAsync();
        Conversation? conversation;
        try
        {
            conversation = await Task.Run(() => _services.Conversations.GetAsync(id));
        }
        catch (Exception ex)
        {
            Log.Error("A saved conversation could not be opened", ex);
            return;
        }

        if (conversation is null) return;
        // Markdown for every reply is parsed on a worker, so opening a long conversation does not hold the window.
        var parsed = await Task.Run(() => conversation.Messages.Select(m => (
            Content: m.Role == ChatRole.Assistant ? MarkdownParser.Parse(m.Content) : (IReadOnlyList<MdBlock>)[],
            Reasoning: string.IsNullOrEmpty(m.Reasoning) ? null : MarkdownParser.Parse(m.Reasoning))).ToList());
        _conversationId = conversation.Id;
        _created = conversation.Created;
        SetTitle(conversation.Title);
        _systemPrompt = conversation.SystemPrompt ?? "";
        if (conversation.Model is { } model) _modelId = model;
        Messages.Clear();
        Attachments.Clear();
        for (var i = 0; i < conversation.Messages.Count; i++)
        {
            var m = conversation.Messages[i];
            if (m.Role == ChatRole.System) continue;
            var vm = new ChatMessageViewModel(this, m.Role, m.Content, m.Images, m.Reasoning);
            vm.Show(parsed[i].Content, parsed[i].Reasoning);
            Messages.Add(vm);
        }

        _promptTokens = 0;
        HasContext = false;
        HistoryOpen = false;
        RefreshFlags();
        RefreshModels();
        MarkCurrent();
        RefreshPromptState();
    }

    internal async Task RenameAsync(ConversationItem item)
    {
        var name = await _host.AskNameAsync("Rename chat", item.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        item.Title = name;
        if (item.Id == _conversationId)
        {
            SetTitle(name);
            Save();
            return;
        }

        var conversation = await Task.Run(() => _services.Conversations.GetAsync(item.Id));
        if (conversation is null) return;
        var renamed = conversation with { Title = name };
        await EnqueueSaveAsync(renamed);
    }

    internal async Task DeleteAsync(ConversationItem item)
    {
        if (!await _host.ConfirmAsync($"Delete \"{item.Title}\"?", "The conversation is removed for good.", "Delete")) return;
        var current = item.Id == _conversationId;
        if (current) await StopAndWaitAsync();
        try
        {
            await _saves;
            await Task.Run(() => _services.Conversations.DeleteAsync(item.Id));
        }
        catch (Exception ex)
        {
            Log.Error("A conversation could not be deleted", ex);
            return;
        }

        History.Remove(item);
        if (current) await NewChat();
    }

    private void MarkCurrent()
    {
        foreach (var item in History) item.IsCurrent = item.Id == _conversationId;
    }

    private void SetTitle(string title)
    {
        _title = title;
        CurrentTitle = title;
    }

    /// <summary>The conversation's title from its first message: the first line, tidied, cut at a sensible length.</summary>
    public static string TitleFrom(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (line.Length == 0) return NewTitle;
        return line.Length <= 48 ? line : line[..47].TrimEnd() + "…";
    }

    /// <summary>The conversation as it stands, saved on a worker. Saves go one after another, in order.</summary>
    private void Save()
    {
        if (Messages.Count == 0) return;
        var messages = new List<ChatMessage>();
        foreach (var m in Messages)
        {
            if (m.IsAssistant && m.Text.Length == 0 && m.ReasoningText.Length == 0) continue;
            messages.Add(new ChatMessage(m.Role, m.Text, m.Images.Count > 0 ? m.Images : null, m.ReasoningText.Length > 0 ? m.ReasoningText : null));
        }

        var conversation = new Conversation(_conversationId, _title, _modelId, _systemPrompt.Length > 0 ? _systemPrompt : null, messages, _created, DateTimeOffset.UtcNow);
        _ = EnqueueSaveAsync(conversation);
        ShowInHistory(conversation);
    }

    private Task EnqueueSaveAsync(Conversation conversation)
    {
        var previous = _saves;
        var save = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // an earlier save's failure was already reported
            }

            try
            {
                await _services.Conversations.SaveAsync(conversation).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("A conversation could not be saved", ex);
            }
        });
        _saves = save;
        return save;
    }

    private void ShowInHistory(Conversation conversation)
    {
        var item = History.FirstOrDefault(h => h.Id == conversation.Id);
        if (item is null)
        {
            item = new ConversationItem(conversation.Id, this);
            History.Insert(0, item);
        }
        else if (History.IndexOf(item) != 0)
        {
            History.Move(History.IndexOf(item), 0);
        }

        item.Title = conversation.Title;
        item.Updated = conversation.Updated;
        item.IsCurrent = true;
        foreach (var other in History.Where(h => h != item)) other.IsCurrent = false;
    }

    /// <summary>Waits for the saves queued so far (the window closing, a test).</summary>
    public Task SavedAsync() => _saves;

    internal static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.UtcNow - when;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (span < TimeSpan.FromHours(1)) return $"{(int)span.TotalMinutes} min ago";
        if (span < TimeSpan.FromDays(1)) return $"{(int)span.TotalHours} h ago";
        if (span < TimeSpan.FromDays(2)) return "yesterday";
        return when.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }

    // ── System prompts ───────────────────────────────────────────────────────────────────────────

    private IReadOnlyList<PromptPreset> _presets = [];
    private string _defaultPrompt = "";

    /// <summary>Settings changed: the system prompt presets and the code colours.</summary>
    public void ApplySettings(WickedSettings settings)
    {
        ChatHighlighter.Theme = Wicked.App.Editor.EditorServices.CodeTheme(settings.CodeColorTheme);
        _presets = ChatSettings.Prompts(settings);
        _defaultPrompt = ChatSettings.DefaultName(settings);
        // A chat that has not begun starts with the default prompt.
        if (Messages.Count == 0) _systemPrompt = DefaultPromptText();
        RefreshPromptState();
    }

    private string DefaultPromptText() => _presets.FirstOrDefault(p => p.Name == _defaultPrompt)?.Prompt ?? "";

    private void RefreshPromptState()
    {
        PromptChoices.Clear();
        PromptChoices.Add(new PromptChoice(this, null) { IsCurrent = _systemPrompt.Length == 0 });
        foreach (var preset in _presets) PromptChoices.Add(new PromptChoice(this, preset) { IsCurrent = _systemPrompt == preset.Prompt && _systemPrompt.Length > 0 });
        PromptLabel = _systemPrompt.Length == 0 ? "System prompt"
            : _presets.FirstOrDefault(p => p.Prompt == _systemPrompt)?.Name ?? "Custom prompt";
        OnPropertyChanged(nameof(SystemPrompt));
    }

    /// <summary>The system prompt this chat sends, shown and edited in the prompt chip's box; a preset fills it.</summary>
    public string SystemPrompt
    {
        get => _systemPrompt;
        set
        {
            value ??= "";
            if (value == _systemPrompt) return;
            _systemPrompt = value;
            RefreshPromptState();
        }
    }

    /// <summary>The prompt box closed: a chat already under way keeps what was typed.</summary>
    public void SystemPromptEdited()
    {
        if (Messages.Count > 0) Save();
    }

    internal void SelectPrompt(PromptPreset? preset)
    {
        _systemPrompt = preset?.Prompt ?? "";
        RefreshPromptState();
        if (Messages.Count > 0) Save();
    }

    [RelayCommand]
    private void EditPrompts() => PromptEditor = new SystemPromptsViewModel(this, _presets, _defaultPrompt);

    internal async Task SavePromptsAsync(IReadOnlyList<PromptPreset> presets, string defaultName)
    {
        PromptEditor = null;
        await _host.UpdateSettingsAsync(s => ChatSettings.With(s, presets, defaultName));
    }

    // ── Code blocks ──────────────────────────────────────────────────────────────────────────────

    public event EventHandler? CanInsertChanged;

    public bool CanInsert => _host.CanInsert;

    void ICodeActions.Copy(string text) => _ = _host.SetClipboardAsync(text);

    void ICodeActions.Insert(string text) => _host.InsertIntoEditor(text);

    void ICodeActions.Apply(string text) => _host.ApplyToEditor(text);

    void ICodeActions.OpenLink(string url) => _host.OpenLink(url);

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}

/// <summary>The system prompt presets in an editor: add, rename, rewrite, delete, and choose the one new chats start with.</summary>
public sealed partial class SystemPromptsViewModel : ObservableObject
{
    private readonly ChatViewModel _owner;

    public SystemPromptsViewModel(ChatViewModel owner, IReadOnlyList<PromptPreset> presets, string defaultName)
    {
        _owner = owner;
        foreach (var preset in presets) Items.Add(new PromptEdit { Name = preset.Name, Prompt = preset.Prompt, IsDefault = preset.Name == defaultName });
        // One prompt is the default: choosing another takes it from the first.
        Items.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<PromptEdit>() ?? []) added.PropertyChanged += OnItemChanged;
        };
        foreach (var item in Items) item.PropertyChanged += OnItemChanged;
        SelectedItem = Items.FirstOrDefault();
    }

    public ObservableCollection<PromptEdit> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private PromptEdit? _selectedItem;

    [ObservableProperty]
    private string _problem = "";

    public bool HasSelection => SelectedItem is not null;

    [RelayCommand]
    private void Add()
    {
        var n = 1;
        while (Items.Any(i => i.Name == $"Prompt {n}")) n++;
        var item = new PromptEdit { Name = $"Prompt {n}", Prompt = "" };
        Items.Add(item);
        SelectedItem = item;
    }

    [RelayCommand]
    private void Remove()
    {
        if (SelectedItem is not { } item) return;
        var index = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PromptEdit.IsDefault) || sender is not PromptEdit { IsDefault: true } chosen) return;
        foreach (var other in Items.Where(i => !ReferenceEquals(i, chosen))) other.IsDefault = false;
    }

    [RelayCommand]
    private Task Save()
    {
        var names = Items.Select(i => i.Name.Trim()).ToList();
        if (names.Any(n => n.Length == 0)) { Problem = "Every prompt needs a name."; return Task.CompletedTask; }
        if (names.Distinct().Count() != names.Count) { Problem = "Two prompts have the same name."; return Task.CompletedTask; }
        var presets = Items.Select(i => new PromptPreset(i.Name.Trim(), i.Prompt)).ToList();
        var def = Items.FirstOrDefault(i => i.IsDefault)?.Name.Trim() ?? "";
        return _owner.SavePromptsAsync(presets, def);
    }

    [RelayCommand]
    private void Cancel() => _owner.PromptEditor = null;
}

/// <summary>One preset being edited.</summary>
public sealed partial class PromptEdit : ObservableObject
{
    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _prompt = "";

    [ObservableProperty]
    private bool _isDefault;
}
