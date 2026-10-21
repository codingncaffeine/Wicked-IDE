using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Wicked.Core.Settings;

/// <summary>What the Settings editor and settings.json's schema say about one setting of <see cref="WickedSettings"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingAttribute(string description) : Attribute
{
    /// <summary>One or two sentences, as the Settings editor shows under the setting's name.</summary>
    public string Description { get; } = description;

    /// <summary>The values a text setting takes (a list to pick from), as VS Code's "enum".</summary>
    public string[]? Values { get; init; }

    /// <summary>The smallest value a number takes; NaN for no limit.</summary>
    public double Minimum { get; init; } = double.NaN;

    /// <summary>The largest value a number takes; NaN for no limit.</summary>
    public double Maximum { get; init; } = double.NaN;

    /// <summary>A secret: the editor shows it masked and never copies it as JSON.</summary>
    public bool Secret { get; init; }
}

/// <summary>How a setting is edited.</summary>
public enum SettingKind
{
    Toggle,
    Integer,
    Number,
    Text,
    Choice,

    /// <summary>A list or an object: edited in settings.json.</summary>
    Json,
}

/// <summary>One setting: its key, where it is listed, what it says, how it is edited and its default.</summary>
public sealed record SettingInfo(string Key, string Group, string Title, string Description, SettingKind Kind, IReadOnlyList<string> Values,
    double Minimum, double Maximum, bool Secret, PropertyInfo Property)
{
    /// <summary>The default as JSON ("true", "14", "\"off\"").</summary>
    public string DefaultJson => JsonSerializer.Serialize(Property.GetValue(SettingsSchema.Defaults), Property.PropertyType, WickedSettings.Json);

    /// <summary>The value in <paramref name="settings"/>.</summary>
    public object? ValueIn(WickedSettings settings) => Property.GetValue(settings);

    /// <summary>The value differs from the default (the editor marks it and offers Reset).</summary>
    public bool IsModified(WickedSettings settings) =>
        JsonSerializer.Serialize(ValueIn(settings), Property.PropertyType, WickedSettings.Json) != DefaultJson;
}

/// <summary>
/// Every setting of <see cref="WickedSettings"/>, read from the type itself (its JSON names and <see cref="SettingAttribute"/>s),
/// so the Settings editor and settings.json's schema can never miss one. Changes go through JSON: a setting set or reset
/// is the settings with that one key changed.
/// </summary>
public static class SettingsSchema
{
    public static WickedSettings Defaults { get; } = new();

    private static readonly Dictionary<string, string> Groups = new(StringComparer.Ordinal)
    {
        ["editor"] = "Text Editor",
        ["breadcrumbs"] = "Workbench",
        ["workbench"] = "Workbench",
        ["window"] = "Window",
        ["files"] = "Files",
        ["terminal"] = "Terminal",
        ["debug"] = "Debug",
        ["lsp"] = "Language Servers",
        ["wicked"] = "Assistant",
        ["models"] = "Models",
        ["huggingface"] = "Models",
        ["runtime"] = "Models",
        ["server"] = "Server",
    };

    /// <summary>The groups in the order the editor lists them.</summary>
    public static IReadOnlyList<string> GroupOrder { get; } =
        ["Text Editor", "Workbench", "Window", "Files", "Terminal", "Debug", "Language Servers", "Assistant", "Models", "Server"];

    private static readonly Lazy<IReadOnlyList<SettingInfo>> AllSettings = new(Read);

    /// <summary>Every setting, grouped in <see cref="GroupOrder"/>, in the order the type declares them within a group.</summary>
    public static IReadOnlyList<SettingInfo> All => AllSettings.Value;

    public static SettingInfo? Find(string key) => All.FirstOrDefault(s => s.Key == key);

