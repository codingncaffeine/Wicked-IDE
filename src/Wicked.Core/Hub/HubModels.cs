namespace Wicked.Core.Hub;

/// <summary>How a repo is gated: not at all, approved automatically after accepting the licence, or approved by the owner.</summary>
public enum Gating
{
    None,
    Auto,
    Manual,
}

/// <summary>What the GGUF header summary on a repo page says (the Hub reads it from the first GGUF file).</summary>
public sealed record HubGguf(string? Architecture, long? ContextLength, long? Total);

/// <summary>
/// A repo as <c>GET /api/models/{repo}</c> describes it. <c>Sha</c> is the commit the answer describes (a different value
/// later means the repo was updated, D7); <c>Siblings</c> lists every file path without sizes (the tree call has those).
/// </summary>
public sealed record HubModel(
    string Id,
    string Sha,
    DateTimeOffset? LastModified,
    Gating Gated,
    string? License,
    long Downloads,
    long Likes,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Siblings,
    HubGguf? Gguf);

/// <summary>One entry of a repo's file tree. <see cref="LfsSha256"/> is the file's real sha256 for LFS/Xet files (the <c>oid</c> field is a git blob id).</summary>
public sealed record HubFile(string Path, long Size, string Oid, string? LfsSha256, bool IsDirectory)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>One row of a model search.</summary>
public sealed record HubSearchResult(
    string Id,
    long Downloads,
    long Likes,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? LastModified,
    Gating Gated,
    IReadOnlyList<string> Tags);

public abstract class HubException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>401: the request needs a token (a gated or private repo asked without one, or a bad token).</summary>
public sealed class AuthRequiredException(string repo) : HubException($"Hugging Face needs a token for {repo} (401)");

/// <summary>403: the token is valid but the account has not been granted this repo (accept its licence on the website first).</summary>
public sealed class GatedRepoException(string repo) : HubException($"Access to {repo} is gated: accept its licence on huggingface.co (403)")
{
    public string Repo { get; } = repo;
}

public sealed class RepoNotFoundException(string repo) : HubException($"No such repo or revision on Hugging Face: {repo} (404)");

/// <summary>429: too many requests; <see cref="RetryAfter"/> is how long the Hub asked us to wait.</summary>
public sealed class RateLimitedException(TimeSpan retryAfter) : HubException($"Hugging Face rate limit hit; retry in {retryAfter.TotalSeconds:0} s")
{
    public TimeSpan RetryAfter { get; } = retryAfter;
}

/// <summary>One commit of a repo: its id, the first line of its message, and when it was made.</summary>
public sealed record HubCommit(string Id, string Title, DateTimeOffset? Date);
