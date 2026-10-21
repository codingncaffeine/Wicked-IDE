using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Editor.Features;

/// <summary>
/// Expand Selection (Shift+Alt+Right) without a language server, as VS Code's word-and-bracket fallback does: the word, a
/// string's contents, the string with its quotes, a bracket pair's contents, the pair, the line's text, the whole line, the
/// next pair out, and so on to the whole file. Shrinking retraces the steps (the editor keeps them).
/// </summary>
public static class SmartSelect
{
    /// <summary>The smallest range that strictly contains [start, end), or null when the selection is the whole text.</summary>
    public static (int Start, int End)? Expand(Rope text, BracketIndex brackets, int start, int end)
    {
        (int Start, int End)? best = null;
        foreach (var range in Candidates(text, brackets, start, end))
        {
            if (range.Start > start || range.End < end || (range.Start == start && range.End == end)) continue;
            if (best is not { } b || range.End - range.Start < b.End - b.Start) best = range;
        }

        return best;
    }

    /// <summary>Ranges around the selection, in no particular order; <see cref="Expand"/> picks the smallest that holds it.</summary>
    private static IEnumerable<(int Start, int End)> Candidates(Rope text, BracketIndex brackets, int start, int end)
    {
        var (wordStart, wordEnd) = TextNavigation.WordAt(text, start);
        if (wordEnd > wordStart) yield return (wordStart, wordEnd);

        // A string on one line: the nearest quote before the selection and the nearest after it, inside then with the quotes.
        var line = text.LineOf(start);
        var lineStart = text.LineStart(line);
        var lineText = text.Line(line);
        if (text.LineOf(end) == line && start > lineStart)
        {
            foreach (var quote in "\"'`")
            {
                var open = lineText.LastIndexOf(quote, start - lineStart - 1);
                var close = open < 0 ? -1 : lineText.IndexOf(quote, end - lineStart);
                if (close < 0) continue;
                yield return (lineStart + open + 1, lineStart + close);
                yield return (lineStart + open, lineStart + close + 1);
            }
        }

        // Every bracket pair around the caret, outwards: its contents, then the pair.
        var at = start;
        for (var steps = 0; steps < 10_000 && brackets.Enclosing(at) is { } pair; steps++)
        {
            yield return (pair.Open + 1, pair.Close);
            yield return (pair.Open, pair.Close + 1);
            if (pair.Open == 0) break;
            at = pair.Open;
        }

        // The line's text without its indentation, the whole line(s), the whole text.
        var indent = TextNavigation.Indentation(lineText).Length;
        var lastLine = text.LineOf(end);
        if (end > start && lastLine > line && end == text.LineStart(lastLine)) lastLine--; // whole lines end at the next line's start
        yield return (lineStart + indent, text.LineStart(lastLine) + text.LineLength(lastLine));
        yield return (lineStart, lastLine + 1 < text.LineCount ? text.LineStart(lastLine + 1) : text.Length);
        yield return (0, text.Length);
    }
}
