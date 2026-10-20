namespace Wicked.Core.Files;

/// <summary>
/// Quick open's matcher: the query's characters must appear in order (case-insensitive); the score rewards matches at
/// the start of words and path parts, runs of consecutive characters, and matches in the file name over the folders,
/// as VS Code's fuzzy scorer does. Allocation-free per candidate, so a worker can score 100k paths per keystroke.
/// </summary>
public static class FuzzyMatch
{
    /// <summary>The score of <paramref name="candidate"/> for <paramref name="query"/>, or -1 when it does not match.</summary>
    public static int Score(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate)
    {
        if (query.Length == 0) return 0;
        var nameStart = candidate.LastIndexOfAny('/', '\\') + 1;

        // Prefer a match inside the file name: try it first, then the whole path.
        var inName = ScoreFrom(query, candidate, nameStart, nameStart);
        if (inName >= 0) return inName + 1000;
        return ScoreFrom(query, candidate, 0, nameStart);
    }

    private static int ScoreFrom(ReadOnlySpan<char> query, ReadOnlySpan<char> candidate, int from, int nameStart)
    {
        var score = 0;
        var q = 0;
        var previousMatch = -2;
        for (var i = from; i < candidate.Length && q < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[q])) continue;
            var points = 1;
            if (i == previousMatch + 1) points += 5;                           // consecutive
            if (i == 0 || IsBoundary(candidate[i - 1], candidate[i])) points += 8; // start of a word or path part
            if (candidate[i] == query[q]) points += 1;                         // same case
            if (i >= nameStart) points += 2;                                   // in the file name
            score += points;
            previousMatch = i;
            q++;
        }

        if (q < query.Length) return -1;
        // Shorter candidates win ties: "app.cs" over "application-settings.cs".
        return score * 100 - Math.Min(99, candidate.Length - from);
    }

    private static bool IsBoundary(char before, char c) =>
        before is '/' or '\\' or '_' or '-' or '.' or ' ' || (char.IsLower(before) && char.IsUpper(c));

    /// <summary>The indices of <paramref name="candidate"/> the query matched (for highlighting), or empty when it does not match.</summary>
    public static int[] Positions(string query, string candidate)
    {
        if (query.Length == 0) return [];
        var nameStart = candidate.LastIndexOfAny(['/', '\\']) + 1;
        foreach (var from in new[] { nameStart, 0 })
        {
            var positions = new List<int>(query.Length);
            var q = 0;
            for (var i = from; i < candidate.Length && q < query.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[q])) continue;
                positions.Add(i);
                q++;
            }

            if (q == query.Length) return [.. positions];
        }

        return [];
    }
}
