using System.Text;

namespace Wicked.Editor.Model;

/// <summary>
/// The editing commands. Every one works on all selections at once, as one undo step, and leaves the selections where
/// VS Code would. They run on the UI thread and cost what the rope edits cost: microseconds per selection.
/// </summary>
public static class Edits
{
    /// <summary>One selection's share of an edit: its changes (in the old text) and its selection afterwards, counted as if
    /// only its own changes had been made.</summary>
    private readonly record struct Local(TextChange[] Changes, Selection After);

    private static Local Keep(Selection s) => new([], s);

    /// <summary>Runs <paramref name="edit"/> on every selection and applies the result as one change set.</summary>
    private static bool Run(Document doc, EditKind kind, Func<Selection, Local> edit)
    {
        var changes = new List<TextChange>();
        var after = new List<Selection>(doc.Selections.Length);
        var shift = 0;
        var lastEnd = 0;
        foreach (var s in doc.Selections)
        {
            var local = edit(s);
            // Two selections whose edits would touch the same text (Backspace at the start of one and the end of another
            // on the same character): the later one gives way.
            if (local.Changes.Any(c => c.Offset < lastEnd))
            {
                after.Add(s with { Anchor = s.Anchor + shift, Active = s.Active + shift });
                continue;
            }

            changes.AddRange(local.Changes);
            after.Add(local.After with { Anchor = local.After.Anchor + shift, Active = local.After.Active + shift });
            foreach (var c in local.Changes)
            {
                shift += c.Delta;
                lastEnd = Math.Max(lastEnd, c.End);
            }
        }

        return doc.Apply(changes, after, kind, doc.PrimaryIndex);
    }

    /// <summary>Moves every selection without editing; ends the current undo group, as a caret move does.</summary>
    private static void Move(Document doc, Func<Selection, Selection> move)
    {
        doc.BreakUndoGroup();
        doc.SetSelections(doc.Selections.Select(move).ToList(), doc.PrimaryIndex);
    }

    // ── Typing ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Types <paramref name="text"/> at every selection, closing, skipping and wrapping pairs as VS Code does.</summary>
    public static bool Type(Document doc, string text, EditOptions options)
    {
        if (text.Length == 0) return false;
        var rope = doc.Text;
        return Run(doc, EditKind.Typing, s =>
        {
            if (text.Length == 1 && options.AutoClosePairs)
            {
                var c = text;
                var next = s.End < rope.Length ? rope[s.End] : '\0';
                var prev = s.Start > 0 ? rope[s.Start - 1] : '\0';

                // Typing a closer that is already there steps over it.
                if (s.IsEmpty && options.Pairs.Any(p => p.Close == c) && next == c[0] && !(options.Pairs.Any(p => p.Open == c && p.Close == c) && prev == '\\'))
                {
                    return new Local([], Selection.Caret(s.End + 1));
                }

                var pair = options.Pairs.FirstOrDefault(p => p.Open == c);
                if (pair.Open is not null)
                {
                    // A selection is wrapped in the pair and stays selected inside it.
                    if (!s.IsEmpty && options.AutoSurround)
                    {
                        var inner = s.Reversed ? new Selection(s.End + 1, s.Start + 1) : new Selection(s.Start + 1, s.End + 1);
                        return new Local([new TextChange(s.Start, 0, pair.Open), new TextChange(s.End, 0, pair.Close)], inner);
                    }

                    // An opener closes itself before whitespace, a closer or the end of the line; a quote not after a word.
                    var quote = pair.Open == pair.Close;
                    var closesHere = s.IsEmpty && (next is '\0' or '\n' || char.IsWhiteSpace(next) || options.Pairs.Any(p => p.Close[0] == next && p.Open != p.Close) || (quote && next is ',' or ';' or ':'))
                        && !(quote && (TextNavigation.Classify(prev) == CharClass.Word || prev == pair.Open[0]));
                    if (closesHere) return new Local([new TextChange(s.Start, 0, pair.Open + pair.Close)], Selection.Caret(s.Start + 1));
                }
            }

            return new Local([new TextChange(s.Start, s.Length, text)], Selection.Caret(s.Start + text.Length));
        });
    }

