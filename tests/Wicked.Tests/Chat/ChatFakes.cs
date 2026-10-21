using System.Runtime.CompilerServices;
using Wicked.App.Chat;
using Wicked.Core;
using Wicked.Core.Chat;

namespace Wicked.Tests.Chat;

/// <summary>The chat contract's other half, played by hand: a runtime, an engine and a store that do what a test says.</summary>
internal sealed class FakeRuntime : IModelRuntime
{
    private readonly Lock _gate = new();
    private IReadOnlyList<RunningModel> _models = [];
    private readonly Dictionary<string, ModelRunSettings> _settings = [];

    public IReadOnlyList<RunningModel> Models
    {
        get
        {
            lock (_gate) return _models;
        }
    }

    public event EventHandler? Changed;

    /// <summary>Loads fail with this message when set.</summary>
    public string? FailLoadsWith { get; set; }

    public List<string> Loads { get; } = [];

    public List<string> Unloads { get; } = [];

    public List<(string Id, ModelRunSettings Settings)> Saved { get; } = [];

    public void Set(params RunningModel[] models)
    {
        lock (_gate) _models = [.. models];
        Raise();
    }

    public void Update(string id, Func<RunningModel, RunningModel> change)
    {
        lock (_gate) _models = [.. _models.Select(m => m.Id == id ? change(m) : m)];
        Raise();
    }

    private void Raise() => Task.Run(() => Changed?.Invoke(this, EventArgs.Empty));

    public async Task LoadAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) Loads.Add(id);
        Update(id, m => m with { State = RunState.Loading, LoadProgress = 0.4, Error = null });
        await Task.Delay(150, ct);
        if (FailLoadsWith is { } error)
        {
            Update(id, m => m with { State = RunState.Failed, LoadProgress = null, Error = error });
            return;
        }

        Update(id, m => m with { State = RunState.Loaded, LoadProgress = null, ContextSize = 8192 });
    }

    public Task UnloadAsync(string id, CancellationToken ct = default)
    {
        lock (_gate) Unloads.Add(id);
        Update(id, m => m with { State = RunState.Unloaded });
        return Task.CompletedTask;
    }

    public ModelRunSettings GetSettings(string id)
    {
        lock (_gate) return _settings.GetValueOrDefault(id) ?? new ModelRunSettings();
    }

    public Task SaveSettingsAsync(string id, ModelRunSettings settings, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _settings[id] = settings;
            Saved.Add((id, settings));
        }

        return Task.CompletedTask;
    }

    public static RunningModel Model(string id, string name, RunState state = RunState.Loaded, bool images = false, bool reasons = false) =>
        new(id, name, state, null, images, reasons, state == RunState.Loaded ? 8192 : null, null);
}

internal sealed class FakeEngine : IChatEngine
{
    private readonly Lock _gate = new();

    public List<ChatRequest> Requests { get; } = [];

    /// <summary>Answers a request; the default echoes the last user message.</summary>
    public Func<ChatRequest, CancellationToken, IAsyncEnumerable<ChatChunk>>? Script { get; set; }

    public IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, CancellationToken ct = default)
    {
        lock (_gate) Requests.Add(request);
        return (Script ?? Echo)(request, ct);
    }

    public static async IAsyncEnumerable<ChatChunk> Echo(ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var last = request.Messages.LastOrDefault(m => m.Role == ChatRole.User)?.Content ?? "";
        await foreach (var chunk in Reply($"You said: {last}", 3, TimeSpan.FromMilliseconds(5), ct)) yield return chunk;
    }

    /// <summary>A reply in <paramref name="pieces"/> deltas, <paramref name="delay"/> apart, then its numbers and its end.</summary>
    public static async IAsyncEnumerable<ChatChunk> Reply(string text, int pieces, TimeSpan delay, [EnumeratorCancellation] CancellationToken ct = default, string? reasoning = null, TimeSpan? thinkFor = null)
    {
        if (reasoning is not null)
        {
            yield return new ReasoningDelta(reasoning[..(reasoning.Length / 2)]);
            await Task.Delay(thinkFor ?? delay);
            yield return new ReasoningDelta(reasoning[(reasoning.Length / 2)..]);
        }

        var size = Math.Max(1, (int)Math.Ceiling(text.Length / (double)pieces));
        for (var i = 0; i < text.Length; i += size)
        {
            if (ct.IsCancellationRequested)
            {
                yield return new ChatFinished("cancelled");
                yield break;
            }

            yield return new ContentDelta(text.Substring(i, Math.Min(size, text.Length - i)));
            await Task.Delay(delay);
        }

        yield return new ChatUsage(120, Math.Max(1, text.Length / 4), 42.5, 8192);
        yield return new ChatFinished("stop");
    }

    /// <summary>Words forever, a delta every <paramref name="delay"/>, until cancelled.</summary>
    public static async IAsyncEnumerable<ChatChunk> Endless(TimeSpan delay, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; !ct.IsCancellationRequested; i++)
        {
            yield return new ContentDelta($"word{i} ");
            await Task.Delay(delay);
        }

        yield return new ChatFinished("cancelled");
    }
}

internal sealed class MemoryStore : IConversationStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Conversation> _all = [];

    public int Saves { get; private set; }

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult<IReadOnlyList<ConversationSummary>>([.. _all.Values.OrderByDescending(c => c.Updated).Select(c => new ConversationSummary(c.Id, c.Title, c.Model, c.Updated))]);
    }

    public Task<Conversation?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_all.GetValueOrDefault(id));
    }

    public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _all[conversation.Id] = conversation;
            Saves++;
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) _all.Remove(id);
        return Task.CompletedTask;
    }

    public IReadOnlyList<Conversation> All
    {
        get
        {
            lock (_gate) return [.. _all.Values.OrderByDescending(c => c.Updated)];
        }
    }
}

/// <summary>Fill-in-the-middle answers a test sets: the text for a request, after a delay; every request kept.</summary>
internal sealed class FakeInfill : IInfillEngine
{
    private readonly Lock _gate = new();
    private readonly List<InfillRequest> _requests = [];

    public Func<InfillRequest, string> Answer { get; set; } = _ => "";

    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(30);

    public IReadOnlyList<InfillRequest> Requests
    {
        get
        {
            lock (_gate) return [.. _requests];
        }
    }

    public async Task<InfillResult> CompleteAsync(InfillRequest request, CancellationToken ct = default)
    {
        lock (_gate) _requests.Add(request);
        await Task.Delay(Delay, ct);
        return new InfillResult(Answer(request), 5, 120, false);
    }
}

/// <summary>The assistant's services for one test, and the hook that hands them to the window.</summary>
internal sealed class ChatWorld
{
    public FakeRuntime Runtime { get; } = new();

    public FakeEngine Engine { get; } = new();

    public MemoryStore Store { get; } = new();

    public FakeInfill Infill { get; } = new();

    public AssistantServices Services => new(Runtime, Engine, Store, Infill);

    /// <summary>The main window on a profile of its own, its assistant wired to this world. Call inside <c>Ui.Run</c>.</summary>
    public (App.Views.MainWindow Window, App.ViewModels.ShellViewModel Shell) Start(WickedPaths paths)
    {
        AssistantServices.Factory = _ => Services;
        try
        {
            return Ui.Start(paths);
        }
        finally
        {
            AssistantServices.Factory = null;
        }
    }
}
