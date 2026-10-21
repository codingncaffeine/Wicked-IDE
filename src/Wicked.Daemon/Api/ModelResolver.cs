using Wicked.Core.Chat;
using Wicked.Core.Models;
using Wicked.Core.Store;

namespace Wicked.Daemon.Api;

/// <summary>
/// Turns the name a client sends as <c>model</c> into the id the router knows (D12). A client may use the router's own id
/// (<c>publisher/repo:Q4_K_M</c>), a catalog id (<c>qwen3-coder</c>), the repo (<c>publisher/repo</c>) or just its last part
/// (<c>Repo-GGUF</c>, with or without <c>:QUANT</c>), in any case. A model with several quants answers to the one that is loaded, else
/// the first. The names offered by <c>/v1/models</c> are the ids and the catalog ids of what is installed.
/// </summary>
public static class ModelResolver
{
    public static string? Resolve(string name, ModelState state, IReadOnlyList<RunningModel> running)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        name = name.Trim();
        var variants = state.Installed.SelectMany(m => m.Variants.Select(v => (Model: m, Variant: v))).ToList();

        // The router's own id, exactly, then in any case.
        if (variants.FirstOrDefault(v => v.Variant.Id == name) is { Variant: not null } exact) return exact.Variant.Id;
        if (variants.FirstOrDefault(v => string.Equals(v.Variant.Id, name, StringComparison.OrdinalIgnoreCase)) is { Variant: not null } folded) return folded.Variant.Id;

        // A model by repo, catalog id or last part of the repo; ":QUANT" narrows it to one file.
        var colon = name.LastIndexOf(':');
        var quant = colon > 0 && !name[(colon + 1)..].Contains('/', StringComparison.Ordinal) ? name[(colon + 1)..] : null; // not a path: a model id and quant
        var bare = quant is null ? name : name[..colon];
        bool Names(StoredModel m) =>
            string.Equals(m.Id, bare, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Manifest?.CatalogId, bare, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.Id[(m.Id.IndexOf('/') + 1)..], bare, StringComparison.OrdinalIgnoreCase); // not a path: a model id
        var candidates = variants.Where(v => Names(v.Model) && (quant is null || string.Equals(v.Variant.Quant, quant, StringComparison.OrdinalIgnoreCase))).ToList();
        if (candidates.Count == 0) return null;
        var loaded = candidates.FirstOrDefault(c => running.Any(r => r.Id == c.Variant.Id && r.State is RunState.Loaded or RunState.Loading or RunState.Sleeping));
        return (loaded.Variant is not null ? loaded : candidates[0]).Variant.Id;
    }

    /// <summary>Every name <c>/v1/models</c> lists: each installed file's id, and the catalog id of each model that has one.</summary>
    public static IReadOnlyList<(string Id, string Owner, DateTimeOffset Created, string? AliasOf)> Names(ModelState state)
    {
        var names = new List<(string, string, DateTimeOffset, string?)>();
        foreach (var model in state.Installed)
        {
            var owner = model.Id.Contains('/', StringComparison.Ordinal) ? model.Id[..model.Id.IndexOf('/')] : "local"; // not a path: a model id
            var created = model.Manifest?.InstalledAt ?? DateTimeOffset.UnixEpoch;
            foreach (var variant in model.Variants) names.Add((variant.Id, owner, created, null));
            if (model.Manifest?.CatalogId is { Length: > 0 } catalogId && model.Variants.Count > 0) names.Add((catalogId, owner, created, model.Variants[0].Id));
        }

        return names;
    }
}