    /// <summary>Enter: a new line indented like this one, one level deeper after an opening bracket, and a bracket pair split around a blank line.</summary>
    public static bool NewLine(Document doc, EditOptions options)
    {
        var rope = doc.Text;
        return Run(doc, EditKind.Other, s =>
        {
            var line = rope.LineOf(s.Start);
            var lineText = rope.Line(line);
            var column = s.Start - rope.LineStart(line);
            var indent = TextNavigation.Indentation(lineText);
            if (indent.Length > column) indent = indent[..column];
            var before = lineText[..column].TrimEnd();
            var afterEnd = s.End - rope.LineStart(rope.LineOf(s.End));
            var afterText = rope.Line(rope.LineOf(s.End))[afterEnd..];
            var opener = before.Length > 0 ? before[^1] : '\0';
            var closer = afterText.TrimStart().Length > 0 ? afterText.TrimStart()[0] : '\0';
            var pairs = options.Pairs.Where(p => p.Open != p.Close).ToList();

            // The whitespace after the caret goes: the new line starts at its indentation.
            var trailing = afterText.Length - afterText.TrimStart(' ', '\t').Length;
            if (pairs.Any(p => p.Open[0] == opener && p.Close[0] == closer))
            {
                var inner = "\n" + indent + options.IndentUnit;
                return new Local([new TextChange(s.Start, s.Length + trailing, inner + "\n" + indent)], Selection.Caret(s.Start + inner.Length));
            }

            if (pairs.Any(p => p.Open[0] == opener)) indent += options.IndentUnit;
            var inserted = "\n" + indent;
            return new Local([new TextChange(s.Start, s.Length + trailing, inserted)], Selection.Caret(s.Start + inserted.Length));
        });
    }

    /// <summary>Tab: indent the lines of a multi-line selection, otherwise insert indentation to the next tab stop.</summary>
    public static bool Tab(Document doc, EditOptions options)
    {
        if (doc.Selections.Any(s => !s.IsEmpty && doc.Text.LineOf(s.Start) != doc.Text.LineOf(s.End))) return IndentLines(doc, options);
        var rope = doc.Text;
        return Run(doc, EditKind.Typing, s =>
        {
            if (!options.InsertSpaces) return new Local([new TextChange(s.Start, s.Length, "\t")], Selection.Caret(s.Start + 1));
            var line = rope.LineOf(s.Start);
            var start = rope.LineStart(line);
            var visual = TextNavigation.VisualColumn(rope.Substring(start, s.Start - start), s.Start - start, options.TabSize);
            var spaces = new string(' ', options.TabSize - (visual % options.TabSize));
            return new Local([new TextChange(s.Start, s.Length, spaces)], Selection.Caret(s.Start + spaces.Length));
        });
    }

    // ── Deleting ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Backspace: the selection, or the character before the caret (an empty auto-closed pair goes whole; in leading spaces, back to the previous tab stop).</summary>
    public static bool Backspace(Document doc, EditOptions options)
    {
        var rope = doc.Text;
        return Run(doc, EditKind.Deleting, s =>
        {
            if (!s.IsEmpty) return new Local([new TextChange(s.Start, s.Length, "")], Selection.Caret(s.Start));
            if (s.Start == 0) return Keep(s);
            var prev = rope[s.Start - 1];
            var next = s.Start < rope.Length ? rope[s.Start] : '\0';
            if (options.AutoClosePairs && options.Pairs.Any(p => p.Open[0] == prev && p.Close[0] == next && p.Open.Length == 1))
            {
                return new Local([new TextChange(s.Start - 1, 2, "")], Selection.Caret(s.Start - 1));
            }

            var lineStart = rope.LineStart(rope.LineOf(s.Start));
            if (options.InsertSpaces && prev == ' ' && s.Start > lineStart)
            {
                var head = rope.Substring(lineStart, s.Start - lineStart);
                if (head.All(ch => ch == ' '))
                {
                    var remove = head.Length % options.TabSize == 0 ? options.TabSize : head.Length % options.TabSize;
                    remove = Math.Min(remove, head.Length);
                    return new Local([new TextChange(s.Start - remove, remove, "")], Selection.Caret(s.Start - remove));
                }
            }

            var from = TextNavigation.PreviousChar(rope, s.Start);
            return new Local([new TextChange(from, s.Start - from, "")], Selection.Caret(from));
        });
    }

