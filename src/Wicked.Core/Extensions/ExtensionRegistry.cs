using System.Collections.Concurrent;
using System.Text.Json;

namespace Wicked.Core.Extensions;

/// <summary>What the enabled extensions declared that other parts of the host read: today the defaults of their settings.</summary>
public sealed class ExtensionRegistry
{
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, JsonElement>> _defaults = new();

    public void SetDefaults(string owner, IReadOnlyDictionary<string, JsonElement> defaults) => _defaults[owner] = defaults;

    public void Remove(string owner) => _defaults.TryRemove(owner, out _);

    /// <summary>The default an extension declared for <paramref name="key"/>, or null.</summary>
    public JsonElement? SettingDefault(string key)
    {
        foreach (var defaults in _defaults.Values)
        {
            if (defaults.TryGetValue(key, out var value)) return value;
        }

        return null;
    }

    /// <summary>Every declared default, for a settings page.</summary>
    public IReadOnlyDictionary<string, JsonElement> AllDefaults() =>
        _defaults.Values.SelectMany(d => d).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.First().Value);
}
