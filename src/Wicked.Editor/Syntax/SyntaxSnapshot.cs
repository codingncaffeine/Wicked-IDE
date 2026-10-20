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

    public int Foreground(int index) => EncodedTokenAttributes.GetForeground(_packed[index * 2 + 1]);

    /// <summary>TextMate's font style bits (1 italic, 2 bold, 4 underline, 8 strikethrough), which <see cref="TokenStyle"/> mirrors.</summary>
    public TokenStyle Style(int index)
    {
        var style = (int)EncodedTokenAttributes.GetFontStyle(_packed[index * 2 + 1]);
        return style < 0 ? TokenStyle.None : (TokenStyle)style;
    }
}

/// <summary>The theme's colours, by id, as 0xAARRGGBB; id 0 is unused and id 1 the default foreground.</summary>
public sealed class ColorTable(ImmutableArray<uint> colors)
{
    public ImmutableArray<uint> Colors { get; } = colors;

    public uint this[int id] => (uint)id < (uint)Colors.Length ? Colors[id] : Default;

    public uint Default => Colors.Length > 1 ? Colors[1] : 0xFFD4D4D4;

    public static ColorTable From(IEnumerable<string> hex) =>
        new([.. hex.Select(h => h is { Length: >= 7 } && h[0] == '#' && uint.TryParse(h.AsSpan(1, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? 0xFF00_0000u | rgb
            : 0xFFD4D4D4u)]);
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