    /// <summary>Delete: the selection, or the character after the caret.</summary>
    public static bool Delete(Document doc)
    {
        var rope = doc.Text;
        return Run(doc, EditKind.Deleting, s =>
        {
            if (!s.IsEmpty) return new Local([new TextChange(s.Start, s.Length, "")], Selection.Caret(s.Start));
            var to = TextNavigation.NextChar(rope, s.Start);
            return to == s.Start ? Keep(s) : new Local([new TextChange(s.Start, to - s.Start, "")], Selection.Caret(s.Start));
        });
    }

    /// <summary>Ctrl+Backspace.</summary>
    public static bool DeleteWordLeft(Document doc)
    {
        var rope = doc.Text;
        return Run(doc, EditKind.Deleting, s =>
        {
            if (!s.IsEmpty) return new Local([new TextChange(s.Start, s.Length, "")], Selection.Caret(s.Start));
            var from = TextNavigation.WordStartLeft(rope, s.Start);
            return new Local([new TextChange(from, s.Start - from, "")], Selection.Caret(from));
        });
    }

    /// <summary>Ctrl+Delete.</summary>
    public static bool DeleteWordRight(Document doc)
    {
        var rope = doc.Text;
        return Run(doc, EditKind.Deleting, s =>
        {
            if (!s.IsEmpty) return new Local([new TextChange(s.Start, s.Length, "")], Selection.Caret(s.Start));
            var to = TextNavigation.WordEndRight(rope, s.Start);
            return new Local([new TextChange(s.Start, to - s.Start, "")], Selection.Caret(s.Start));
        });
    }

    // ── Moving the carets ────────────────────────────────────────────────────────────────────────

    public static void Left(Document doc, bool select) => Move(doc, s =>
        !select && !s.IsEmpty ? Selection.Caret(s.Start) : Extend(s, TextNavigation.PreviousChar(doc.Text, s.Active), select));

    public static void Right(Document doc, bool select) => Move(doc, s =>
        !select && !s.IsEmpty ? Selection.Caret(s.End) : Extend(s, TextNavigation.NextChar(doc.Text, s.Active), select));

    public static void WordLeft(Document doc, bool select) => Move(doc, s => Extend(s, TextNavigation.WordStartLeft(doc.Text, s.Active), select));

    public static void WordRight(Document doc, bool select) => Move(doc, s => Extend(s, TextNavigation.WordEndRight(doc.Text, s.Active), select));

    /// <summary>Up a line (or <paramref name="lines"/>); <paramref name="step"/> maps a line and a count to the line reached, skipping folded lines.</summary>
    public static void Up(Document doc, bool select, EditOptions options, int lines = 1, Func<int, int, int>? step = null) => Vertical(doc, -lines, select, options, step);

    public static void Down(Document doc, bool select, EditOptions options, int lines = 1, Func<int, int, int>? step = null) => Vertical(doc, lines, select, options, step);

    /// <summary>Home: to the first non-blank character, or to column 0 when already there (smart home).</summary>
    public static void Home(Document doc, bool select) => Move(doc, s =>
    {
        var rope = doc.Text;
        var line = rope.LineOf(s.Active);
        var start = rope.LineStart(line);
        var first = start + TextNavigation.Indentation(rope.Line(line)).Length;
        return Extend(s, s.Active == first ? start : first, select);
    });

    public static void End(Document doc, bool select) => Move(doc, s =>
    {
        var line = doc.Text.LineOf(s.Active);
        return Extend(s, doc.Text.LineStart(line) + doc.Text.LineLength(line), select);
    });

    public static void DocumentStart(Document doc, bool select) => Move(doc, s => Extend(s, 0, select));

    public static void DocumentEnd(Document doc, bool select) => Move(doc, s => Extend(s, doc.Text.Length, select));

    public static void SelectAll(Document doc)
    {
        doc.BreakUndoGroup();
        doc.SetSelections([new Selection(0, doc.Text.Length)]);
    }

    /// <summary>Escape: back to the primary caret alone.</summary>
    public static void SingleCaret(Document doc)
    {
        doc.SetSelections([Selection.Caret(doc.Primary.Active)]);
    }

    private static Selection Extend(Selection s, int to, bool select) => select ? new Selection(s.Anchor, to) : Selection.Caret(to);

