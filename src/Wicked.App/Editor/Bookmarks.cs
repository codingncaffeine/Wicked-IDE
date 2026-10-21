using Wicked.App.Workbench;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.App.Editor;

/// <summary>
/// Bookmarks (Ctrl+Alt+K, the Bookmarks extension's keys): lines marked to come back to, a ribbon in the gutter, kept with
/// their lines through edits (they are a decoration layer), Ctrl+Alt+L / Ctrl+Alt+J to the next and previous across the
/// open files, and a list in the palette.
/// </summary>
public sealed class Bookmarks(Wicked.App.Workbench.Workbench bench)
{
    public const string Layer = "bookmarks";

    private readonly Dictionary<Document, DecorationSet> _marks = [];

    private static DecorationSet Of(CodeEditor editor) => editor.Decorations[Layer];

    /// <summary>Marks or unmarks the caret's line in the active editor.</summary>
    public void Toggle(EditorTab? tab)
    {
        if (tab?.Editor is not { } editor) return;
        var doc = tab.Document;
        var key = DecorationSet.LineKey(doc.Text, doc.Text.LineOf(doc.Primary.Active));
        var current = Of(editor).Gutter.ToList();
        if (current.RemoveAll(g => g.LineStart == key) == 0) current.Add(new GutterDecoration(key, GutterGlyph.Bookmark));
        var set = DecorationSet.OfGutter(current);
        _marks[doc] = set;
        foreach (var e in bench.AllTabs.Where(t => ReferenceEquals(t.Document, doc)).Select(t => t.Editor).OfType<CodeEditor>()) e.SetDecorations(Layer, set, doc.Version);
    }

    /// <summary>Every bookmark, by file and line (as they are now, edits followed).</summary>
    public IReadOnlyList<(EditorTab Tab, int Line)> All()
    {
        var list = new List<(EditorTab, int)>();
        foreach (var tab in bench.AllTabs.Where(t => t.Editor is not null).DistinctBy(t => t.Document))
        {
            foreach (var g in Of(tab.Editor!).Gutter.Where(g => g.Glyph == GutterGlyph.Bookmark)) list.Add((tab, tab.Document.Text.LineOf(g.LineStart)));
        }

        return [.. list.OrderBy(b => b.Item1.Path ?? b.Item1.Title, StringComparer.Ordinal).ThenBy(b => b.Item2)];
    }

    /// <summary>The next (or previous) bookmark after the caret, wrapping across the files; its file brought forward.</summary>
    public void Step(EditorTab? from, int direction)
    {
        var all = All();
        if (all.Count == 0) return;
        var line = from is null ? -1 : from.Document.Text.LineOf(from.Document.Primary.Active);
        var at = all.ToList().FindIndex(b => ReferenceEquals(b.Tab, from) && b.Line == line);
        int next;
        if (at >= 0) next = (at + direction + all.Count) % all.Count;
        else
        {
            var after = all.ToList().FindIndex(b => ReferenceEquals(b.Tab, from) && b.Line > line);
            next = direction > 0 ? (after >= 0 ? after : 0) : (after > 0 ? after - 1 : all.Count - 1);
        }

        Go(all[next]);
    }

    public void Go((EditorTab Tab, int Line) mark)
    {
        var doc = mark.Tab.Document;
        var offset = doc.Text.LineStart(mark.Line);
        doc.SetSelections([Selection.Caret(offset)]);
        bench.Groups.FirstOrDefault(g => g.Tabs.Contains(mark.Tab))?.Activate(mark.Tab);
        mark.Tab.Editor?.Select(offset, offset);
    }
}
