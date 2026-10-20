using System.Text;
using Microsoft.Extensions.Time.Testing;
using Wicked.Editor.Model;
using Wicked.Editor.Text;

namespace Wicked.Tests.Editor;

/// <summary>
/// Texts with their selections written in: '|' is a caret, "[...]" a selection with its caret at the end and "]...["
/// one with its caret at the start. The last marked selection is the primary one, as a user's newest is.
/// </summary>
internal static class Marked
{
    public static Document Doc(string marked, FakeTimeProvider? time = null)
    {
        var text = new StringBuilder();
        var selections = new List<Selection>();
        int? open = null;
        bool reversed = false;
        foreach (var c in marked)
        {
            switch (c)
            {
                case '|':
                    selections.Add(Selection.Caret(text.Length));
                    break;
                case '[' when open is null:
                    open = text.Length;
                    reversed = false;
                    break;
                case ']' when open is not null && !reversed:
                    selections.Add(new Selection(open.Value, text.Length));
                    open = null;
                    break;
                case ']' when open is null:
                    open = text.Length;
                    reversed = true;
                    break;
                case '[' when open is not null && reversed:
                    selections.Add(new Selection(text.Length, open.Value));
                    open = null;
                    break;
                default:
                    text.Append(c);
                    break;
            }
        }

        var doc = new Document(Rope.FromString(text.ToString()), "\n", time);
        doc.SetSelections(selections.Count == 0 ? [Selection.Caret(0)] : selections);
        return doc;
    }

    public static string Show(Document doc)
    {
        var text = doc.Text.ToString();
        var marks = new List<(int At, string Mark, int Order)>();
        foreach (var s in doc.Selections)
        {
            if (s.IsEmpty) marks.Add((s.Active, "|", 1));
            else if (!s.Reversed)
            {
                marks.Add((s.Start, "[", 2));
                marks.Add((s.End, "]", 0));
            }
            else
            {
                marks.Add((s.Start, "]", 2));
                marks.Add((s.End, "[", 0));
            }
        }

        var builder = new StringBuilder(text);
        foreach (var (at, mark, _) in marks.OrderByDescending(m => m.At).ThenBy(m => m.Order)) builder.Insert(at, mark);
        return builder.ToString();
    }
}
