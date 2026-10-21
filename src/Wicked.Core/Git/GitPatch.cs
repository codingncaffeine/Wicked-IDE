using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Wicked.Core.Git;

/// <summary>One line of a hunk: ' ' context, '+' added, '-' removed. <paramref name="NoNewline"/> marks "\ No newline at end of file" after it.</summary>
public sealed record PatchLine(char Kind, string Text, bool NoNewline = false);

/// <summary>A hunk of a unified diff, with the line numbers its header gave (1-based; a count of 0 means the start is the line before).</summary>
public sealed record PatchHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Heading, ImmutableArray<PatchLine> Lines)
{
    /// <summary>First old line the hunk covers, 1-based (git writes the line before when the old side is empty).</summary>
    public int OldFirst => OldCount == 0 ? OldStart + 1 : OldStart;

    public int NewFirst => NewCount == 0 ? NewStart + 1 : NewStart;

    /// <summary>Indexes (into <see cref="Lines"/>) of the lines that change something.</summary>
    public IEnumerable<int> ChangedLines => Lines.Select((l, i) => (l, i)).Where(t => t.l.Kind != ' ').Select(t => t.i);

    /// <summary>The first line of the hunk's new side that the hunk changes (0-based, for matching a gutter mark to a hunk).</summary>
    public int NewStartLine0 => Math.Max(0, NewFirst - 1);
}

/// <summary>A file's diff: the header git wrote and its hunks.</summary>
public sealed record FilePatch(ImmutableArray<string> Header, ImmutableArray<PatchHunk> Hunks)
{
    public bool IsEmpty => Hunks.IsEmpty;
}

/// <summary>What to do with the chosen hunks and lines.</summary>
public enum PatchAction
{
    /// <summary>Work tree to index (the patch is a work-tree-against-index diff).</summary>
    Stage,

    /// <summary>Index back to HEAD (the patch is an index-against-HEAD diff).</summary>
    Unstage,

    /// <summary>Work tree back to the index (the patch is a work-tree-against-index diff).</summary>
    Discard,
}

/// <summary>
/// The hunks, and within them the lines, to act on. A hunk listed with <c>null</c> lines is taken whole; otherwise only the
/// changed lines named (indexes into the hunk's <see cref="PatchHunk.Lines"/>).
/// </summary>
public sealed class PatchSelection
{
    private readonly Dictionary<int, HashSet<int>?> _hunks = [];

    public static PatchSelection OfHunks(params int[] hunks)
    {
        var s = new PatchSelection();
        foreach (var h in hunks) s._hunks[h] = null;
        return s;
    }

    public static PatchSelection OfLines(int hunk, IEnumerable<int> lines)
    {
        var s = new PatchSelection();
        s._hunks[hunk] = [.. lines];
        return s;
    }

    public PatchSelection AddLines(int hunk, IEnumerable<int> lines)
    {
        if (_hunks.TryGetValue(hunk, out var existing) && existing is null) return this;
        var set = existing ?? [];
        foreach (var l in lines) set.Add(l);
        _hunks[hunk] = set;
        return this;
    }


    /// <summary>
    /// The changes of a work-tree diff that fall in a range of lines of the working text (0-based, inclusive): the lines added or
    /// changed there, and the lines removed just above any of them. This is what "stage the selected lines" and "revert the
    /// selected lines" act on. Null when the range holds no change.
    /// </summary>
    public static PatchSelection? ForNewLines(FilePatch patch, int firstLine, int lastLine)
    {
        PatchSelection? selection = null;
        for (var h = 0; h < patch.Hunks.Length; h++)
        {
            var hunk = patch.Hunks[h];
            var line = hunk.NewFirst - 1;
            var picked = new List<int>();
            for (var i = 0; i < hunk.Lines.Length; i++)
            {
                var kind = hunk.Lines[i].Kind;
                if (kind == '-')
                {
                    // A removed line sits above the working line that follows it.
                    if (line >= firstLine && line <= lastLine) picked.Add(i);
                    continue;
                }

                if (kind == '+' && line >= firstLine && line <= lastLine) picked.Add(i);
                line++;
            }

            if (picked.Count > 0) (selection ??= new PatchSelection()).AddLines(h, picked);
        }

        return selection;
    }

