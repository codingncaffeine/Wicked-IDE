using Wicked.Editor.Text;

namespace Wicked.Editor.Model;

/// <summary>
/// How a file is indented, read from its lines as VS Code's editor.detectIndentation does: tabs or spaces by which more
/// lines start with, and the space width by the step most often seen between one indented line and the next. Null when
/// the file says nothing (no indented lines), so the settings stand; a TabSize of 0 likewise keeps the setting's width
/// (tabs, whose width is the reader's choice, or spaces with no clear step).
/// </summary>
public static class IndentationGuess
{
    private const int MaxLines = 10_000;

    public static (bool InsertSpaces, int TabSize)? Guess(Rope text)
    {
        var tabs = 0;
        var spaces = 0;
        var steps = new int[9];
        var previous = 0;
        var lines = Math.Min(text.LineCount, MaxLines);
        for (var line = 0; line < lines; line++)
        {
            var start = text.LineStart(line);
            var end = line + 1 < text.LineCount ? text.LineStart(line + 1) : text.Length;
            var (lead, width, blank) = Leading(text, start, end);
            if (blank) continue;
            if (lead == '\t') tabs++;
            else if (lead == ' ' && width > 1) spaces++;

            // The step from the line before, counted only between space-indented lines (a tab line says nothing of width).
            if (lead != '\t')
            {
                var step = Math.Abs(width - previous);
                if (step is >= 2 and <= 8) steps[step]++;
                previous = width;
            }
        }

        if (tabs == 0 && spaces == 0) return null;
        if (tabs > spaces) return (false, 0);

        // The commonest step; 4, then 2, then 8 win a tie (the usual widths before the rare ones).
        int[] preference = [4, 2, 8, 3, 6, 5, 7];
        var best = preference.MaxBy(w => (steps[w], -Array.IndexOf(preference, w)));
        return (true, steps[best] == 0 ? 0 : best);
    }

    /// <summary>The first indenting character of a line (or none), its indentation width in spaces, and whether it is blank.</summary>
    private static (char Lead, int Width, bool Blank) Leading(Rope text, int start, int end)
    {
        var lead = '\0';
        var width = 0;
        for (var i = start; i < end; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n') return (lead, width, true);
            if (c != ' ' && c != '\t') return (lead, width, false);
            if (lead == '\0') lead = c;
            width += c == '\t' ? 4 : 1;
        }

        return (lead, width, true);
    }
}
