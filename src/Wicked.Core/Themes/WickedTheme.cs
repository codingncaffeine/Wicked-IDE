using System.Globalization;
using System.Text.Json;

namespace Wicked.Core.Themes;

/// <summary>Whether a theme is dark, light or high contrast (the controls' own look follows it).</summary>
public enum ThemeKind
{
    Dark,
    Light,
    HighContrast,
}

/// <summary>
/// A window theme (D16 layer 1, D17): VS Code's theme.json shape — a name, a type, <c>colors</c> by VS Code's keys and
/// <c>tokenColors</c> for code — plus Wicked's own keys (<c>wicked.Brush.Ground</c> …) that set a token directly, and
/// <c>wicked.codeTheme</c> naming a built-in code palette when the theme has no token colours of its own. Any VS Code
/// colour theme reads as one; what it leaves out is worked out from what it gives.
/// </summary>
public sealed class WickedTheme
{
    private WickedTheme(string name, ThemeKind kind, IReadOnlyDictionary<string, uint> colors, string? codeTheme, string? tokenColorsJson, string? path)
    {
        Name = name;
        Kind = kind;
        Colors = colors;
        CodeTheme = codeTheme;
        TokenColorsJson = tokenColorsJson;
        Path = path;
    }

    public string Name { get; }

    public ThemeKind Kind { get; }

    /// <summary>The colours the file gives, by key (0xAARRGGBB).</summary>
    public IReadOnlyDictionary<string, uint> Colors { get; }

    /// <summary>A built-in code palette to use (TextMateSharp's names: DarkPlus, LightPlus, …), when the theme names one.</summary>
    public string? CodeTheme { get; }

    /// <summary>The file's own tokenColors (an imported VS Code theme), kept as JSON for the syntax highlighter.</summary>
    public string? TokenColorsJson { get; }

    /// <summary>Where it was read from (a user theme), null for a built-in.</summary>
    public string? Path { get; }

    public bool IsDark => Kind != ThemeKind.Light;

