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

    /// <summary>The spot where the window gets its services: tests answer through <see cref="Factory"/>, the app takes the model half's.</summary>
    public static AssistantServices Resolve(WickedPaths paths, ModelServices models)
    {
        if (Factory?.Invoke(paths) is { } supplied) return supplied;

        return new(models.Runtime, models.Chat, models.Conversations);
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