    private static void Vertical(Document doc, int lines, bool select, EditOptions options, Func<int, int, int>? step) => Move(doc, s =>
    {
        var rope = doc.Text;
        if (!select && !s.IsEmpty)
        {
            // Up from a selection starts at its top, down at its bottom.
            s = Selection.Caret(lines < 0 ? s.Start : s.End);
        }

        var (line, column) = rope.Position(s.Active);
        var want = s.Column >= 0 ? s.Column : TextNavigation.VisualColumn(rope.Line(line), column, options.TabSize);
        var target = step?.Invoke(line, lines) ?? line + lines;
        if (step is not null && target == line) target = line + lines < 0 ? -1 : line + lines >= rope.LineCount ? rope.LineCount : line;
        int to;
        if (target < 0) to = 0;
        else if (target >= rope.LineCount) to = rope.Length;
        else to = rope.LineStart(target) + TextNavigation.ColumnOfVisual(rope.Line(target), want, options.TabSize);
        return select ? new Selection(s.Anchor, to, want) : Selection.Caret(to, want);
    });

    // ── More carets ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Ctrl+Alt+Up / Down: a caret on the line above or below every caret, at the same visual column.</summary>
    public static void AddCaretVertically(Document doc, int direction, EditOptions options)
    {
        var rope = doc.Text;
        var added = new List<Selection>(doc.Selections);
        var primary = doc.Primary;
        Selection newest = primary;
        foreach (var s in doc.Selections)
        {
            var (line, column) = rope.Position(s.Active);
            var target = line + direction;
            if (target < 0 || target >= rope.LineCount) continue;
            var want = s.Column >= 0 ? s.Column : TextNavigation.VisualColumn(rope.Line(line), column, options.TabSize);
            var caret = Selection.Caret(rope.LineStart(target) + TextNavigation.ColumnOfVisual(rope.Line(target), want, options.TabSize), want);
            added.Add(caret);
            if (s == primary) newest = caret;
        }

        doc.SetSelections(added, added.IndexOf(newest));
    }

    /// <summary>
    /// Ctrl+D: with a caret, select the word under it; with a selection, also select the next place the same text
    /// appears (wrapping at the end), case-sensitive, as VS Code does.
    /// </summary>
    public static bool AddNextOccurrence(Document doc)
    {
        var rope = doc.Text;
        var primary = doc.Primary;
        if (primary.IsEmpty)
        {
            var (start, end) = TextNavigation.WordAt(rope, primary.Active);
            if (start == end) return false;
            var list = doc.Selections.ToList();
            list[doc.PrimaryIndex] = new Selection(start, end);
            doc.SetSelections(list, doc.PrimaryIndex);
            return true;
        }

        var needle = rope.Substring(primary.Start, primary.Length);
        var last = doc.Selections.MaxBy(s => s.End);
        var text = rope.ToString();
        var at = text.IndexOf(needle, last.End, StringComparison.Ordinal);
        if (at < 0) at = text.IndexOf(needle, 0, StringComparison.Ordinal);
        while (at >= 0 && doc.Selections.Any(s => s.Start == at && s.Length == needle.Length))
        {
            var next = text.IndexOf(needle, at + 1, StringComparison.Ordinal);
            if (next == at || next < 0 || next == primary.Start) return false;
            at = next;
        }

        if (at < 0) return false;
        var added = doc.Selections.ToList();
        var selection = new Selection(at, at + needle.Length);
        added.Add(selection);
        doc.SetSelections(added, added.Count - 1);
        return true;
    }

    /// <summary>Ctrl+Shift+L: select every place the primary selection's text (or the word under the caret) appears.</summary>
    public static bool SelectAllOccurrences(Document doc)
    {
        var rope = doc.Text;
        var primary = doc.Primary;
        var (start, end) = primary.IsEmpty ? TextNavigation.WordAt(rope, primary.Active) : (primary.Start, primary.End);
        if (start == end) return false;
        var needle = rope.Substring(start, end - start);
        var text = rope.ToString();
        var found = new List<Selection>();
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            found.Add(new Selection(at, at + needle.Length));
        }