    private static IReadOnlyList<SettingInfo> Read()
    {
        var list = new List<SettingInfo>();
        foreach (var p in typeof(WickedSettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetCustomAttribute<JsonPropertyNameAttribute>() is not { } name) continue;
            var meta = p.GetCustomAttribute<SettingAttribute>();
            var key = name.Name;
            var t = p.PropertyType;
            var kind = t == typeof(bool) ? SettingKind.Toggle
                : t == typeof(int) ? SettingKind.Integer
                : t == typeof(double) ? SettingKind.Number
                : t == typeof(string) ? meta?.Values is { Length: > 0 } ? SettingKind.Choice : SettingKind.Text
                : SettingKind.Json;
            list.Add(new SettingInfo(key, GroupOf(key), TitleOf(key), meta?.Description ?? "", kind, meta?.Values ?? [],
                meta?.Minimum ?? double.NaN, meta?.Maximum ?? double.NaN, meta?.Secret ?? false, p));
        }

        return [.. list.OrderBy(s => GroupOrder.ToList().IndexOf(s.Group))];
    }

    private static string GroupOf(string key) => Groups.GetValueOrDefault(key.Split('.')[0], "Other");

    /// <summary>
    /// VS Code's way of naming a setting: "editor.bracketPairColorization.enabled" is "Bracket Pair Colorization: Enabled",
    /// "terminal.integrated.fontSize" is "Font Size" (the words that only repeat the group are dropped).
    /// </summary>
    public static string TitleOf(string key)
    {
        var parts = key.Split('.').Skip(1).Where(p => p != "integrated").Select(Words).ToList();
        if (parts.Count == 0) return Words(key);
        return parts.Count == 1 ? parts[0] : string.Join(" › ", parts[..^1]) + ": " + parts[^1];
    }

    private static string Words(string camel)
    {
        var words = new List<string>();
        var start = 0;
        for (var i = 1; i <= camel.Length; i++)
        {
            // A word ends before an upper-case letter that follows a lower-case one ("fontSize"), or at the end.
            if (i == camel.Length || char.IsUpper(camel[i]) && char.IsLower(camel[i - 1]))
            {
                words.Add(camel[start..i]);
                start = i;
            }
        }

        return string.Join(' ', words.Select(w => w.Length > 0 ? char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..] : w));
    }

    /// <summary>
    /// <paramref name="settings"/> with the setting <paramref name="key"/> set to <paramref name="value"/> (a JSON value), or
    /// back to its default when <paramref name="value"/> is null. Null when the value does not fit the setting.
    /// </summary>
    public static WickedSettings? With(WickedSettings settings, string key, JsonNode? value)
    {
        var node = JsonSerializer.SerializeToNode(settings, WickedSettings.Json)!.AsObject();
        if (value is null) node.Remove(key);
        else node[key] = value.DeepClone();
        try
        {
            return node.Deserialize<WickedSettings>(WickedSettings.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>settings.json's JSON schema: every setting with its type, description, values, limits and default.</summary>
    public static string JsonSchema()
    {
        var properties = new JsonObject();
        foreach (var s in All)
        {
            var p = new JsonObject { ["description"] = s.Description, ["default"] = JsonNode.Parse(s.DefaultJson) };
            switch (s.Kind)
            {
                case SettingKind.Toggle: p["type"] = "boolean"; break;
                case SettingKind.Integer: p["type"] = "integer"; break;
                case SettingKind.Number: p["type"] = "number"; break;
                case SettingKind.Text: p["type"] = "string"; break;
                case SettingKind.Choice:
                    p["type"] = "string";
                    p["enum"] = new JsonArray([.. s.Values.Select(v => (JsonNode)JsonValue.Create(v)!)]);
                    break;
                default:
                    p["type"] = s.Property.PropertyType.IsArray ? "array" : "object";
                    break;
            }

            if (!double.IsNaN(s.Minimum)) p["minimum"] = s.Minimum;
            if (!double.IsNaN(s.Maximum)) p["maximum"] = s.Maximum;
            properties[s.Key] = p;
        }

        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "Wicked settings",
            ["type"] = "object",
            ["properties"] = properties,
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
