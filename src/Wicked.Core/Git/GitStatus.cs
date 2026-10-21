using System.Collections.Immutable;
using System.Globalization;

namespace Wicked.Core.Git;

/// <summary>How a file differs, on one side (the index against HEAD, or the work tree against the index).</summary>
public enum GitChangeKind
{
    None,
    Modified,
    Added,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Untracked,
    Conflicted,
    Ignored,
}

/// <summary>
/// One entry of <c>git status</c>. <see cref="Code"/> is git's two-letter XY ("M.", ".M", "R.", "UU", "??"); for a rename
/// <see cref="OriginalPath"/> is where the file came from. Paths are relative to the repository root with '/'.
/// </summary>
public sealed record GitChange(string Path, string? OriginalPath, GitChangeKind Index, GitChangeKind WorkTree, string Code, bool IsSubmodule = false)
{
    public bool IsConflicted => Index == GitChangeKind.Conflicted;

    public bool IsUntracked => WorkTree == GitChangeKind.Untracked;

    public bool IsStaged => Index is not (GitChangeKind.None or GitChangeKind.Conflicted);

    /// <summary>A change in the work tree to a tracked file (the "Changes" group).</summary>
    public bool IsUnstaged => WorkTree is not (GitChangeKind.None or GitChangeKind.Untracked or GitChangeKind.Ignored or GitChangeKind.Conflicted);

    /// <summary>For a conflicted entry, what each side did, in words ("Both modified", "Deleted by us").</summary>
    public string ConflictText => Code switch
    {
        "UU" => "Both modified",
        "AA" => "Both added",
        "DD" => "Both deleted",
        "AU" => "Added by us",
        "UA" => "Added by them",
        "DU" => "Deleted by us",
        "UD" => "Deleted by them",
        _ => "",
    };
}

/// <summary>The current branch: its commit, its upstream, and how far apart they are.</summary>
public sealed record GitBranchInfo(string? Oid, string? Head, string? Upstream, int Ahead, int Behind)
{
    public static readonly GitBranchInfo Unknown = new(null, null, null, 0, 0);

    /// <summary>HEAD points at a commit, not a branch.</summary>
    public bool IsDetached => Head is null;

    public bool HasUpstream => Upstream is not null;

    /// <summary>The branch has no commit yet (a fresh <c>git init</c>).</summary>
    public bool IsUnborn => Oid is null;
}

/// <summary>An operation that stopped halfway and is waiting for the user.</summary>
public enum GitOperation
{
    None,
    Merge,
    Rebase,
    CherryPick,
    Revert,
    Bisect,
}

/// <summary>A repository's state as the Source Control panel shows it.</summary>
public sealed record GitStatus(GitBranchInfo Branch, ImmutableArray<GitChange> Changes, GitOperation Operation)
{
    public static readonly GitStatus Empty = new(GitBranchInfo.Unknown, [], GitOperation.None);

    public IEnumerable<GitChange> Conflicts => Changes.Where(c => c.IsConflicted);

    public IEnumerable<GitChange> Staged => Changes.Where(c => c.IsStaged);

    public IEnumerable<GitChange> Unstaged => Changes.Where(c => c.IsUnstaged);

    public IEnumerable<GitChange> Untracked => Changes.Where(c => c.IsUntracked);

    public bool IsClean => Changes.IsEmpty;
}

/// <summary>Reads <c>git status --porcelain=v2 --branch -z</c>.</summary>
public static class GitStatusParser
{
    public static GitStatus Parse(string output, GitOperation operation = GitOperation.None)
    {
        string? oid = null, head = null, upstream = null;
        int ahead = 0, behind = 0;
        var changes = ImmutableArray.CreateBuilder<GitChange>();
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var r = records[i];
            if (r.Length < 2) continue;
            switch (r[0])
            {
                case '#':
                    ParseHeader(r, ref oid, ref head, ref upstream, ref ahead, ref behind);
                    break;
                case '1' when r.Length > 2:
                {
                    var f = r.Split(' ', 9);
                    if (f.Length == 9) changes.Add(Entry(f[8], null, f[1], f[2]));
                    break;
                }

                case '2':
                {
                    var f = r.Split(' ', 10);
                    if (f.Length == 10)
                    {
                        changes.Add(Entry(f[9], i + 1 < records.Length ? records[i + 1] : null, f[1], f[2]));
                        i++; // the original path is the next record
                    }

                    break;
                }

                case 'u':
                {
                    var f = r.Split(' ', 11);
                    if (f.Length == 11) changes.Add(new GitChange(f[10], null, GitChangeKind.Conflicted, GitChangeKind.Conflicted, f[1], f[2].StartsWith('S')));
                    break;
                }

                case '?':
                    changes.Add(new GitChange(r[2..], null, GitChangeKind.None, GitChangeKind.Untracked, "??"));
                    break;
                case '!':
                    break; // ignored files are not asked for
            }
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new GitStatus(new GitBranchInfo(oid, head, upstream, ahead, behind), changes.ToImmutable(), operation);
    }

    private static void ParseHeader(string line, ref string? oid, ref string? head, ref string? upstream, ref int ahead, ref int behind)
    {
        if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
        {
            var value = line[13..];
            oid = value == "(initial)" ? null : value;
        }
        else if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
        {
            var value = line[14..];
            head = value == "(detached)" ? null : value;
        }
        else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
        {
            upstream = line[18..];
        }
        else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
        {
            var parts = line[12..].Split(' ');
            if (parts.Length == 2)
            {
                _ = int.TryParse(parts[0].TrimStart('+'), NumberStyles.Integer, CultureInfo.InvariantCulture, out ahead);
                _ = int.TryParse(parts[1].TrimStart('-'), NumberStyles.Integer, CultureInfo.InvariantCulture, out behind);
            }
        }
    }

    private static GitChange Entry(string path, string? original, string xy, string sub) =>
        new(path, original, Kind(xy[0]), Kind(xy[1]), xy, sub.StartsWith('S'));

    private static GitChangeKind Kind(char c) => c switch
    {
        'M' => GitChangeKind.Modified,
        'A' => GitChangeKind.Added,
        'D' => GitChangeKind.Deleted,
        'R' => GitChangeKind.Renamed,
        'C' => GitChangeKind.Copied,
        'T' => GitChangeKind.TypeChanged,
        _ => GitChangeKind.None,
    };
}
