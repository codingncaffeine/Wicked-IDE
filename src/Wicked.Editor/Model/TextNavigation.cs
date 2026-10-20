using Wicked.Editor.Text;

namespace Wicked.Editor.Model;

/// <summary>The three kinds of character a word move steps over, as VS Code tells them apart.</summary>
public enum CharClass
{
    Whitespace,
    Separator,
    Word,
}

/// <summary>Columns, words and indentation: the rules caret movement and editing share.</summary>
public static class TextNavigation
{
    /// <summary>VS Code's default word separators.</summary>
    public const string Separators = "`~!@#$%^&*()-=+[{]}\\|;:'\",.<>/?";

    public static CharClass Classify(char c) =>
        char.IsWhiteSpace(c) ? CharClass.Whitespace : Separators.Contains(c) ? CharClass.Separator : CharClass.Word;

    /// <summary>
    /// The visual column of <paramref name="column"/> in <paramref name="line"/>: tabs advance to the next tab stop and
    /// East Asian wide characters take two cells, as a monospaced editor draws them.
    /// </summary>
    public static int VisualColumn(ReadOnlySpan<char> line, int column, int tabSize)
    {
        var visual = 0;
        for (var i = 0; i < column && i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\t') visual += tabSize - (visual % tabSize);
            else if (char.IsLowSurrogate(c)) continue;
            else visual += IsWide(c) ? 2 : 1;
        }

        return visual;
    }

    /// <summary>The column whose visual column is nearest to <paramref name="visual"/> (the left one on a tie).</summary>
    public static int ColumnOfVisual(ReadOnlySpan<char> line, int visual, int tabSize)
    {
        var at = 0;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (char.IsLowSurrogate(c)) continue;
            var width = c == '\t' ? tabSize - (at % tabSize) : IsWide(c) ? 2 : 1;
            if (at + width > visual) return visual - at > width / 2 ? NextColumn(line, i) : i;
            at += width;
        }

        return line.Length;
    }

    private static int NextColumn(ReadOnlySpan<char> line, int i) => i + (char.IsHighSurrogate(line[i]) && i + 1 < line.Length ? 2 : 1);

    /// <summary>Hangul, CJK and full-width forms: the characters a terminal and an editor give two cells.</summary>
    public static bool IsWide(char c) =>
        c is >= 'ᄀ' and <= 'ᅟ' or >= '⺀' and <= '꓏' or >= '가' and <= '힣'
            or >= '豈' and <= '﫿' or >= '︰' and <= '﹏' or >= '＀' and <= '｠' or >= '￠' and <= '￦';

    /// <summary>The offset one character to the left, stepping over a surrogate pair whole.</summary>
    public static int PreviousChar(Rope text, int offset)
    {
        if (offset <= 0) return 0;
        return offset >= 2 && char.IsLowSurrogate(text[offset - 1]) && char.IsHighSurrogate(text[offset - 2]) ? offset - 2 : offset - 1;
    }

    /// <summary>The offset one character to the right, stepping over a surrogate pair whole.</summary>
    public static int NextChar(Rope text, int offset)
    {
        if (offset >= text.Length) return text.Length;
        return offset + 1 < text.Length && char.IsHighSurrogate(text[offset]) && char.IsLowSurrogate(text[offset + 1]) ? offset + 2 : offset + 1;
    }

    /// <summary>Ctrl+Left: to the start of the word (or run of separators) before the caret, across whitespace and line breaks.</summary>
    public static int WordStartLeft(Rope text, int offset)
    {
        if (offset == 0) return 0;
        var i = offset;
        // A line break is a stop of its own: from the start of a line, Ctrl+Left goes to the end of the line above.
        if (text[i - 1] == '\n') return i - 1;
        while (i > 0 && text[i - 1] != '\n' && Classify(text[i - 1]) == CharClass.Whitespace) i--;
        if (i == 0 || text[i - 1] == '\n') return i;
        var kind = Classify(text[i - 1]);
        while (i > 0 && text[i - 1] != '\n' && Classify(text[i - 1]) == kind) i--;
        return i;
    }

    /// <summary>Ctrl+Right: to the end of the word (or run of separators) after the caret, across whitespace and line breaks.</summary>
    public static int WordEndRight(Rope text, int offset)
    {
        var length = text.Length;
        if (offset >= length) return length;
        var i = offset;
        if (text[i] == '\n') return i + 1;
        while (i < length && text[i] != '\n' && Classify(text[i]) == CharClass.Whitespace) i++;
        if (i >= length || text[i] == '\n') return i;
        var kind = Classify(text[i]);
        while (i < length && text[i] != '\n' && Classify(text[i]) == kind) i++;
        return i;
    }

    /// <summary>The word under or just before <paramref name="offset"/>, or an empty range when there is none.</summary>
    public static (int Start, int End) WordAt(Rope text, int offset)
    {
        var start = offset;
        var end = offset;
        while (start > 0 && Classify(text[start - 1]) == CharClass.Word) start--;
        while (end < text.Length && Classify(text[end]) == CharClass.Word) end++;
        return (start, end);
    }

    /// <summary>The leading whitespace of a line.</summary>
    public static string Indentation(string line)
    {
        var n = 0;
        while (n < line.Length && line[n] is ' ' or '\t') n++;
        return line[..n];
    }
}
