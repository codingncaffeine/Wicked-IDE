using Wicked.Core;
using Wicked.Core.Chat;
using Wicked.Core.Models;
using Wicked.Core.Settings;

namespace Wicked.App.Chat;

/// <summary>What the assistant runs on: the models, the chat engine and the saved conversations (the contract in <c>Wicked.Core.Chat</c>).</summary>
public sealed record AssistantServices(IModelRuntime Runtime, IChatEngine Chat, IConversationStore Conversations)
{
    /// <summary>Lets a test supply fakes; the app leaves it null.</summary>
    public static Func<WickedPaths, AssistantServices>? Factory { get; set; }

    /// <summary>
    /// THE spot where the window gets its services. Tests answer through <see cref="Factory"/>. The app takes what the model
    /// half provides: until its runtime, engine and store are merged in, a stand-in that lists no models and says so.
    /// </summary>
    public static AssistantServices Resolve(WickedPaths paths, ModelServices models)
    {
        if (Factory?.Invoke(paths) is { } supplied) return supplied;

        // MERGE POINT: return new(models.Runtime, models.Chat, models.Conversations) once ModelServices provides them.
        _ = models;
        return Unwired();
    }

    private static AssistantServices Unwired() => new(new NoModelRuntime(), new NoChatEngine(), new MemoryConversations());
}

/// <summary>Stands in for the runtime until the model half is wired in: no model can be loaded.</summary>
internal sealed class NoModelRuntime : IModelRuntime
{
    public IReadOnlyList<RunningModel> Models => [];

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task LoadAsync(string id, CancellationToken ct = default) => throw new InvalidOperationException("Running models is not available in this build yet.");

    public Task UnloadAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    public ModelRunSettings GetSettings(string id) => new();

    public Task SaveSettingsAsync(string id, ModelRunSettings settings, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class NoChatEngine : IChatEngine
{
    public IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, CancellationToken ct = default) =>
        throw new ChatException("Running models is not available in this build yet.");
}

/// <summary>Conversations kept for the life of the window, until the model half's store replaces it.</summary>
internal sealed class MemoryConversations : IConversationStore
{
    private readonly Dictionary<Guid, Conversation> _all = [];
    private readonly Lock _gate = new();

    public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<ConversationSummary>>([.. _all.Values.OrderByDescending(c => c.Updated).Select(c => new ConversationSummary(c.Id, c.Title, c.Model, c.Updated))]);
        }
    }

    public Task<Conversation?> GetAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) return Task.FromResult(_all.GetValueOrDefault(id));
    }

    public Task SaveAsync(Conversation conversation, CancellationToken ct = default)
    {
        lock (_gate) _all[conversation.Id] = conversation;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        lock (_gate) _all.Remove(id);
        return Task.CompletedTask;
    }
}

/// <summary>What the chat needs from the window around it; the shell provides it and a test can stand in.</summary>
public interface IChatHost
{
    /// <summary>The settings in force (the system prompt presets live in them).</summary>
    WickedSettings Settings { get; }

    Task UpdateSettingsAsync(Func<WickedSettings, WickedSettings> change);

    /// <summary>Whether there is an open file a code block can be inserted into.</summary>
    bool CanInsert { get; }

    event Action? CanInsertChanged;

    /// <summary>Puts code in the active editor at the caret, replacing the selection.</summary>
    void InsertIntoEditor(string text);

    Task SetClipboardAsync(string text);

    Task<bool> ConfirmAsync(string message, string detail, string confirm);

    Task<string?> AskNameAsync(string prompt, string initial);

    void OpenLink(string url);

    /// <summary>Goes to the Models panel.</summary>
    void ShowModels();
}
