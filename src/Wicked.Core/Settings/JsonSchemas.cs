using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wicked.Core.Settings;

/// <summary>
/// The JSON schemas of Wicked's own files and how the JSON language server is told about them, as VS Code tells it:
/// <c>json.schemas</c> in its settings (file pattern to schema URL), and <c>handledSchemaProtocols: ["file"]</c> at start so
/// it reads the schema files itself. With them, settings.json and keybindings.json get completion, hovers and problems.
/// </summary>
public static class JsonSchemas
{
    /// <summary>keybindings.json's schema: a list of key, command (one Wicked knows, or "-" and one to take away) and when.</summary>
    public static string Keybindings(IEnumerable<string> commandIds)
    {
        var ids = commandIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var item = new JsonObject
        {
            ["type"] = "object",
            ["required"] = new JsonArray("key", "command"),
            ["properties"] = new JsonObject
            {
                ["key"] = new JsonObject { ["type"] = "string", ["description"] = "The keys, as VS Code writes them: \"ctrl+shift+k\", a chord \"ctrl+k ctrl+s\"." },
                ["command"] = new JsonObject
                {
                    ["description"] = "The command the keys run; \"-\" before it takes its default keys away.",
                    ["anyOf"] = new JsonArray(
                        new JsonObject { ["enum"] = new JsonArray([.. ids.Select(i => (JsonNode)JsonValue.Create(i)!)]) },
                        new JsonObject { ["type"] = "string", ["pattern"] = "^-" }),
                },
                ["when"] = new JsonObject { ["type"] = "string", ["description"] = "When the keys apply, as VS Code writes it." },
                ["args"] = new JsonObject { ["description"] = "What the command is given." },
            },
        };
        var schema = new JsonObject
        {
            ["$schema"] = "http://json-schema.org/draft-07/schema#",
            ["title"] = "Wicked key bindings",
            ["type"] = "array",
            ["items"] = item,
        };
        return schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>
    /// The JSON server's settings: the user's own for it (<c>lsp.servers.json.settings</c>), with Wicked's schemas added to
    /// <c>json.schemas</c> after any the user listed.
    /// </summary>
    public static JsonElement ServerSettings(JsonElement? user, IEnumerable<(string File, string Schema)> schemas)
    {
        var root = user is { ValueKind: JsonValueKind.Object } u ? JsonNode.Parse(u.GetRawText())!.AsObject() : new JsonObject();
        if (root["json"] is not JsonObject json)
        {
            json = new JsonObject();
            root["json"] = json;
        }

        if (json["schemas"] is not JsonArray list)
        {
            list = new JsonArray();
            json["schemas"] = list;
        }

        foreach (var (file, schema) in schemas)
        {
            list.Add(new JsonObject
            {
                ["fileMatch"] = new JsonArray(file.Replace('\\', '/'), Lsp.LspPaths.ToUri(file)),
                ["url"] = Lsp.LspPaths.ToUri(schema),
            });
        }

        return JsonDocument.Parse(root.ToJsonString()).RootElement.Clone();
    }

    /// <summary>What the JSON server is told at start: it formats, and reads file: schemas itself.</summary>
    public static JsonElement ServerInitializationOptions { get; } =
        JsonDocument.Parse("""{ "provideFormatter": true, "handledSchemaProtocols": ["file"] }""").RootElement.Clone();
}
