using Wicked.Core.Store;

namespace Wicked.Core.Catalog;

/// <summary>What a model's name says it is: an instruct or chat version, a base model, or nothing either way.</summary>
public enum NamedUse
{
    Unknown,
    Chat,
    Base,
}

/// <summary>
/// What a model is for: a conversation (the chat, the agent, inline edits) and/or code completion as you type. A completion-only
/// model is a base model trained to fill in code at the cursor: given a chat it rambles without end, so the chat never offers it.
/// </summary>
public readonly record struct ModelUse(bool Chats, bool Completes)
{
    private static readonly string[] ChatWords = ["instruct", "chat", "it", "thinking"];

    public bool CompletionOnly => Completes && !Chats;

    /// <summary>A catalog model by its capabilities.</summary>
    public static ModelUse Of(CatalogModel model) => new(model.Has(Capability.Chat), model.Has(Capability.Fim));

    /// <summary>
    /// An installed model: the catalog's word when it lists the model (by the manifest's catalog id, else the repo). A model from
    /// Hugging Face or copied in counts as a chat model unless its name says base model or its file has no chat template: a base
    /// model can carry a template too, so the file alone cannot tell a completion-only model apart.
    /// </summary>
    public static ModelUse Of(StoredModel model, StoredVariant? variant, ModelCatalog? catalog)
    {
        if (Find(model, catalog) is { } known) return Of(known);
        if (FromNames(model.Id, model.Manifest?.SourceRepo) == NamedUse.Base) return new ModelUse(false, false);
        var template = (variant ?? model.Variants.FirstOrDefault())?.Gguf?.HasChatTemplate;
        return new ModelUse(template != false, false);
    }

    public static CatalogModel? Find(StoredModel model, ModelCatalog? catalog)
    {
        if (catalog is null) return null;
        var byId = model.Manifest?.CatalogId is { } id ? catalog.Models.FirstOrDefault(m => m.Id == id) : null;
        return byId ?? catalog.Models.FirstOrDefault(m => string.Equals(m.Repo, model.Id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What the names say, the repo's and the one it was quantised from: Hugging Face has no field for it (a base model carries a
    /// chat template and the "conversational" tag as an instruct one does), so the convention of naming the chat-tuned release
    /// "-Instruct", "-Chat", "-it" or "-Thinking" and the raw one "-Base" is all there is. Either name saying chat wins.
    /// </summary>
    public static NamedUse FromNames(params string?[] ids)
    {
        var words = ids.OfType<string>()
            .Select(id => id[(id.LastIndexOf('/') + 1)..].ToLowerInvariant().Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries)) // not a path: a repo id
            .ToList();
        if (words.Any(w => w.Any(ChatWords.Contains))) return NamedUse.Chat;
        return words.Any(w => w.Contains("base")) ? NamedUse.Base : NamedUse.Unknown;
    }

    /// <summary>The repo a Hugging Face GGUF repo was quantised from (its <c>base_model:quantized:</c> tag), or null.</summary>
    public static string? QuantizedFrom(IEnumerable<string> tags) =>
        tags.FirstOrDefault(t => t.StartsWith("base_model:quantized:", StringComparison.Ordinal)) is { } tag ? tag["base_model:quantized:".Length..] : null;

    /// <summary>
    /// The words a Hugging Face repo gets before anyone downloads it: the catalog's word when it lists the repo, else what the names
    /// say, else plainly that Hugging Face does not say.
    /// </summary>
    public static string DescribeRepo(string repo, IEnumerable<string> tags, ModelCatalog? catalog)
    {
        if (catalog?.Models.FirstOrDefault(m => string.Equals(m.Repo, repo, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            return Of(known) switch
            {
                { CompletionOnly: true } => "Code completion only: fills in code as you type, cannot chat",
                { Chats: true, Completes: true } => "Chat model that also completes code",
                { Chats: true } => "Chat model",
                _ => "Not a chat model",
            };
        }

        return FromNames(repo, QuantizedFrom(tags)) switch
        {
            NamedUse.Chat => "Chat model (an instruct or chat version)",
            NamedUse.Base => "Base model: continues or fills in text, cannot chat",
            _ => "Hugging Face does not say whether it can chat: look for an Instruct or Chat version",
        };
    }
}
