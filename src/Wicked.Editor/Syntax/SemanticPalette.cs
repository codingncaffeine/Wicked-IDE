using System.Collections.Concurrent;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;

namespace Wicked.Editor.Syntax;

/// <summary>
/// Colours for a language server's semantic tokens, from the code colour theme: each token type (and its modifiers) is
/// read as the TextMate scopes VS Code maps it to, and the theme's colour for the first scope it has a rule for wins. A
/// token whose scopes the theme says nothing about keeps the grammar's colour, as in VS Code.
/// </summary>
public sealed class SemanticPalette
{
    // VS Code's default mapping from semantic token types (and type.modifier pairs) to TextMate scopes.
    private static readonly Dictionary<string, string[]> Scopes = new(StringComparer.Ordinal)
    {
        ["namespace"] = ["entity.name.namespace"],
        ["type"] = ["entity.name.type"],
        ["type.defaultLibrary"] = ["support.type"],
        ["struct"] = ["storage.type.struct", "entity.name.type.struct"],
        ["class"] = ["entity.name.type.class"],
        ["class.defaultLibrary"] = ["support.class"],
        ["interface"] = ["entity.name.type.interface"],
        ["enum"] = ["entity.name.type.enum"],
        ["function"] = ["entity.name.function"],
        ["function.defaultLibrary"] = ["support.function"],
        ["method"] = ["entity.name.function.member", "entity.name.function"],
        ["macro"] = ["entity.name.function.preprocessor"],
        ["variable"] = ["variable.other.readwrite", "entity.name.variable"],
        ["variable.readonly"] = ["variable.other.constant"],
        ["variable.readonly.defaultLibrary"] = ["support.constant"],
        ["parameter"] = ["variable.parameter"],
        ["property"] = ["variable.other.property"],
        ["property.readonly"] = ["variable.other.constant.property"],
        ["enumMember"] = ["variable.other.enummember"],
        ["event"] = ["variable.other.event"],
        ["decorator"] = ["entity.name.decorator", "entity.name.function"],
        ["label"] = ["entity.name.label"],
        ["typeParameter"] = ["entity.name.type.parameter"],
        ["keyword"] = ["keyword.control"],
        ["modifier"] = ["storage.modifier"],
        ["comment"] = ["comment"],
        ["string"] = ["string"],
        ["number"] = ["constant.numeric"],
        ["regexp"] = ["constant.regexp"],
        ["operator"] = ["keyword.operator"],
    };

    private readonly TextMateSharp.Themes.Theme _theme;
    private readonly ConcurrentDictionary<string, uint> _cache = new(StringComparer.Ordinal);

    private SemanticPalette(ThemeName name, TextMateSharp.Themes.Theme theme)
    {
        Name = name;
        _theme = theme;
    }

    public ThemeName Name { get; }

    /// <summary>The palette for a code theme. Reads the theme from the bundled set: call off the UI thread.</summary>
    public static SemanticPalette For(ThemeName name, string? custom = null)
    {
        var registry = new Registry(Languages.Options);
        registry.SetTheme(SyntaxService.Load(name, custom));
        return new SemanticPalette(name, registry.GetTheme());
    }

    /// <summary>The colour (0xAARRGGBB) for a token of <paramref name="type"/> with <paramref name="modifiers"/>, or 0 for none.</summary>
    public uint ColorFor(string type, IReadOnlyList<string> modifiers)
    {
        var key = modifiers.Count == 0 ? type : type + "." + string.Join('.', modifiers);
        return _cache.GetOrAdd(key, _ => Resolve(type, modifiers));
    }

    private uint Resolve(string type, IReadOnlyList<string> modifiers)
    {
        // The most specific mapping first: type with each modifier, then the type alone.
        var candidates = new List<string[]>();
        foreach (var m in modifiers)
        {
            if (modifiers.Contains("readonly") && modifiers.Contains("defaultLibrary") && Scopes.TryGetValue($"{type}.readonly.defaultLibrary", out var both)) candidates.Add(both);
            if (Scopes.TryGetValue($"{type}.{m}", out var withModifier)) candidates.Add(withModifier);
        }

        if (Scopes.TryGetValue(type, out var plain)) candidates.Add(plain);
        foreach (var scopes in candidates)
        {
            foreach (var scope in scopes)
            {
                var rules = _theme.Match([scope]);
                var foreground = rules.Select(r => r.foreground).FirstOrDefault(f => f > 0);
                if (foreground > 0 && ColorTable.Parse(_theme.GetColor(foreground), 0) is var color && color != 0) return color;
            }
        }

        return 0;
    }
}
