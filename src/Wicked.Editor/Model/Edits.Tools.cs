using System.Globalization;
using System.Text;

namespace Wicked.Editor.Model;

/// <summary>
/// The line and text tools VS Code users reach for: duplicate, insert a line above or below, select the line, change case,
/// sort, reverse and join lines, carets at the ends of the selected lines. Same rules as the rest of <see cref="Edits"/>:
/// every selection at once, one undo step.
/// </summary>
public static partial class Edits
{
    /// <summary>
    /// Duplicate Selection: a selection's text is copied right after it and the copy selected; an empty selection copies its
    /// line below, the caret going with the copy (as VS Code does).
    /// </summary>
    public static bool DuplicateSelection(Document doc)
    {
        if (doc.Selections.All(s => s.IsEmpty)) return CopyLines(doc, 1);
        var rope = doc.Text;
        var changes = new List<TextChange>();
        var after = new List<Selection>();
        var shift = 0;
        foreach (var s in doc.Selections)
        {
            if (s.IsEmpty)
            {
                var line = rope.LineOf(s.Active);
                var end = rope.LineStart(line) + rope.LineLength(line);
                var text = rope.Line(line);
                changes.Add(new TextChange(end, 0, "\n" + text));
                after.Add(Selection.Caret(s.Active + shift + text.Length + 1));
                shift += text.Length + 1;
            }
            else
            {
                var text = rope.Substring(s.Start, s.End - s.Start);
                changes.Add(new TextChange(s.End, 0, text));
                after.Add(new Selection(s.End + shift, s.End + shift + text.Length));
                shift += text.Length;
            }
        }

        return doc.Apply(changes, after, EditKind.Other, doc.PrimaryIndex);
    }

    /// <summary>
    /// Ctrl+Enter / Ctrl+Shift+Enter: a new line below (or above) each caret's line, indented like it, the caret on it; the
    /// line the caret was on is not split.
    /// </summary>
    public static bool InsertLine(Document doc, bool above)
    {
        var rope = doc.Text;
        var lines = doc.Selections.Select(s => rope.LineOf(s.Active)).Distinct().Order().ToList();
        var changes = new List<TextChange>();
        var after = new List<Selection>();
        var shift = 0;
        foreach (var line in lines)
        {
            var indent = TextNavigation.Indentation(rope.Line(line));
            if (above)
            {
                var start = rope.LineStart(line);
                changes.Add(new TextChange(start, 0, indent + "\n"));
                after.Add(Selection.Caret(start + shift + indent.Length));
            }
            else
            {
                var end = rope.LineStart(line) + rope.LineLength(line);
                changes.Add(new TextChange(end, 0, "\n" + indent));
                after.Add(Selection.Caret(end + shift + 1 + indent.Length));
            }

            shift += indent.Length + 1;
        }

        return doc.Apply(changes, after, EditKind.Other);
    }

    /// <summary>Expand Line Selection: each selection grows to whole lines; again, it takes the next line too.</summary>
    public static bool SelectLines(Document doc)
    {
        var rope = doc.Text;
        var after = doc.Selections.Select(s =>
        {
            // Through the line the selection ends on: whole lines already (ending at a line start) take that next line too.
            var startOfFirst = rope.LineStart(rope.LineOf(s.Start));
            var last = rope.LineOf(s.End);
            var end = last + 1 < rope.LineCount ? rope.LineStart(last + 1) : rope.Length;
            return new Selection(startOfFirst, end);
        }).ToList();
        doc.SetSelections(after, doc.PrimaryIndex);
        return true;
    }

    /// <summary>Transform to Upper / Lower / Title Case: the selected text, or the word at an empty caret.</summary>
    public static bool TransformCase(Document doc, Func<string, string> transform)
    {
        var rope = doc.Text;
        var changes = new List<TextChange>();
        var after = new List<Selection>();
        foreach (var s in doc.Selections)
        {
            var (start, end) = s.IsEmpty ? TextNavigation.WordAt(rope, s.Active) : (s.Start, s.End);
            if (end <= start)
            {
                after.Add(s);
                continue;
            }

            var text = rope.Substring(start, end - start);
            var changed = transform(text); // the invariant case maps keep the length, so selections stay put
            if (changed != text) changes.Add(new TextChange(start, end - start, changed));
            after.Add(s);
        }

        return changes.Count > 0 && doc.Apply(changes, after, EditKind.Other, doc.PrimaryIndex);
    }

