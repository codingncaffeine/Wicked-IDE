using System.Text;
using Wicked.Core.Terminal;

namespace Wicked.App.Terminal;

/// <summary>Finds the link under a cell of a frame: an OSC 8 hyperlink the program drew, or an address or file reference in the text.</summary>
internal static class TerminalLinks
{
    public static (TerminalPoint Start, TerminalPoint End, TerminalLinkActivation Link)? FindAt(TerminalFrame frame, int row, int column)
    {
        if (row < 0 || row >= frame.Rows) return null;
        var cells = frame.Lines[row].Cells;
        if (column >= cells.Length) return null;

        // A link the program marked itself: the run of cells that share its id.
        var id = cells[column].Link;
        if (id != 0 && frame.Links[id] is { } url)
        {
            var from = column;
            var to = column;
            while (from > 0 && cells[from - 1].Link == id) from--;
            while (to + 1 < cells.Length && cells[to + 1].Link == id) to++;
            return (new TerminalPoint(frame.TopLine + row, from), new TerminalPoint(frame.TopLine + row, to + 1), new TerminalLinkActivation(LinkKind.Url, url, null, null));
        }

        // Otherwise the line's text, rows the terminal wrapped joined into one.
        var first = row;
        while (first > 0 && frame.Lines[first - 1].Wrapped) first--;
        var last = row;
        while (last + 1 < frame.Rows && frame.Lines[last].Wrapped) last++;

        var text = new StringBuilder();
        var cellOf = new List<(int Row, int Column)>();
        var cursorChar = -1;
        for (var r = first; r <= last; r++)
        {
            var rowCells = frame.Lines[r].Cells;
            var width = r < last ? frame.Columns : rowCells.Length;
            for (var c = 0; c < width; c++)
            {
                var cell = c < rowCells.Length ? rowCells[c] : default;
                if ((cell.Flags & CellFlags.WideTail) != 0) continue;
                var piece = frame.TextOf(cell);
                if (r == row && c <= column) cursorChar = text.Length;
                text.Append(piece);
                for (var k = 0; k < piece.Length; k++) cellOf.Add((r, c));
            }
        }

        if (cursorChar < 0 || cursorChar >= text.Length) return null;
        var link = LinkDetector.At(text.ToString(), cursorChar);
        if (link is null) return null;
        var (startRow, startCol) = cellOf[link.Start];
        var (endRow, endCol) = cellOf[link.Start + link.Length - 1];
        var endCell = frame.Lines[endRow].Cells;
        var endWidth = endCol < endCell.Length && (endCell[endCol].Flags & CellFlags.Wide) != 0 ? 2 : 1;
        return (new TerminalPoint(frame.TopLine + startRow, startCol), new TerminalPoint(frame.TopLine + endRow, endCol + endWidth), new TerminalLinkActivation(link.Kind, link.Target, link.Line, link.Column));
    }
}
