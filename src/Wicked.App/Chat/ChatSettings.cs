using System.Text.Json;
using Wicked.Core.Settings;

namespace Wicked.App.Chat;

/// <summary>A named system prompt the user keeps for chats.</summary>
public sealed record PromptPreset(string Name, string Prompt);

/// <summary>
/// The chat's keys in settings.json: <c>chat.systemPrompts</c> (a list of <c>{ "name", "prompt" }</c>) and
/// <c>chat.defaultSystemPrompt</c> (the name of the one new chats start with). They ride in the settings file's extra
/// keys, so the file keeps them when anything else is saved.
/// </summary>
public static class ChatSettings
{
    public const string PromptsKey = "chat.systemPrompts";
    public const string DefaultKey = "chat.defaultSystemPrompt";

    public static IReadOnlyList<PromptPreset> Prompts(WickedSettings settings)
    {
        var presets = new List<PromptPreset>();
        if (settings.Other?.GetValueOrDefault(PromptsKey) is { ValueKind: JsonValueKind.Array } array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                var prompt = item.TryGetProperty("prompt", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                if (!string.IsNullOrWhiteSpace(name) && prompt is not null && presets.All(x => x.Name != name)) presets.Add(new PromptPreset(name, prompt));
            }
        }

        return presets;
    }

    /// <summary>The name of the preset new chats start with, or empty for none.</summary>
    public static string DefaultName(WickedSettings settings) =>
        settings.Other?.GetValueOrDefault(DefaultKey) is { ValueKind: JsonValueKind.String } name ? name.GetString() ?? "" : "";

    public static WickedSettings With(WickedSettings settings, IReadOnlyList<PromptPreset> presets, string defaultName)
    {
        var other = new Dictionary<string, JsonElement>(settings.Other ?? []);
        other[PromptsKey] = JsonSerializer.SerializeToElement(presets.Select(p => new { name = p.Name, prompt = p.Prompt }).ToArray());
        other[DefaultKey] = JsonSerializer.SerializeToElement(defaultName);
        return settings with { Other = other };
    }
}
