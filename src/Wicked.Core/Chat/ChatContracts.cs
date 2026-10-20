namespace Wicked.Core.Chat;

// The seam between running models (Wicked.Core) and the chat view (Wicked.App), Phase 4. Every member here may be called
// from any thread; nothing in an implementation blocks the caller's thread on I/O. Events are raised on a worker.

public enum ChatRole
{
    System,
    User,
    Assistant,
}

/// <summary>An image attached to a user message (vision models, through the model's mmproj). <c>MediaType</c> is e.g. <c>image/png</c>.</summary>
public sealed record ChatImage(string MediaType, byte[] Data);

/// <summary>One turn. <c>Reasoning</c> is the model's thinking for an assistant turn, kept apart from the answer and never sent back.</summary>
public sealed record ChatMessage(ChatRole Role, string Content, IReadOnlyList<ChatImage>? Images = null, string? Reasoning = null);

/// <summary>Per-request sampling; null means the model's own default (its preset, then the engine's).</summary>
public sealed record ChatSampling(double? Temperature = null, double? TopP = null, int? MaxTokens = null);

/// <summary>A chat completion for the model with id <c>Model</c> (the id <see cref="IModelRuntime"/> lists).</summary>
public sealed record ChatRequest(string Model, IReadOnlyList<ChatMessage> Messages, ChatSampling? Sampling = null);

/// <summary>What a streamed reply yields, in order: deltas as they arrive, at most one <see cref="ChatUsage"/>, then exactly one <see cref="ChatFinished"/>.</summary>
public abstract record ChatChunk;

public sealed record ContentDelta(string Text) : ChatChunk;

public sealed record ReasoningDelta(string Text) : ChatChunk;

/// <summary>Token counts and speed from the engine's final event. <c>ContextSize</c> is the loaded context, for the usage meter.</summary>
public sealed record ChatUsage(int PromptTokens, int CompletionTokens, double? TokensPerSecond, int? ContextSize) : ChatChunk;

/// <summary><c>Reason</c>: <c>stop</c>, <c>length</c>, or <c>cancelled</c> (the caller's token fired; the partial reply stands).</summary>
public sealed record ChatFinished(string Reason) : ChatChunk;

/// <summary>Streams chat replies. Loads the model first when it is not loaded. Failures throw <see cref="ChatException"/> with a message fit to show.</summary>
public interface IChatEngine
{
    IAsyncEnumerable<ChatChunk> StreamAsync(ChatRequest request, CancellationToken ct = default);
}

public sealed class ChatException(string message, Exception? inner = null) : IOException(message, inner);

public enum RunState
{
    Unloaded,
    Loading,
    Loaded,
    Sleeping,
    Failed,
}

/// <summary>An installed model as the runtime sees it. <c>Error</c> is set when <c>State</c> is <see cref="RunState.Failed"/>.</summary>
public sealed record RunningModel(string Id, string DisplayName, RunState State, double? LoadProgress, bool AcceptsImages, bool Reasons, int? ContextSize, string? Error);

/// <summary>
/// The user's choices for running one model (the router preset, D3). Null = automatic: Wicked picks from the machine and the
/// model (context from what fits, GPU layers from VRAM, flash attention when the backend has it).
/// </summary>
public sealed record ModelRunSettings(
    int? ContextSize = null,
    string? GpuLayers = null,
    string? KvCacheType = null,
    bool? FlashAttention = null,
    string? DraftModelId = null,
    bool? Reasoning = null,
    ChatSampling? Sampling = null);

/// <summary>Installed models, their load state, and their run settings. The router starts on first need, not at launch.</summary>
public interface IModelRuntime
{
    /// <summary>A snapshot; replaced (never mutated) on every change.</summary>
    IReadOnlyList<RunningModel> Models { get; }

    /// <summary>Raised on a worker after <see cref="Models"/> changes.</summary>
    event EventHandler? Changed;

    Task LoadAsync(string id, CancellationToken ct = default);

    Task UnloadAsync(string id, CancellationToken ct = default);

    ModelRunSettings GetSettings(string id);

    /// <summary>Keeps the settings and rewrites the preset; a loaded model is reloaded for the change to take effect.</summary>
    Task SaveSettingsAsync(string id, ModelRunSettings settings, CancellationToken ct = default);
}

public sealed record ConversationSummary(Guid Id, string Title, string? Model, DateTimeOffset Updated);

public sealed record Conversation(Guid Id, string Title, string? Model, string? SystemPrompt, IReadOnlyList<ChatMessage> Messages, DateTimeOffset Created, DateTimeOffset Updated);

/// <summary>Saved conversations (SQLite in the profile's data folder). Newest first.</summary>
public interface IConversationStore
{
    Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken ct = default);

    Task<Conversation?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Inserts or replaces the whole conversation.</summary>
    Task SaveAsync(Conversation conversation, CancellationToken ct = default);

    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