    public static WickedTheme Parse(string json, string? path = null)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = doc.RootElement;
        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : System.IO.Path.GetFileNameWithoutExtension(path ?? "Theme");
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var kind = type switch
        {
            "light" or "vs" => ThemeKind.Light,
            "hc" or "hc-black" or "hcDark" or "hc-light" => ThemeKind.HighContrast,
            _ => ThemeKind.Dark,
        };
        var colors = new Dictionary<string, uint>(StringComparer.Ordinal);
        if (root.TryGetProperty("colors", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in c.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && ParseColor(p.Value.GetString()) is { } color) colors[p.Name] = color;
            }
        }

        var code = colors.Count >= 0 && root.TryGetProperty("wicked.codeTheme", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null;
        var tokens = root.TryGetProperty("tokenColors", out var tc) && tc.ValueKind == JsonValueKind.Array ? tc.GetRawText() : null;
        if (type is null && colors.TryGetValue("editor.background", out var bg)) kind = Luminance(bg) > 0.5 ? ThemeKind.Light : ThemeKind.Dark;
        return new WickedTheme(name, kind, colors, code, tokens, path);
    }

    /// <summary><c>#RGB</c>, <c>#RGBA</c>, <c>#RRGGBB</c> or <c>#RRGGBBAA</c> as 0xAARRGGBB; null for anything else.</summary>
    public static uint? ParseColor(string? text)
    {
        if (text is not { Length: >= 4 } || text[0] != '#') return null;
        var hex = text[1..];
        if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(ch => $"{ch}{ch}"));
        if (hex.Length == 6) hex += "FF";
        if (hex.Length != 8 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba)) return null;
        return (rgba << 24) | (rgba >> 8);
    }

    public static string Hex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture) + ((argb >> 24) == 0xFF ? "" : (argb >> 24).ToString("X2", CultureInfo.InvariantCulture));

    // ── Wicked's tokens from VS Code's keys ──────────────────────────────────────────────────────

    /// <summary>For each token, the VS Code keys that give it, most specific first.</summary>
    private static readonly (string Token, string[] Keys)[] Map =
    [
        ("Brush.Ground", ["titleBar.activeBackground", "activityBar.background", "editorGroupHeader.tabsBackground"]),
        ("Brush.Surface", ["sideBar.background", "panel.background", "editorGroupHeader.tabsBackground"]),
        ("Brush.Editor", ["editor.background"]),
        ("Brush.Raised", ["editorWidget.background", "dropdown.background", "input.background", "quickInput.background"]),
        ("Brush.Hover", ["list.hoverBackground", "toolbar.hoverBackground"]),
        ("Brush.Pressed", ["list.activeSelectionBackground", "list.inactiveSelectionBackground"]),
        ("Brush.Border.Subtle", ["sideBar.border", "panel.border", "editorGroup.border"]),
        ("Brush.Border", ["contrastBorder", "input.border", "dropdown.border", "editorWidget.border", "panel.border"]),
        ("Brush.Border.Focus", ["focusBorder", "contrastActiveBorder"]),
        ("Brush.Text", ["foreground", "editor.foreground", "sideBar.foreground"]),
        ("Brush.Text.Secondary", ["descriptionForeground", "sideBarSectionHeader.foreground"]),
        ("Brush.Text.Muted", ["disabledForeground", "editorLineNumber.foreground"]),
        ("Brush.Text.OnAccent", ["button.foreground"]),
        ("Brush.Accent", ["button.background", "focusBorder", "textLink.foreground"]),
        ("Brush.Accent.Hover", ["button.hoverBackground", "textLink.activeForeground"]),
        ("Brush.Accent.Subtle", ["list.focusBackground", "editor.selectionBackground"]),
        ("Brush.Success", ["terminal.ansiGreen", "gitDecoration.addedResourceForeground"]),
        ("Brush.Warning", ["editorWarning.foreground", "list.warningForeground"]),
        ("Brush.Danger", ["errorForeground", "editorError.foreground", "list.errorForeground"]),
        ("Brush.Match", ["editor.findMatchHighlightBackground"]),
        ("Brush.Inserted", ["diffEditor.insertedTextBackground"]),
        ("Brush.Caption.Close", ["errorForeground"]),
        ("Brush.Terminal.Background", ["terminal.background", "panel.background", "editor.background"]),
        ("Brush.Terminal.Foreground", ["terminal.foreground", "foreground"]),
        ("Brush.Terminal.Cursor", ["terminalCursor.foreground", "editorCursor.foreground"]),
        ("Brush.Terminal.Selection", ["terminal.selectionBackground", "editor.selectionBackground"]),
        ("Brush.Terminal.FindMatch", ["terminal.findMatchHighlightBackground", "editor.findMatchHighlightBackground"]),
        ("Brush.Terminal.FindMatch.Current", ["terminal.findMatchBackground", "editor.findMatchBackground"]),
    ];

    private static readonly string[] AnsiNames = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White", "BrightBlack", "BrightRed", "BrightGreen", "BrightYellow", "BrightBlue", "BrightMagenta", "BrightCyan", "BrightWhite"];

    /// <summary>
    /// The window's tokens for this theme over <paramref name="fallback"/> (the built-in theme of the same kind): a
    /// <c>wicked.</c> key first, then VS Code's keys, then what is worked out from the surface and the ink.
    /// </summary>
    public IReadOnlyDictionary<string, uint> Tokens(IReadOnlyDictionary<string, uint> fallback)
    {
        var result = new Dictionary<string, uint>(fallback, StringComparer.Ordinal);
        foreach (var (token, keys) in Map)
        {
            if (Colors.TryGetValue("wicked." + token, out var own))
            {
                result[token] = own;
                continue;
            }

            foreach (var key in keys)
            {
                if (!Colors.TryGetValue(key, out var color)) continue;
                result[token] = token is "Brush.Accent.Subtle" or "Brush.Match" or "Brush.Inserted" or "Brush.Terminal.Selection" ? color : Opaque(color, result.GetValueOrDefault("Brush.Surface", 0xFF000000));
                break;
            }
        }

        for (var i = 0; i < 16; i++)
        {
            if (Colors.TryGetValue($"terminal.ansi{AnsiNames[i]}", out var ansi)) result[$"Brush.Terminal.Ansi{i}"] = ansi;
        }

        // What a theme leaves out comes from what it gives: hover and pressed are the surface stepped toward the ink.
        if (!Has("Brush.Hover")) result["Brush.Hover"] = Mix(result["Brush.Surface"], result["Brush.Text"], 0.07);
        if (!Has("Brush.Pressed")) result["Brush.Pressed"] = Mix(result["Brush.Surface"], result["Brush.Text"], 0.12);
        if (!Has("Brush.Raised")) result["Brush.Raised"] = Mix(result["Brush.Surface"], result["Brush.Text"], 0.04);
        if (!Has("Brush.Text.Secondary")) result["Brush.Text.Secondary"] = Mix(result["Brush.Text"], result["Brush.Surface"], 0.3);
        if (!Has("Brush.Text.Muted")) result["Brush.Text.Muted"] = Mix(result["Brush.Text"], result["Brush.Surface"], 0.5);
        if (!Has("Brush.Ground")) result["Brush.Ground"] = Mix(result["Brush.Surface"], IsDark ? 0xFF000000 : 0xFFFFFFFF, 0.25);
        if (!Has("Brush.Accent.Hover") && Has("Brush.Accent")) result["Brush.Accent.Hover"] = Mix(result["Brush.Accent"], 0xFFFFFFFF, 0.15);
        if (!Has("Brush.Accent.Subtle") && Has("Brush.Accent")) result["Brush.Accent.Subtle"] = (result["Brush.Accent"] & 0x00FFFFFF) | 0x26000000;
        if (!Has("Brush.Text.OnAccent") && Has("Brush.Accent")) result["Brush.Text.OnAccent"] = Luminance(result["Brush.Accent"]) > 0.45 ? 0xFF0D0E11 : 0xFFFFFFFF;
        return result;

        bool Has(string token) => Colors.ContainsKey("wicked." + token) || (Map.FirstOrDefault(m => m.Token == token).Keys?.Any(Colors.ContainsKey) ?? false);
    }

    /// <summary>A translucent colour laid on <paramref name="under"/>: brushes for surfaces must be opaque.</summary>
    private static uint Opaque(uint color, uint under)
    {
        var a = (color >> 24) / 255.0;
        if (a >= 0.999) return color;
        return Mix(under, color | 0xFF000000, a);
    }

    public static uint Mix(uint a, uint b, double t)
    {
        static uint Ch(uint c, int shift) => (c >> shift) & 0xFF;
        uint Blend(int shift) => (uint)Math.Round(Ch(a, shift) + ((double)Ch(b, shift) - Ch(a, shift)) * t);
        return 0xFF000000 | (Blend(16) << 16) | (Blend(8) << 8) | Blend(0);
    }

    /// <summary>WCAG relative luminance (0 black … 1 white).</summary>
    public static double Luminance(uint argb)
    {
        static double Linear(uint c)
        {
            var s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Linear((argb >> 16) & 0xFF)) + (0.7152 * Linear((argb >> 8) & 0xFF)) + (0.0722 * Linear(argb & 0xFF));
    }

    /// <summary>The WCAG contrast ratio of two opaque colours (1 … 21).</summary>
    public static double Contrast(uint a, uint b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>The editor's own colours (VS Code's editor keys) a theme gives, for the code view.</summary>
    public IReadOnlyDictionary<string, uint> EditorColors => Colors;
}
