using System.Text;
using System.Text.RegularExpressions;

namespace Wicked.Core.Terminal;

/// <summary>How a search matches.</summary>
public readonly record struct SearchOptions(bool CaseSensitive = false, bool WholeWord = false, bool Regex = false);

/// <summary>The lines a search reads, frozen: safe to search on any thread while the terminal carries on.</summary>
public sealed class SearchSnapshot(Line[] lines, long firstLine, int columns, ClusterTable clusters, int epoch)
{
    private const int MaxMatches = 10_000;

    public int Epoch { get; } = epoch;

    public long FirstLine { get; } = firstLine;

    /// <summary>
    /// Every match of <paramref name="query"/>, in order. A line the terminal wrapped is searched as one, so a match can
    /// run across rows. An invalid regular expression gives no matches.
    /// </summary>
    public List<TerminalMatch> Find(string query, SearchOptions options, CancellationToken ct)
    {
        var matches = new List<TerminalMatch>();
        if (query.Length == 0) return matches;
        Regex? regex = null;
        if (options.Regex || options.WholeWord)
        {
            var pattern = options.Regex ? query : Regex.Escape(query);
            if (options.WholeWord) pattern = $@"\b(?:{pattern})\b";
            try
            {
                regex = new Regex(pattern, (options.CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                return matches;
            }
        }

        var text = new StringBuilder();
        var cellOf = new List<int>();
        var comparison = options.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var i = 0;
        while (i < lines.Length && matches.Count < MaxMatches)
        {
            ct.ThrowIfCancellationRequested();
            var first = i;
            text.Clear();
            cellOf.Clear();
            while (true)
            {
                var row = lines[i];
                var wrapped = row.Wrapped && i + 1 < lines.Length;
                AppendRow(text, cellOf, row.Cells, (i - first) * columns, wrapped);
                i++;
                if (!wrapped) break;
            }

            var logical = text.ToString();
            if (logical.Length == 0) continue;
            try
            {
                if (regex is not null)
                {
                    for (var m = regex.Match(logical); m.Success && matches.Count < MaxMatches; m = m.NextMatch())
                    {
                        if (m.Length > 0) matches.Add(ToMatch(first, m.Index, m.Length, cellOf, lines, first));
                    }
                }
                else
                {
                    for (var at = logical.IndexOf(query, comparison); at >= 0 && matches.Count < MaxMatches; at = logical.IndexOf(query, at + 1, comparison))
                    {
                        matches.Add(ToMatch(first, at, query.Length, cellOf, lines, first));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pattern that takes this long on one line is skipped for that line.
            }
        }

        return matches;
    }

    private void AppendRow(StringBuilder text, List<int> cellOf, Cell[] cells, int baseCell, bool wrapped)
    {
        var n = wrapped ? columns : cells.Length;
        // A wrapped row is read to its full width: its blanks are real spaces between words.
        for (var c = 0; c < n; c++)
        {
            var cell = c < cells.Length ? cells[c] : default;
            if ((cell.Flags & CellFlags.WideTail) != 0) continue;
            var piece = (cell.Flags & CellFlags.Cluster) != 0 ? clusters[cell.Rune] : cell.Rune == 0 ? " " : char.ConvertFromUtf32(cell.Rune);
            text.Append(piece);
            for (var k = 0; k < piece.Length; k++) cellOf.Add(baseCell + c);
        }
    }

    private TerminalMatch ToMatch(int firstIndex, int charStart, int charLength, List<int> cellOf, Line[] all, int firstRow)
    {
        var startCell = cellOf[charStart];
        var lastCell = cellOf[charStart + charLength - 1];
        var lastRow = all[firstRow + lastCell / columns];
        var lastCol = lastCell % columns;
        var width = lastCol < lastRow.Cells.Length && (lastRow.Cells[lastCol].Flags & CellFlags.Wide) != 0 ? 2 : 1;
        var start = new TerminalPoint(FirstLine + firstIndex + startCell / columns, startCell % columns);
        var end = new TerminalPoint(FirstLine + firstIndex + lastCell / columns, lastCol + width);
        return new TerminalMatch(start, end);
    }
}