    public static string TitleCase(string text)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var startsWord = i == 0 || !char.IsLetterOrDigit(chars[i - 1]);
            chars[i] = startsWord ? char.ToUpperInvariant(chars[i]) : char.ToLowerInvariant(chars[i]);
        }

        return new string(chars);
    }

    /// <summary>
    /// Sort Lines Ascending / Descending and Reverse Lines: the selected lines, or the whole document when nothing is
    /// selected. Sorting compares as people read (culture-aware, as VS Code's localeCompare); selections keep their lines' range.
    /// </summary>
    public static bool ReorderLines(Document doc, Func<List<string>, List<string>> reorder)
    {
        var rope = doc.Text;
        var blocks = doc.Selections.All(s => s.IsEmpty) ? [(0, rope.LineCount - 1)] : LineBlocks(doc);
        var changes = new List<TextChange>();
        foreach (var (first, last) in blocks)
        {
            if (last <= first) continue;
            var lines = Enumerable.Range(first, last - first + 1).Select(rope.Line).ToList();
            var reordered = reorder(lines);
            if (reordered.SequenceEqual(lines, StringComparer.Ordinal)) continue;
            var start = rope.LineStart(first);
            var end = rope.LineStart(last) + rope.LineLength(last);
            changes.Add(new TextChange(start, end - start, string.Join('\n', reordered)));
        }

        // Line lengths are kept in sum, so every selection's offsets still lie inside its block.
        return changes.Count > 0 && doc.Apply(changes, doc.Selections, EditKind.Other, doc.PrimaryIndex);
    }

    public static List<string> SortAscending(List<string> lines) => [.. lines.Order(StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None))];

    public static List<string> SortDescending(List<string> lines) => [.. lines.OrderDescending(StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.None))];

    public static List<string> Reverse(List<string> lines) => [.. Enumerable.Reverse(lines)];

    /// <summary>
    /// Join Lines: each selection's lines become one, joined by a single space with the next line's indentation dropped; a
    /// caret on one line joins it with the line below. Carets go to the last join.
    /// </summary>
    public static bool JoinLines(Document doc)
    {
        var rope = doc.Text;
        var changes = new List<TextChange>();
        var after = new List<Selection>();
        var shift = 0;
        foreach (var (first, blockLast) in LineBlocks(doc))
        {
            var last = blockLast == first ? Math.Min(first + 1, rope.LineCount - 1) : blockLast;
            if (last == first) continue;
            var joined = new StringBuilder(rope.Line(first).TrimEnd());
            var joinAt = 0;
            for (var line = first + 1; line <= last; line++)
            {
                var next = rope.Line(line).Trim();
                if (joined.Length > 0 && next.Length > 0) joined.Append(' ');
                joinAt = joined.Length;
                joined.Append(next);
            }

            var start = rope.LineStart(first);
            var end = rope.LineStart(last) + rope.LineLength(last);
            changes.Add(new TextChange(start, end - start, joined.ToString()));
            after.Add(Selection.Caret(start + shift + joinAt));
            shift += joined.Length - (end - start);
        }

        return changes.Count > 0 && doc.Apply(changes, after, EditKind.Other);
    }

    /// <summary>Shift+Alt+I: a caret at the end of every line each selection covers (the last at the selection's end).</summary>
    public static bool CaretsAtLineEnds(Document doc)
    {
        var rope = doc.Text;
        var carets = new List<Selection>();
        foreach (var s in doc.Selections)
        {
            var first = rope.LineOf(s.Start);
            var last = rope.LineOf(s.End);
            if (last > first && s.End == rope.LineStart(last)) last--;
            for (var line = first; line <= last; line++)
            {
                carets.Add(Selection.Caret(line == rope.LineOf(s.End) ? s.End : rope.LineStart(line) + rope.LineLength(line)));
            }
        }

        var distinct = carets.DistinctBy(c => c.Active).OrderBy(c => c.Active).ToList();
        doc.SetSelections(distinct, distinct.Count - 1);
        return true;
    }
}

/// <summary>Closing tags as an opening tag is finished: typing "&gt;" after &lt;div class="x" puts &lt;/div&gt; after the caret.</summary>
public static partial class Edits
{
    private static readonly System.Text.RegularExpressions.Regex OpeningTag =
        new(@"<([A-Za-z][\w:.-]*)(?:\s(?:[^<>""']|""[^""]*""|'[^']*')*)?>$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>HTML's void elements, which never have a closing tag.</summary>
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>
    /// After "&gt;" was typed: each caret just past a complete opening tag on its line (not self-closing, not a void element
    /// in HTML) gets that tag's closing tag after it, the caret staying between. False when nothing was closed.
    /// </summary>
    public static bool CloseTag(Document doc, TagClosing closing)
    {
        if (closing == TagClosing.None) return false;
        var rope = doc.Text;
        var changes = new List<TextChange>();
        var after = new List<Selection>();
        var shift = 0;
        foreach (var s in doc.Selections)
        {
            var caret = s.Active;
            var line = rope.LineOf(caret);
            var before = rope.Substring(rope.LineStart(line), caret - rope.LineStart(line));
            var match = s.IsEmpty ? OpeningTag.Match(before) : System.Text.RegularExpressions.Match.Empty;
            var name = match.Success ? match.Groups[1].Value : null;
            var closingTag = $"</{name}>";
            var rest = rope.Substring(caret, Math.Min(closingTag.Length, rope.Length - caret));
            if (name is null || before.EndsWith("/>", StringComparison.Ordinal) || (closing == TagClosing.Html && VoidElements.Contains(name)) || rest == closingTag) // not a path
            {
                after.Add(Selection.Caret(caret + shift));
                continue;
            }

            changes.Add(new TextChange(caret, 0, closingTag));
            after.Add(Selection.Caret(caret + shift));
            shift += closingTag.Length;
        }

        return changes.Count > 0 && doc.Apply(changes, after, EditKind.Other, doc.PrimaryIndex);
    }
}