    /// <summary>The whole hunk that covers a line of the working text (or has lines removed just above it), as one selection; null when no hunk does.</summary>
    public static PatchSelection? ForHunkAt(FilePatch patch, int line)
    {
        for (var h = 0; h < patch.Hunks.Length; h++)
        {
            var hunk = patch.Hunks[h];
            var first = hunk.NewFirst - 1;
            var covered = false;
            var at = first;
            foreach (var l in hunk.Lines)
            {
                if (l.Kind == '-') covered |= at == line;
                else
                {
                    if (l.Kind == '+') covered |= at == line;
                    at++;
                }
            }

            if (covered) return OfHunks(h);
        }

        return null;
    }

    internal bool Includes(int hunk, int line) => _hunks.TryGetValue(hunk, out var lines) && (lines is null || lines.Contains(line));

    internal bool Touches(int hunk) => _hunks.ContainsKey(hunk);
}

/// <summary>Reads unified diffs and builds the smaller patches that stage, unstage or discard part of one.</summary>
public static class GitPatch
{
    public static FilePatch Parse(string diff)
    {
        var lines = diff.Split('\n');
        var header = ImmutableArray.CreateBuilder<string>();
        var hunks = ImmutableArray.CreateBuilder<PatchHunk>();
        var i = 0;
        for (; i < lines.Length && !lines[i].StartsWith("@@ ", StringComparison.Ordinal); i++) header.Add(lines[i]);
        while (i < lines.Length)
        {
            if (!TryParseHeader(lines[i], out var oldStart, out var oldCount, out var newStart, out var newCount, out var heading))
            {
                i++;
                continue;
            }

            i++;
            var body = ImmutableArray.CreateBuilder<PatchLine>();
            int oldLeft = oldCount, newLeft = newCount;
            while (i < lines.Length && (oldLeft > 0 || newLeft > 0 || (i < lines.Length && lines[i].StartsWith('\\'))))
            {
                var line = lines[i];
                if (line.StartsWith('\\'))
                {
                    if (body.Count > 0) body[^1] = body[^1] with { NoNewline = true };
                    i++;
                    continue;
                }

                if (line.Length == 0)
                {
                    // A context line that was only a newline, or the end of the text.
                    if (i == lines.Length - 1) break;
                    body.Add(new PatchLine(' ', ""));
                    oldLeft--;
                    newLeft--;
                    i++;
                    continue;
                }

                var kind = line[0];
                if (kind == '+') newLeft--;
                else if (kind == '-') oldLeft--;
                else if (kind == ' ')
                {
                    oldLeft--;
                    newLeft--;
                }
                else break;
                body.Add(new PatchLine(kind, line[1..]));
                i++;
            }

            hunks.Add(new PatchHunk(oldStart, oldCount, newStart, newCount, heading, body.ToImmutable()));
        }

        return new FilePatch(header.ToImmutable(), hunks.ToImmutable());
    }

    private static bool TryParseHeader(string line, out int oldStart, out int oldCount, out int newStart, out int newCount, out string heading)
    {
        oldStart = oldCount = newStart = newCount = 0;
        heading = "";
        if (!line.StartsWith("@@ -", StringComparison.Ordinal)) return false;
        var end = line.IndexOf(" @@", 3, StringComparison.Ordinal);
        if (end < 0) return false;
        var ranges = line[3..end].Split(' ');
        if (ranges.Length != 2 || !Range(ranges[0].TrimStart('-'), out oldStart, out oldCount) || !Range(ranges[1].TrimStart('+'), out newStart, out newCount)) return false;
        heading = line[(end + 3)..].Trim();
        return true;
    }

