using Wicked.Core.Store;

namespace Wicked.Core.Catalog;

/// <summary>
/// What a model is for: a conversation (the chat, the agent, inline edits) and/or code completion as you type. A completion-only
/// model is a base model trained to fill in code at the cursor: given a chat it rambles without end, so the chat never offers it.
/// </summary>
public readonly record struct ModelUse(bool Chats, bool Completes)
{
    public bool CompletionOnly => Completes && !Chats;

    /// <summary>A catalog model by its capabilities.</summary>
    public static ModelUse Of(CatalogModel model) => new(model.Has(Capability.Chat), model.Has(Capability.Fim));

    /// <summary>
    /// An installed model: the catalog's word when it lists the model (by the manifest's catalog id, else the repo). A model from
    /// Hugging Face or copied in counts as a chat model unless its file has no chat template: a base model can carry one too, so
    /// the file cannot tell a completion-only model apart; only the catalog knows that.
    /// </summary>
    public static ModelUse Of(StoredModel model, StoredVariant? variant, ModelCatalog? catalog)
    {
        if (Find(model, catalog) is { } known) return Of(known);
        var template = (variant ?? model.Variants.FirstOrDefault())?.Gguf?.HasChatTemplate;
        return new ModelUse(template != false, false);
    }

    public static CatalogModel? Find(StoredModel model, ModelCatalog? catalog)
    {
        if (catalog is null) return null;
        var byId = model.Manifest?.CatalogId is { } id ? catalog.Models.FirstOrDefault(m => m.Id == id) : null;
        return byId ?? catalog.Models.FirstOrDefault(m => string.Equals(m.Repo, model.Id, StringComparison.OrdinalIgnoreCase));
    }
}
