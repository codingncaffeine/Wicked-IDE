using System.Collections.Immutable;
using System.Globalization;
using TextMateSharp.Internal.Grammars;

namespace Wicked.Editor.Syntax;

/// <summary>How a run of text is drawn: an index into the snapshot's colours, and its weight and slant.</summary>
[Flags]
public enum TokenStyle : byte
{
    None = 0,
    Italic = 1,
    Bold = 2,
    Underline = 4,
    Strikethrough = 8,
}

/// <summary>
/// One line's highlighting, packed as TextMate's binary tokens are: pairs of (start column, attributes), where the
/// attributes hold the foreground colour id and the font style. Immutable once published.
/// </summary>
public sealed class LineTokens(int[] packed)
{
    private readonly int[] _packed = packed;

    public int Count => _packed.Length / 2;

    public int Start(int index) => _packed[index * 2];

    /// <summary>TextMate's standard token type: 0 other, 1 comment, 2 string, 3 regex.</summary>
    public int TokenType(int index) => (int)EncodedTokenAttributes.GetTokenType(_packed[index * 2 + 1]);

    public int Foreground(int index) => EncodedTokenAttributes.GetForeground(_packed[index * 2 + 1]);

    /// <summary>TextMate's font style bits (1 italic, 2 bold, 4 underline, 8 strikethrough), which <see cref="TokenStyle"/> mirrors.</summary>
    public TokenStyle Style(int index)
    {
        var style = (int)EncodedTokenAttributes.GetFontStyle(_packed[index * 2 + 1]);
        return style < 0 ? TokenStyle.None : (TokenStyle)style;
    }
}

/// <summary>
/// The theme's colours, by TextMate colour id, as 0xAARRGGBB. Id 1 is the colour of text no rule names: the theme's
/// editor foreground (TextMate's own table holds black there).
/// </summary>
public sealed class ColorTable(ImmutableArray<uint> colors)
{
    public ImmutableArray<uint> Colors { get; } = colors;

    public uint this[int id] => (uint)id < (uint)Colors.Length && id > 0 ? Colors[id] : Default;

    public uint Default => Colors.Length > 1 ? Colors[1] : 0xFFD4D4D4;

    /// <summary>The table for a TextMate theme: ids from 1, looked up through the theme itself.</summary>
    public static ColorTable From(TextMateSharp.Themes.Theme theme)
    {
        var count = theme.GetColorMap().Count;
        var colors = new uint[count + 1];
        for (var id = 1; id <= count; id++) colors[id] = Parse(theme.GetColor(id), 0xFFD4D4D4);
        if (theme.GetGuiColorDictionary().TryGetValue("editor.foreground", out var foreground)) colors[1] = Parse(foreground, 0xFFD4D4D4);
        return new ColorTable([.. colors]);
    }

    public static uint Parse(string? hex, uint fallback)
    {
        if (hex is not { Length: >= 7 } || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return fallback;
        // #RRGGBBAA carries an alpha.
        var alpha = hex.Length >= 9 && uint.TryParse(hex.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : 0xFF;
        return (alpha << 24) | rgb;
    }
}

/// <summary>
/// The highlighting of one document as far as the syntax thread has got: per-line tokens for <see cref="Version"/> of
/// the text, correct for lines below <see cref="ValidLines"/> (later lines may still carry tokens from before an edit,
/// which is what an editor shows while it catches up). Immutable and shared: publishing one costs nothing to copy.
/// </summary>
public sealed record SyntaxSnapshot(long Version, ImmutableList<LineTokens?> Lines, int ValidLines, ColorTable Colors)
{
    public LineTokens? this[int line] => (uint)line < (uint)Lines.Count ? Lines[line] : null;
}
