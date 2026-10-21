using System.Collections.Immutable;

namespace Wicked.Core.Git;

/// <summary>A local or remote branch as <c>git for-each-ref</c> lists it: "main", or "origin/main" for a remote one; Upstream is the branch it tracks, or null.</summary>
public sealed record GitBranch(string Name, bool IsRemote, bool IsCurrent, string? Upstream, string Oid, string Subject, DateTimeOffset Date, int Ahead, int Behind)
{
    /// <summary>The name without its remote ("main" for "origin/main").</summary>
    public string ShortName => IsRemote && Name.Contains('/') ? Name[(Name.IndexOf('/') + 1)..] : Name; // not a path: a ref name

    public string? Remote => IsRemote && Name.Contains('/') ? Name[..Name.IndexOf('/')] : null; // not a path: a ref name
}

public sealed record GitTag(string Name, string Oid, bool IsAnnotated, string Subject);

public sealed record GitRemote(string Name, string FetchUrl, string PushUrl);

public sealed record GitStash(int Index, string Ref, string Message, string Branch, DateTimeOffset Date);

/// <summary>A checked-out work tree of the repository (the main one and any added by <c>git worktree add</c>).</summary>
public sealed record GitWorktree(string Path, string? Head, string? Branch, bool IsBare, bool IsLocked);

public enum GitRefKind
{
    Head,
    LocalBranch,
    RemoteBranch,
    Tag,
}

/// <summary>A name that points at a commit, for the decorations on a log row.</summary>
public sealed record GitRef(string Name, GitRefKind Kind);

public sealed record GitCommit(string Hash, ImmutableArray<string> Parents, string Author, string AuthorEmail, DateTimeOffset Date, string Subject, ImmutableArray<GitRef> Refs)
{
    public string ShortHash => Hash.Length > 7 ? Hash[..7] : Hash;

    public bool IsMerge => Parents.Length > 1;
}

/// <summary>A file a commit touched.</summary>
public sealed record GitCommitFile(string Path, string? OriginalPath, GitChangeKind Kind);

/// <summary>What the commit box asks for.</summary>
public sealed record GitCommitOptions(string Message, bool Amend = false, bool SignOff = false, bool NoVerify = false);

/// <summary>How a pull ended up integrating the remote's commits.</summary>
public enum GitPullMode
{
    /// <summary>Whatever the user's git configuration says (pull.rebase); a divergence git will not guess falls back to a merge.</summary>
    Configured,
    Merge,
    Rebase,
    FastForwardOnly,
}