        doc.SetSelections(found, Math.Max(0, found.FindIndex(s => s.Start == start)));
        return true;
    }

    // ── Lines ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The blocks of whole lines the selections cover (a selection ending at column 0 does not take that line), with
    /// overlapping and adjacent blocks merged so that line moves never touch the same line twice.
    /// </summary>
    public static List<(int First, int Last)> LineBlocks(Document doc)
    {
        var rope = doc.Text;
        var blocks = new List<(int First, int Last)>();
        foreach (var s in doc.Selections)
        {
            var first = rope.LineOf(s.Start);
            var last = rope.LineOf(s.End);
            if (last > first && s.End == rope.LineStart(last)) last--;
            if (blocks.Count > 0 && first <= blocks[^1].Last + 1) blocks[^1] = (blocks[^1].First, Math.Max(last, blocks[^1].Last));
            else blocks.Add((first, last));
        }

        return blocks;
    }

    /// <summary>Alt+Up / Alt+Down: the selected lines swap with the line above or below; the selections ride along.</summary>
    public static bool MoveLines(Document doc, int direction)
    {
        var rope = doc.Text;
        var blocks = LineBlocks(doc);
        if (direction < 0 && blocks[0].First == 0) return false;
        if (direction > 0 && blocks[^1].Last == rope.LineCount - 1) return false;
        var changes = new List<TextChange>();
        var moves = new List<(int From, int To, int Delta)>();
        foreach (var (first, last) in blocks)
        {
            var blockStart = rope.LineStart(first);
            var blockEnd = rope.LineStart(last) + rope.LineLength(last);
            var block = rope.Substring(blockStart, blockEnd - blockStart);
            if (direction < 0)
            {
                var aboveStart = rope.LineStart(first - 1);
                var above = rope.Line(first - 1);
                changes.Add(new TextChange(aboveStart, blockEnd - aboveStart, block + "\n" + above));
                moves.Add((blockStart, blockEnd, -(above.Length + 1)));
            }
            else
            {
                var belowStart = rope.LineStart(last + 1);
                var below = rope.Line(last + 1);
                changes.Add(new TextChange(blockStart, belowStart + below.Length - blockStart, below + "\n" + block));
                moves.Add((blockStart, blockEnd, below.Length + 1));
            }
        }

        var after = doc.Selections.Select(s =>
        {
            var m = moves.First(m => s.Start >= m.From && s.Start <= m.To);
            return s with { Anchor = s.Anchor + m.Delta, Active = s.Active + m.Delta };
        }).ToList();
        return doc.Apply(changes, after, EditKind.Other, doc.PrimaryIndex);
    }

    /// <summary>Shift+Alt+Up / Down: the selected lines are copied above or below; the selections go with the copy below.</summary>
    public static bool CopyLines(Document doc, int direction)
    {
        var rope = doc.Text;
        var blocks = LineBlocks(doc);
        var changes = new List<TextChange>();
        var shiftAt = new List<(int From, int To, int Delta)>();
        var shift = 0;
        foreach (var (first, last) in blocks)
        {
            var blockStart = rope.LineStart(first);
            var blockEnd = rope.LineStart(last) + rope.LineLength(last);
            var block = rope.Substring(blockStart, blockEnd - blockStart);
            changes.Add(new TextChange(blockEnd, 0, "\n" + block));
            // Copying down, the selection moves onto the copy; copying up, it stays on the original text, which is now the upper copy.
            shiftAt.Add((blockStart, blockEnd, shift + (direction > 0 ? block.Length + 1 : 0)));
            shift += block.Length + 1;
        }

        var after = doc.Selections.Select(s =>
        {
            var m = shiftAt.First(m => s.Start >= m.From && s.Start <= m.To);
            return s with { Anchor = s.Anchor + m.Delta, Active = s.Active + m.Delta };
        }).ToList();
        return doc.Apply(changes, after, EditKind.Other, doc.PrimaryIndex);
    }

    /// <summary>Ctrl+Shift+K: the selected lines go; each caret lands on the line that took their place, at the same visual column.</summary>
    public static bool DeleteLines(Document doc, EditOptions options)
    {
        var rope = doc.Text;
        var changes = new List<TextChange>();
        var carets = new List<Selection>();
        var shift = 0;
        foreach (var (first, last) in LineBlocks(doc))
        {
            var start = rope.LineStart(first);
            var end = last + 1 < rope.LineCount ? rope.LineStart(last + 1) : rope.Length;
            if (last + 1 >= rope.LineCount && first > 0) start--; // the last line takes the break before it
            var owner = doc.Selections.First(s => rope.LineOf(s.Active) >= first && rope.LineOf(s.Active) <= last);
            var (ownerLine, ownerColumn) = rope.Position(owner.Active);
            var column = TextNavigation.VisualColumn(rope.Line(ownerLine), ownerColumn, options.TabSize);
            changes.Add(new TextChange(start, end - start, ""));
            carets.Add(Selection.Caret(start + shift, column));
            shift -= end - start;
        }

        // Each caret is at the start of the line that took the block's place (or the end of the line above, for the last
        // line); it moves to its column there.
        var after = ChangeSet.Apply(rope, ChangeSet.Normalise(changes));
        var placed = carets.Select(c =>
        {
            var line = after.LineOf(Math.Min(c.Active, after.Length));
            return Selection.Caret(after.LineStart(line) + TextNavigation.ColumnOfVisual(after.Line(line), c.Column, options.TabSize), c.Column);
        }).ToList();
        return doc.Apply(changes, placed, EditKind.Other);
    }

    /// <summary>Ctrl+] (and Tab over several lines): one indentation unit more on every selected line that is not empty.</summary>
    public static bool IndentLines(Document doc, EditOptions options) => EditLines(doc, line => line.Length == 0 ? null : (0, 0, options.IndentUnit));

    /// <summary>Ctrl+[ and Shift+Tab: one indentation unit less on every selected line.</summary>
    public static bool OutdentLines(Document doc, EditOptions options) => EditLines(doc, line =>
    {
        if (line.StartsWith('\t')) return (0, 1, "");
        var spaces = 0;
        while (spaces < options.TabSize && spaces < line.Length && line[spaces] == ' ') spaces++;
        return spaces == 0 ? null : (0, spaces, "");
    });

    /// <summary>
    /// Ctrl+/: comments the selected lines with the language's line comment, at the shallowest indentation among them, or
    /// uncomments them when every non-blank one is already commented.
    /// </summary>
    public static bool ToggleLineComment(Document doc, EditOptions options)
    {
        if (options.LineComment is not { } mark) return ToggleBlockComment(doc, options);
        var rope = doc.Text;
        var lines = LineBlocks(doc).SelectMany(b => Enumerable.Range(b.First, b.Last - b.First + 1)).Select(rope.Line).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count == 0) return false;
        var uncomment = lines.All(l => l.TrimStart().StartsWith(mark, StringComparison.Ordinal));
        var depth = lines.Min(l => TextNavigation.Indentation(l).Length);
        return EditLines(doc, line =>
        {
            if (line.Trim().Length == 0) return null;
            if (uncomment)
            {
                var at = line.IndexOf(mark, StringComparison.Ordinal);
                var length = mark.Length + (at + mark.Length < line.Length && line[at + mark.Length] == ' ' ? 1 : 0);
                return (at, length, "");
            }

            return (depth, 0, mark + " ");
        });
    }

    /// <summary>Shift+Alt+A: wraps each selection in the block comment marks, or unwraps one already wrapped.</summary>
    public static bool ToggleBlockComment(Document doc, EditOptions options)
    {
        if (options.BlockComment is not { } marks) return false;
        var rope = doc.Text;
        return Run(doc, EditKind.Other, s =>
        {
            var text = rope.Substring(s.Start, s.Length);
            var (open, close) = marks;
            if (text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal) && text.Length >= open.Length + close.Length)
            {
                var inner = text[open.Length..^close.Length].Trim(' ');
                return new Local([new TextChange(s.Start, s.Length, inner)], new Selection(s.Start, s.Start + inner.Length));
            }

            var wrapped = open + " " + text + " " + close;
            return new Local([new TextChange(s.Start, s.Length, wrapped)], s.IsEmpty ? Selection.Caret(s.Start + open.Length + 1) : new Selection(s.Start, s.Start + wrapped.Length));
        });
    }

    /// <summary>Applies one change per selected line (column, removed length, inserted text), keeping the selections over the same text.</summary>
    private static bool EditLines(Document doc, Func<string, (int Column, int Remove, string Insert)?> edit)
    {
        var rope = doc.Text;
        var changes = new List<TextChange>();
        foreach (var (first, last) in LineBlocks(doc))
        {
            for (var line = first; line <= last; line++)
            {
                if (edit(rope.Line(line)) is { } e) changes.Add(new TextChange(rope.LineStart(line) + e.Column, e.Remove, e.Insert));
            }
        }

        if (changes.Count == 0) return false;
        var set = ChangeSet.Normalise(changes);
        // The start of a selection that begins at a line's start stays there when the line is indented (so the whole line
        // stays selected); every other edge, and every caret, moves with its text.
        var after = doc.Selections.Select(s => s with
        {
            Anchor = ChangeSet.Map(s.Anchor, set, stickAfter: s.IsEmpty || s.Anchor != s.Start),
            Active = ChangeSet.Map(s.Active, set, stickAfter: s.IsEmpty || s.Active != s.Start),
        }).ToList();
        return doc.Apply(set, after, EditKind.Other, doc.PrimaryIndex);
    }

    // ── Clipboard ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What Copy puts on the clipboard: the selected texts joined by line breaks, or, when every selection is empty, the
    /// whole lines the carets are on (VS Code's "copy line"). <c>WholeLines</c> tells Paste to insert above the caret's line.
    /// </summary>
    public static (string Text, bool WholeLines) CopyText(Document doc)
    {
        var rope = doc.Text;
        if (doc.Selections.All(s => s.IsEmpty))
        {
            var lines = doc.Selections.Select(s => rope.LineOf(s.Active)).Distinct().Select(l => rope.Line(l) + "\n");
            return (string.Concat(lines), true);
        }

        return (string.Join('\n', doc.Selections.Where(s => !s.IsEmpty).Select(s => rope.Substring(s.Start, s.Length))), false);
    }

    /// <summary>Cut: Copy, then the selections (or the carets' whole lines) go.</summary>
    public static (string Text, bool WholeLines) Cut(Document doc, EditOptions options)
    {
        var copied = CopyText(doc);
        if (copied.WholeLines) DeleteLines(doc, options);
        else Run(doc, EditKind.Other, s => new Local([new TextChange(s.Start, s.Length, "")], Selection.Caret(s.Start)));
        return copied;
    }

    /// <summary>
    /// Paste: one piece of text at every selection, or, when the clipboard holds as many lines as there are carets, one
    /// line per caret. Whole lines copied by "copy line" go above the caret's line.
    /// </summary>
    public static bool Paste(Document doc, string text, bool wholeLines)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var pieces = text.TrimEnd('\n').Split('\n');
        var spread = doc.Selections.Length > 1 && pieces.Length == doc.Selections.Length;
        var rope = doc.Text;
        var index = 0;
        return Run(doc, EditKind.Other, s =>
        {
            var piece = spread ? pieces[index++] : text;
            if (wholeLines && !spread && s.IsEmpty)
            {
                var lineStart = rope.LineStart(rope.LineOf(s.Active));
                return new Local([new TextChange(lineStart, 0, piece)], Selection.Caret(s.Active + piece.Length));
            }

            return new Local([new TextChange(s.Start, s.Length, piece)], Selection.Caret(s.Start + piece.Length));
        });
    }

    /// <summary>Removes the spaces and tabs at the end of every line (a save option); the carets stay on their lines.</summary>
    public static bool TrimTrailingWhitespace(Document doc)
    {
        var rope = doc.Text;
        var changes = new List<TextChange>();
        var caretLines = doc.Selections.Select(s => rope.LineOf(s.Active)).ToHashSet();
        for (var line = 0; line < rope.LineCount; line++)
        {
            var text = rope.Line(line);
            var trimmed = text.TrimEnd(' ', '\t').Length;
            if (trimmed == text.Length) continue;
            // A caret in the trailing blanks keeps them, as VS Code does, so typing is not undone under the user.
            if (caretLines.Contains(line) && doc.Selections.Any(s => s.Active > rope.LineStart(line) + trimmed && rope.LineOf(s.Active) == line)) continue;
            changes.Add(new TextChange(rope.LineStart(line) + trimmed, text.Length - trimmed, ""));
        }

        if (changes.Count == 0) return false;
        var set = ChangeSet.Normalise(changes);
        return doc.Apply(set, doc.Selections.Select(s => s with { Anchor = ChangeSet.Map(s.Anchor, set), Active = ChangeSet.Map(s.Active, set) }).ToList(), EditKind.Other, doc.PrimaryIndex);
    }

    /// <summary>The text with the document's own line breaks, for saving.</summary>
    public static string ForSaving(Document doc)
    {
        if (doc.LineBreak == "\n") return doc.Text.ToString();
        var builder = new StringBuilder(doc.Text.Length + doc.Text.LineCount);
        foreach (var chunk in doc.Text.Chunks(0, doc.Text.Length))
        {
            var span = chunk.Span;
            int at;
            while ((at = span.IndexOf('\n')) >= 0)
            {
                builder.Append(span[..at]).Append(doc.LineBreak);
                span = span[(at + 1)..];
            }

            builder.Append(span);
        }

        return builder.ToString();
    }
}
