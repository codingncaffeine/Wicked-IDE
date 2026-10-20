using System.Text.Json;

namespace Wicked.Core.Settings;

/// <summary>One line of keybindings.json: a key (a chord is two, space-separated) for a command, or "-command" to remove a default.</summary>
/// <param name="Keys">In Avalonia's gesture syntax ("Ctrl+Shift+D", "Ctrl+K Ctrl+O"); null when a removal names no key.</param>
/// <param name="Command">The command id.</param>
/// <param name="Remove">The line takes a default binding away.</param>
public sealed record Keybinding(string? Keys, string Command, bool Remove);

/// <summary>
/// keybindings.json in VS Code's own format (an array of { "key", "command", "when" }, comments allowed), so a file
/// from VS Code mostly reads as-is. Keys are translated from VS Code's names ("ctrl+/", "pagedown") to Avalonia's.
/// "when" clauses are kept out for now: editor commands bind in the editor, the rest everywhere.
/// </summary>
public static class Keybindings
{
    public static (IReadOnlyList<Keybinding> Bindings, string? Problem) Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ([], null);
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var list = new List<Keybinding>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (!item.TryGetProperty("command", out var c) || c.GetString() is not { Length: > 0 } command) continue;
                var key = item.TryGetProperty("key", out var k) ? k.GetString() : null;
                var remove = command.StartsWith('-');
                var gesture = key is null ? null : Translate(key);
                if (key is not null && gesture is null) continue; // a key this build does not know
                list.Add(new Keybinding(gesture, remove ? command[1..] : command, remove));
            }

            return (list, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ([], $"keybindings.json could not be read: {ex.Message}");
        }
    }

    public static async Task<(IReadOnlyList<Keybinding> Bindings, string? Problem)> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return ([], null);
        return Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }

    /// <summary>VS Code's "ctrl+shift+k ctrl+o" as Avalonia's "Ctrl+Shift+K Ctrl+O"; null when a key is unknown.</summary>
    public static string? Translate(string vscode)
    {
        var chords = vscode.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var translated = new List<string>();
        foreach (var chord in chords)
        {
            // "ctrl++" names the plus key: split on '+' but keep a trailing one.
            var parts = chord.EndsWith("++", StringComparison.Ordinal) ? [.. chord[..^2].Split('+'), "+"] : chord.Split('+');
            var mods = new List<string>();
            string? key = null;
            foreach (var raw in parts)
            {
                switch (raw.ToLowerInvariant())
                {
                    case "ctrl": mods.Add("Ctrl"); break;
                    case "shift": mods.Add("Shift"); break;
                    case "alt": mods.Add("Alt"); break;
                    case "meta" or "cmd" or "win" or "super": mods.Add("Meta"); break;
                    default: key = Key(raw); break;
                }
            }

            if (key is null) return null;
            translated.Add(string.Join('+', [.. mods, key]));
        }

        return translated.Count == 0 ? null : string.Join(' ', translated);
    }

    private static string? Key(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Length == 1 && char.IsAsciiLetter(n[0])) return n.ToUpperInvariant();
        if (n.Length == 1 && char.IsAsciiDigit(n[0])) return "D" + n;
        if (n.Length is 2 or 3 && n[0] == 'f' && int.TryParse(n[1..], out var f) && f is >= 1 and <= 24) return "F" + f;
        return n switch
        {
            "/" => "Oem2",
            "[" => "OemOpenBrackets",
            "]" => "OemCloseBrackets",
            "," => "OemComma",
            "." => "OemPeriod",
            "=" or "+" => "OemPlus",
            "-" => "OemMinus",
            "`" => "OemTilde",
            "\\" => "OemPipe",
            ";" => "OemSemicolon",
            "'" => "OemQuotes",
            "enter" => "Enter",
            "escape" => "Escape",
            "tab" => "Tab",
            "space" => "Space",
            "backspace" => "Back",
            "delete" => "Delete",
            "insert" => "Insert",
            "home" => "Home",
            "end" => "End",
            "pageup" => "PageUp",
            "pagedown" => "PageDown",
            "up" => "Up",
            "down" => "Down",
            "left" => "Left",
            "right" => "Right",
            _ => null,
        };
    }
}
