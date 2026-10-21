using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wicked.Core.Settings;

/// <summary>
/// The changes the Keyboard Shortcuts editor makes to keybindings.json, as VS Code makes them: a new key for a command
/// adds a line for it and a "-command" line taking its default away; Remove takes a default away or deletes the user's
/// line; Reset deletes every line of the user's for the command. Keys go in and out in Avalonia's names ("Ctrl+K Ctrl+S")
/// and are written in VS Code's ("ctrl+k ctrl+s"). The file's comments are not kept: a header comment is written back.
/// </summary>
public static class KeybindingsEdit
{
    public const string Header =
        "// Your key bindings, in VS Code's format. A binding adds keys to a command; \"-command\" removes a default.\n" +
        "// The Keyboard Shortcuts editor (Ctrl+K Ctrl+S) writes this file; you can edit it by hand too.\n";

    /// <summary>
    /// <paramref name="command"/> takes <paramref name="keys"/>: the user's line for the keys it had replaced (or a new line),
    /// and when the keys it had were a default, a line taking that default away.
    /// </summary>
    /// <param name="json">keybindings.json as it is.</param>
    /// <param name="command">The command id.</param>
    /// <param name="keys">The new keys, in Avalonia's names.</param>
    /// <param name="old">The keys the row showed; null for a command that had none.</param>
    /// <param name="oldIsDefault">Those keys were a default, not a line of the user's.</param>
    /// <param name="when">The line's "when", kept when a line of the user's is rewritten.</param>
    public static string Change(string json, string command, string keys, string? old, bool oldIsDefault, string? when = null)
    {
        var list = Read(json);
        var vs = Keybindings.ToVsCode(keys);
        var oldVs = old is null ? null : Keybindings.ToVsCode(old);
        if (old is not null && !oldIsDefault && list.FirstOrDefault(e => Is(e, command, oldVs)) is { } mine)
        {
            mine["key"] = vs;
        }
        else
        {
            var line = new JsonObject { ["key"] = vs, ["command"] = command };
            if (when is not null) line["when"] = when;
            list.Add(line);
            if (old is not null && oldIsDefault && !list.Any(e => Is(e, "-" + command, oldVs))) list.Add(new JsonObject { ["key"] = oldVs, ["command"] = "-" + command });
        }

        return Write(list);
    }

    /// <summary>The command loses <paramref name="keys"/>: the user's line goes, or a default is taken away.</summary>
    public static string Remove(string json, string command, string keys, bool isDefault)
    {
        var list = Read(json);
        var vs = Keybindings.ToVsCode(keys);
        if (isDefault)
        {
            if (!list.Any(e => Is(e, "-" + command, vs))) list.Add(new JsonObject { ["key"] = vs, ["command"] = "-" + command });
        }
        else
        {
            list.RemoveAll(e => Is(e, command, vs));
        }

        return Write(list);
    }

    /// <summary>The command back to its defaults: every line of the user's for it goes, additions and removals.</summary>
    public static string Reset(string json, string command)
    {
        var list = Read(json);
        list.RemoveAll(e => e["command"]?.GetValue<string>() is { } c && (c == command || c == "-" + command));
        return Write(list);
    }

    private static bool Is(JsonObject entry, string command, string? vsKeys) =>
        entry["command"]?.GetValue<string>() == command
        && (vsKeys is null || string.Equals(entry["key"]?.GetValue<string>(), vsKeys, StringComparison.OrdinalIgnoreCase));

    private static List<JsonObject> Read(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return node is JsonArray array ? [.. array.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone())] : [];
    }

    private static string Write(List<JsonObject> list)
    {
        var array = new JsonArray([.. list]);
        // Relaxed escaping: a key such as ctrl+k is written as it reads, not with its plus escaped, for whoever edits the file by hand.
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return Header + array.ToJsonString(options).Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }
}