    private static bool Range(string text, out int start, out int count)
    {
        var parts = text.Split(',');
        count = 1;
        return int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
            && (parts.Length == 1 || int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out count));
    }

    /// <summary>
    /// The patch for <paramref name="selection"/>, or null when it selects no change. Stage works on a work-tree diff and is
    /// applied to the index; Unstage works on an index diff and is applied in reverse to the index; Discard works on a
    /// work-tree diff and is applied in reverse to the work tree. Changes not selected stay as they are: a line left out of a
    /// stage stays unstaged, a line left out of an unstage stays staged.
    /// </summary>
    public static string? Build(FilePatch patch, PatchSelection selection, PatchAction action)
    {
        var forward = action == PatchAction.Stage;
        var body = new StringBuilder();
        var delta = 0;
        foreach (var (hunk, index) in patch.Hunks.Select((h, i) => (h, i)))
        {
            if (!selection.Touches(index)) continue;
            var lines = new List<PatchLine>();
            var changed = false;
            for (var l = 0; l < hunk.Lines.Length; l++)
            {
                var line = hunk.Lines[l];
                if (line.Kind == ' ')
                {
                    lines.Add(line);
                    continue;
                }

                if (selection.Includes(index, l))
                {
                    lines.Add(line);
                    changed = true;
                }
                else if (line.Kind == (forward ? '-' : '+'))
                {
                    // Not taking this change: the line stays as it is on the side that is applied to.
                    lines.Add(line with { Kind = ' ' });
                }
            }

            if (!changed) continue;
            var oldCount = lines.Count(x => x.Kind != '+');
            var newCount = lines.Count(x => x.Kind != '-');
            int oldStart, newStart;
            if (forward)
            {
                var first = hunk.OldFirst;
                oldStart = hunk.OldStart;
                newStart = newCount == 0 ? first + delta - 1 : first + delta;
            }
            else
            {
                var first = hunk.NewFirst;
                newStart = hunk.NewStart;
                oldStart = oldCount == 0 ? first - delta - 1 : first - delta;
            }

            delta += newCount - oldCount;
            body.Append(CultureInfo.InvariantCulture, $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@{(hunk.Heading.Length > 0 ? " " + hunk.Heading : "")}\n");
            foreach (var line in lines)
            {
                body.Append(line.Kind).Append(line.Text).Append('\n');
                if (line.NoNewline) body.Append("\\ No newline at end of file\n");
            }
        }

        if (body.Length == 0) return null;
        return string.Join('\n', Header(patch.Header)) + "\n" + body;
    }

    /// <summary>The header trimmed to the lines that name the file: mode and index lines would make git expect a whole-file change.</summary>
    private static List<string> Header(ImmutableArray<string> header)
    {
        var keep = header.Where(h => h.StartsWith("diff --git ", StringComparison.Ordinal) || h.StartsWith("--- ", StringComparison.Ordinal) || h.StartsWith("+++ ", StringComparison.Ordinal)).ToList();
        var from = keep.FindIndex(h => h.StartsWith("--- ", StringComparison.Ordinal));
        var to = keep.FindIndex(h => h.StartsWith("+++ ", StringComparison.Ordinal));
        if (from >= 0 && to >= 0)
        {
            if (keep[from].StartsWith("--- /dev/null", StringComparison.Ordinal)) keep[from] = "--- a/" + keep[to][6..];
            if (keep[to].StartsWith("+++ /dev/null", StringComparison.Ordinal)) keep[to] = "+++ b/" + keep[from][6..];
        }

        return keep;
    }
}

public sealed partial class GitRepository
{
    /// <summary>The diff of one file as hunks, ready for <see cref="GitRepository.ApplySelectionAsync(FilePatch, PatchAction, PatchSelection, CancellationToken)"/>: renames are not followed, so the patch names one path.</summary>
    public async Task<FilePatch> PatchAsync(string path, bool staged, CancellationToken ct = default)
    {
        List<string> args = ["--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", "--no-renames", "-U3"];
        if (staged) args.Add("--cached");
        args.AddRange(["--", path]);
        return GitPatch.Parse(await ReadAsync(args, null, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Stages, unstages or discards chosen hunks or lines of one file by applying a patch built from them. The patch is
    /// re-read from the repository now, so it is built from the file as it is, not as the panel last saw it.
    /// </summary>
    public async Task<GitResult> ApplySelectionAsync(string path, PatchAction action, PatchSelection selection, CancellationToken ct = default)
    {
        var patch = await PatchAsync(path, staged: action == PatchAction.Unstage, ct).ConfigureAwait(false);
        return await ApplySelectionAsync(patch, action, selection, ct).ConfigureAwait(false);
    }

    public async Task<GitResult> ApplySelectionAsync(FilePatch patch, PatchAction action, PatchSelection selection, CancellationToken ct = default)
    {
        var text = GitPatch.Build(patch, selection, action);
        if (text is null) return new GitResult(1, "", "Nothing is selected to apply.");
        List<string> args = ["apply", "--recount", "--whitespace=nowarn"];
        if (action != PatchAction.Discard) args.Add("--cached");
        if (action != PatchAction.Stage) args.Add("--reverse");
        args.Add("-");
        return await WriteAsync(args, text, ct).ConfigureAwait(false);
    }
}
