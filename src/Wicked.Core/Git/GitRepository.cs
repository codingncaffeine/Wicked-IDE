using System.Collections.Immutable;
using System.Globalization;

namespace Wicked.Core.Git;

/// <summary>
/// One git repository, driven by the installed <c>git</c> (it respects the user's configuration, hooks, LFS and credential
/// helpers). Every call is async and runs on the thread pool. Reads run side by side; writes queue so two never race on the
/// index. Paths in and out are relative to <see cref="Root"/> with '/' separators.
/// </summary>
public sealed partial class GitRepository
{
    private readonly SemaphoreSlim _writes = new(1, 1);

    private GitRepository(string root, string gitDir, string commonDir, GitEnvironment env)
    {
        Root = root;
        GitDir = gitDir;
        CommonDir = commonDir;
        Environment = env;
    }

    /// <summary>The work tree's folder.</summary>
    public string Root { get; }

    /// <summary>The repository's git directory (a linked work tree has its own, inside the common one).</summary>
    public string GitDir { get; }

    /// <summary>The git directory shared by every work tree: refs and objects live here.</summary>
    public string CommonDir { get; }

    public GitEnvironment Environment { get; }

    public string Name => Path.GetFileName(Root.TrimEnd(Path.DirectorySeparatorChar));

    /// <summary>The repository whose work tree contains <paramref name="folder"/>, or null when it is in none.</summary>
    public static async Task<GitRepository?> OpenAsync(string folder, GitEnvironment env, CancellationToken ct = default)
    {
        if (!Directory.Exists(folder)) return null;
        GitResult result;
        try
        {
            result = await GitProcess.RunAsync(env, folder, ["rev-parse", "--show-toplevel", "--absolute-git-dir", "--git-common-dir", "--is-bare-repository"], null, ct).ConfigureAwait(false);
        }
        catch (GitException)
        {
            return null;
        }

        if (!result.Ok) return null;
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 4 || lines[3].Trim() == "true") return null;
        var root = Path.GetFullPath(lines[0].Trim());
        var gitDir = lines[1].Trim();
        var common = Path.GetFullPath(lines[2].Trim(), folder);
        return new GitRepository(root, gitDir, common, env);
    }

    /// <summary>Makes <paramref name="folder"/> a repository (<c>git init</c>) and opens it.</summary>
    public static async Task<GitRepository?> InitAsync(string folder, GitEnvironment env, CancellationToken ct = default)
    {
        var result = await GitProcess.RunAsync(env, folder, ["init"], null, ct).ConfigureAwait(false);
        if (!result.Ok) throw new GitException(Describe(result, "git init failed"), result);
        return await OpenAsync(folder, env, ct).ConfigureAwait(false);
    }

    // ── Running git ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A read: runs at once, alongside other reads; throws <see cref="GitException"/> when git fails.</summary>
    public async Task<string> ReadAsync(IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        var result = await GitProcess.RunAsync(Environment, Root, args, stdin, ct).ConfigureAwait(false);
        if (!result.Ok) throw new GitException(Describe(result, $"git {args.First(a => !a.StartsWith('-'))} failed"), result);
        return result.Output;
    }

    /// <summary>A read that reports instead of throwing.</summary>
    public Task<GitResult> TryReadAsync(IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default) =>
        GitProcess.RunAsync(Environment, Root, args, stdin, ct);

    /// <summary>A write: waits for the writes before it, so the index is never touched by two commands at once.</summary>
    public async Task<GitResult> WriteAsync(IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await GitProcess.RunAsync(Environment, Root, args, stdin, ct).ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Several writes that must follow one another with nothing between (a stash then a checkout).</summary>
    public async Task<T> WriteScopeAsync<T>(Func<Task<T>> body, CancellationToken ct = default)
    {
        await _writes.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>The first line git said, or <paramref name="fallback"/>.</summary>
    internal static string Describe(GitResult result, string fallback)
    {
        var text = (result.Error.Length > 0 ? result.Error : result.Output).Trim();
        return text.Length == 0 ? fallback : text;
    }

    private static string PathList(IEnumerable<string> paths) => string.Join('\0', paths) + "\0";

    private static readonly string[] FromStdin = ["--pathspec-from-file=-", "--pathspec-file-nul"];

    // ── Status ───────────────────────────────────────────────────────────────────────────────────

    public async Task<GitStatus> StatusAsync(CancellationToken ct = default)
    {
        var output = await ReadAsync(["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], null, ct).ConfigureAwait(false);
        var operation = await Task.Run(DetectOperation, ct).ConfigureAwait(false);
        return GitStatusParser.Parse(output, operation);
    }

    private GitOperation DetectOperation()
    {
        bool Exists(string name) => File.Exists(Path.Combine(GitDir, name)) || Directory.Exists(Path.Combine(GitDir, name));
        if (Exists("rebase-merge") || Exists("rebase-apply")) return GitOperation.Rebase;
        if (Exists("MERGE_HEAD")) return GitOperation.Merge;
        if (Exists("CHERRY_PICK_HEAD")) return GitOperation.CherryPick;
        if (Exists("REVERT_HEAD")) return GitOperation.Revert;
        if (Exists("BISECT_LOG")) return GitOperation.Bisect;
        return GitOperation.None;
    }

    // ── Staging ──────────────────────────────────────────────────────────────────────────────────

    public Task<GitResult> StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        paths.Count == 0 ? Task.FromResult(new GitResult(0, "", "")) : WriteAsync(["--literal-pathspecs", "add", .. FromStdin], PathList(paths), ct);

    public Task<GitResult> StageAllAsync(CancellationToken ct = default) => WriteAsync(["add", "-A"], null, ct);

    public async Task<GitResult> UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return new GitResult(0, "", "");
        var result = await WriteAsync(["--literal-pathspecs", "restore", "--staged", .. FromStdin], PathList(paths), ct).ConfigureAwait(false);
        if (result.Ok || !result.Error.Contains("HEAD", StringComparison.Ordinal)) return result;
        // No commit yet, so there is no HEAD to restore from: drop the paths from the index instead.
        return await WriteAsync(["--literal-pathspecs", "rm", "--cached", "-r", "-q", "--ignore-unmatch", .. FromStdin], PathList(paths), ct).ConfigureAwait(false);
    }

    public async Task<GitResult> UnstageAllAsync(CancellationToken ct = default)
    {
        var result = await WriteAsync(["reset", "-q"], null, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>Throws away the work tree's changes to tracked files (they return to what the index holds).</summary>
    public Task<GitResult> DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
        paths.Count == 0 ? Task.FromResult(new GitResult(0, "", "")) : WriteAsync(["--literal-pathspecs", "restore", "--worktree", .. FromStdin], PathList(paths), ct);

    /// <summary>Deletes untracked files (and folders in the list).</summary>
    public async Task<GitResult> CleanAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        var last = new GitResult(0, "", "");
        foreach (var chunk in paths.Chunk(200))
        {
            last = await WriteAsync(["--literal-pathspecs", "clean", "-f", "-d", "-q", "--", .. chunk], null, ct).ConfigureAwait(false);
            if (!last.Ok) return last;
        }

        return last;
    }

    /// <summary>Resolving a conflict by taking one side whole: <c>--ours</c> or <c>--theirs</c>, then staged.</summary>
    public async Task<GitResult> TakeSideAsync(string path, bool ours, CancellationToken ct = default)
    {
        var result = await WriteAsync(["--literal-pathspecs", "checkout", ours ? "--ours" : "--theirs", "--", path], null, ct).ConfigureAwait(false);
        return result.Ok ? await StageAsync([path], ct).ConfigureAwait(false) : result;
    }

    // ── Commit ───────────────────────────────────────────────────────────────────────────────────

    public async Task<bool> HasStagedChangesAsync(CancellationToken ct = default)
    {
        var result = await TryReadAsync(["diff", "--cached", "--quiet", "--exit-code"], null, ct).ConfigureAwait(false);
        return result.ExitCode == 1;
    }

    /// <summary>
    /// Commits what is staged. An empty message and an empty commit are refused here, before git runs; hooks run as git
    /// runs them and what they print comes back in the result. Signing (commit.gpgsign) is git's own business.
    /// </summary>
    public async Task<GitResult> CommitAsync(GitCommitOptions options, CancellationToken ct = default)
    {
        var message = options.Message.Trim();
        if (message.Length == 0 && !options.Amend) return new GitResult(1, "", "A commit needs a message.");
        if (!options.Amend && !await HasStagedChangesAsync(ct).ConfigureAwait(false) && DetectOperation() == GitOperation.None)
        {
            return new GitResult(1, "", "There is nothing to commit: stage some changes first.");
        }

        List<string> args = ["commit"];
        if (options.Amend) args.Add("--amend");
        if (options.SignOff) args.Add("--signoff");
        if (options.NoVerify) args.Add("--no-verify");
        if (message.Length > 0) args.AddRange(["-F", "-"]);
        else args.Add("--no-edit");
        return await WriteAsync(args, message.Length > 0 ? message + "\n" : null, ct).ConfigureAwait(false);
    }

    /// <summary>The full message of HEAD, to fill the box when amending; null with no commit yet.</summary>
    public async Task<string?> LastCommitMessageAsync(CancellationToken ct = default)
    {
        var result = await TryReadAsync(["log", "-1", "--format=%B"], null, ct).ConfigureAwait(false);
        return result.Ok ? result.Output.TrimEnd() : null;
    }

    /// <summary>Undo Last Commit: HEAD moves back one commit and its changes stay staged. Returns the message it had.</summary>
    public async Task<(GitResult Result, string? Message)> UndoLastCommitAsync(CancellationToken ct = default)
    {
        var message = await LastCommitMessageAsync(ct).ConfigureAwait(false);
        var parent = await TryReadAsync(["rev-parse", "--verify", "-q", "HEAD~1"], null, ct).ConfigureAwait(false);
        // The first commit has no parent: removing it leaves an unborn branch with everything still staged.
        var result = parent.Ok
            ? await WriteAsync(["reset", "--soft", "HEAD~1"], null, ct).ConfigureAwait(false)
            : await WriteAsync(["update-ref", "-d", "HEAD"], null, ct).ConfigureAwait(false);
        return (result, result.Ok ? message : null);
    }

    // ── Configuration ────────────────────────────────────────────────────────────────────────────

    public async Task<string?> ConfigAsync(string key, CancellationToken ct = default)
    {
        var result = await TryReadAsync(["config", "--get", key], null, ct).ConfigureAwait(false);
        return result.Ok ? result.Output.Trim() : null;
    }

    // ── Branches and tags ────────────────────────────────────────────────────────────────────────

    public async Task<ImmutableArray<GitBranch>> BranchesAsync(CancellationToken ct = default)
    {
        const string Format = "%(HEAD)%1f%(refname)%1f%(upstream:short)%1f%(objectname)%1f%(committerdate:unix)%1f%(upstream:track,nobracket)%1f%(contents:subject)%1e";
        var output = await ReadAsync(["for-each-ref", $"--format={Format}", "--sort=-committerdate", "refs/heads", "refs/remotes"], null, ct).ConfigureAwait(false);
        var list = ImmutableArray.CreateBuilder<GitBranch>();
        foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.TrimStart('\n').Split('\u001f');
            if (f.Length < 7) continue;
            var full = f[1];
            var remote = full.StartsWith("refs/remotes/", StringComparison.Ordinal);
            var name = remote ? full["refs/remotes/".Length..] : full["refs/heads/".Length..];
            if (remote && name.EndsWith("/HEAD", StringComparison.Ordinal)) continue; // not a path: a ref name
            var (ahead, behind) = ParseTrack(f[5]);
            list.Add(new GitBranch(name, remote, f[0] == "*", f[2].Length > 0 ? f[2] : null, f[3], f[6].TrimEnd('\n'), UnixTime(f[4]), ahead, behind));
        }

        return list.ToImmutable();
    }

    private static (int Ahead, int Behind) ParseTrack(string track)
    {
        int ahead = 0, behind = 0;
        foreach (var part in track.Split(',', StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(' ');
            if (bits.Length != 2 || !int.TryParse(bits[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) continue;
            if (bits[0] == "ahead") ahead = n;
            else if (bits[0] == "behind") behind = n;
        }

        return (ahead, behind);
    }

    internal static DateTimeOffset UnixTime(string seconds) =>
        long.TryParse(seconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime() : default;

    /// <summary>Checks out a branch; a remote branch ("origin/x") becomes a local tracking branch. A commit or tag detaches HEAD.</summary>
    public Task<GitResult> CheckoutAsync(string target, bool detach = false, CancellationToken ct = default) =>
        WriteAsync(detach ? ["switch", "--detach", target] : ["switch", target], null, ct);

    public Task<GitResult> CheckoutRemoteAsync(string remoteBranch, CancellationToken ct = default) =>
        WriteAsync(["switch", "--track", remoteBranch], null, ct);

    public Task<GitResult> CreateBranchAsync(string name, string? startPoint = null, bool checkout = true, CancellationToken ct = default)
    {
        List<string> args = checkout ? ["switch", "-c", name] : ["branch", name];
        if (startPoint is not null) args.Add(startPoint);
        return WriteAsync(args, null, ct);
    }

    public Task<GitResult> RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) =>
        WriteAsync(["branch", "-m", oldName, newName], null, ct);

    public Task<GitResult> DeleteBranchAsync(string name, bool force = false, CancellationToken ct = default) =>
        WriteAsync(["branch", force ? "-D" : "-d", name], null, ct);

    public Task<GitResult> DeleteRemoteBranchAsync(string remote, string branch, CancellationToken ct = default) =>
        WriteAsync(["push", remote, "--delete", branch], null, ct);

    public Task<GitResult> SetUpstreamAsync(string branch, string upstream, CancellationToken ct = default) =>
        WriteAsync(["branch", $"--set-upstream-to={upstream}", branch], null, ct);

    public Task<GitResult> MergeAsync(string branch, CancellationToken ct = default) => WriteAsync(["merge", branch], null, ct);

    public Task<GitResult> RebaseOntoAsync(string branch, CancellationToken ct = default) => WriteAsync(["rebase", branch], null, ct);

    /// <summary>Abort the merge, rebase, cherry-pick or revert that stopped.</summary>
    public Task<GitResult> AbortAsync(GitOperation operation, CancellationToken ct = default) => operation switch
    {
        GitOperation.Merge => WriteAsync(["merge", "--abort"], null, ct),
        GitOperation.Rebase => WriteAsync(["rebase", "--abort"], null, ct),
        GitOperation.CherryPick => WriteAsync(["cherry-pick", "--abort"], null, ct),
        GitOperation.Revert => WriteAsync(["revert", "--abort"], null, ct),
        _ => Task.FromResult(new GitResult(1, "", "Nothing to abort.")),
    };

    /// <summary>Continue after the conflicts are resolved and staged (a merge is finished with the commit box instead).</summary>
    public Task<GitResult> ContinueAsync(GitOperation operation, CancellationToken ct = default) => operation switch
    {
        GitOperation.Rebase => WriteAsync(["rebase", "--continue"], null, ct),
        GitOperation.CherryPick => WriteAsync(["cherry-pick", "--continue"], null, ct),
        GitOperation.Revert => WriteAsync(["revert", "--continue"], null, ct),
        GitOperation.Merge => WriteAsync(["commit", "--no-edit"], null, ct),
        _ => Task.FromResult(new GitResult(1, "", "Nothing to continue.")),
    };

    public async Task<ImmutableArray<GitTag>> TagsAsync(CancellationToken ct = default)
    {
        var output = await ReadAsync(["for-each-ref", "--sort=-creatordate", "--format=%(refname:short)%1f%(objectname)%1f%(objecttype)%1f%(contents:subject)%1e", "refs/tags"], null, ct).ConfigureAwait(false);
        var list = ImmutableArray.CreateBuilder<GitTag>();
        foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.TrimStart('\n').Split('\u001f');
            if (f.Length >= 4) list.Add(new GitTag(f[0], f[1], f[2] == "tag", f[3].TrimEnd('\n')));
        }

        return list.ToImmutable();
    }

    public Task<GitResult> CreateTagAsync(string name, string? message = null, string? target = null, CancellationToken ct = default)
    {
        List<string> args = message is null ? ["tag", name] : ["tag", "-a", name, "-m", message];
        if (target is not null) args.Add(target);
        return WriteAsync(args, null, ct);
    }

    public Task<GitResult> DeleteTagAsync(string name, CancellationToken ct = default) => WriteAsync(["tag", "-d", name], null, ct);

    // ── Remotes ──────────────────────────────────────────────────────────────────────────────────

    public async Task<ImmutableArray<GitRemote>> RemotesAsync(CancellationToken ct = default)
    {
        var output = await ReadAsync(["remote", "-v"], null, ct).ConfigureAwait(false);
        var fetch = new Dictionary<string, string>();
        var push = new Dictionary<string, string>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var name = line[..tab];
            var rest = line[(tab + 1)..];
            var open = rest.LastIndexOf(" (", StringComparison.Ordinal);
            if (open < 0) continue;
            var url = rest[..open];
            if (rest[open..] == " (fetch)") fetch[name] = url;
            else push[name] = url;
        }
        return [.. fetch.Select(kv => new GitRemote(kv.Key, kv.Value, push.GetValueOrDefault(kv.Key, kv.Value)))];
    }

    public Task<GitResult> FetchAsync(string? remote = null, CancellationToken ct = default) =>
        WriteAsync(remote is null ? ["fetch", "--all", "--prune"] : ["fetch", "--prune", remote], null, ct);

    /// <summary>
    /// Pull. With <see cref="GitPullMode.Configured"/> git decides by the user's configuration; if the histories have diverged
    /// and the user has chosen neither merge nor rebase, git refuses, and the pull is repeated as a merge (git's old default).
    /// </summary>
    public async Task<GitResult> PullAsync(GitPullMode mode = GitPullMode.Configured, CancellationToken ct = default)
    {
        string[] flags = mode switch
        {
            GitPullMode.Merge => ["--no-rebase"],
            GitPullMode.Rebase => ["--rebase"],
            GitPullMode.FastForwardOnly => ["--ff-only"],
            _ => [],
        };
        var result = await WriteAsync(["pull", .. flags], null, ct).ConfigureAwait(false);
        if (!result.Ok && mode == GitPullMode.Configured && result.Error.Contains("reconcile divergent branches", StringComparison.Ordinal))
        {
            result = await WriteAsync(["pull", "--no-rebase"], null, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>Push. A branch with no upstream is pushed to its remote and set to track it.</summary>
    public async Task<GitResult> PushAsync(bool forceWithLease = false, CancellationToken ct = default)
    {
        var status = await StatusAsync(ct).ConfigureAwait(false);
        if (status.Branch.IsDetached) return new GitResult(1, "", "HEAD is not on a branch, so there is nothing to push. Create a branch first.");
        List<string> args = ["push"];
        if (forceWithLease) args.Add("--force-with-lease");
        if (!status.Branch.HasUpstream)
        {
            var remote = await PushRemoteAsync(status.Branch.Head!, ct).ConfigureAwait(false);
            if (remote is null) return new GitResult(1, "", "This repository has no remote to push to. Add one with: git remote add origin <url>");
            args.AddRange(["--set-upstream", remote, status.Branch.Head!]);
        }

        return await WriteAsync(args, null, ct).ConfigureAwait(false);
    }

    /// <summary>The remote a new branch goes to: the branch's own, the user's default, "origin", or the only remote.</summary>
    public async Task<string?> PushRemoteAsync(string branch, CancellationToken ct = default)
    {
        foreach (var key in new[] { $"branch.{branch}.pushRemote", "remote.pushDefault", $"branch.{branch}.remote" })
        {
            if (await ConfigAsync(key, ct).ConfigureAwait(false) is { Length: > 0 } configured) return configured;
        }

        var remotes = await RemotesAsync(ct).ConfigureAwait(false);
        if (remotes.Any(r => r.Name == "origin")) return "origin";
        return remotes.Length == 1 ? remotes[0].Name : null;
    }

    // ── Stash ────────────────────────────────────────────────────────────────────────────────────

    public async Task<ImmutableArray<GitStash>> StashesAsync(CancellationToken ct = default)
    {
        var output = await ReadAsync(["stash", "list", "--format=%gd%x1f%gs%x1f%ct%x1e"], null, ct).ConfigureAwait(false);
        var list = ImmutableArray.CreateBuilder<GitStash>();
        var index = 0;
        foreach (var record in output.Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.TrimStart('\n').Split('\u001f');
            if (f.Length < 3) continue;
            var subject = f[1];
            var branch = "";
            var message = subject;
            var colon = subject.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0 && (subject.StartsWith("On ", StringComparison.Ordinal) || subject.StartsWith("WIP on ", StringComparison.Ordinal)))
            {
                branch = subject[..colon].Replace("WIP on ", "", StringComparison.Ordinal).Replace("On ", "", StringComparison.Ordinal);
                message = subject.StartsWith("On ", StringComparison.Ordinal) ? subject[(colon + 2)..] : subject;
            }

            list.Add(new GitStash(index++, f[0], message, branch, UnixTime(f[2])));
        }

        return list.ToImmutable();
    }

    public Task<GitResult> StashPushAsync(string? message = null, bool includeUntracked = true, CancellationToken ct = default)
    {
        List<string> args = ["stash", "push"];
        if (includeUntracked) args.Add("--include-untracked");
        if (!string.IsNullOrWhiteSpace(message)) args.AddRange(["-m", message.Trim()]);
        return WriteAsync(args, null, ct);
    }

    public Task<GitResult> StashApplyAsync(string stashRef, CancellationToken ct = default) => WriteAsync(["stash", "apply", stashRef], null, ct);

    public Task<GitResult> StashPopAsync(string stashRef, CancellationToken ct = default) => WriteAsync(["stash", "pop", stashRef], null, ct);

    public Task<GitResult> StashDropAsync(string stashRef, CancellationToken ct = default) => WriteAsync(["stash", "drop", stashRef], null, ct);

    // ── Work trees ───────────────────────────────────────────────────────────────────────────────

    public async Task<ImmutableArray<GitWorktree>> WorktreesAsync(CancellationToken ct = default)
    {
        var output = await ReadAsync(["worktree", "list", "--porcelain", "-z"], null, ct).ConfigureAwait(false);
        var list = ImmutableArray.CreateBuilder<GitWorktree>();
        string? path = null, head = null, branch = null;
        bool bare = false, locked = false;
        void Flush()
        {
            if (path is not null) list.Add(new GitWorktree(path, head, branch, bare, locked));
            path = head = branch = null;
            bare = locked = false;
        }

        foreach (var field in output.Split('\0'))
        {
            if (field.Length == 0) Flush();
            else if (field.StartsWith("worktree ", StringComparison.Ordinal)) path = field[9..];
            else if (field.StartsWith("HEAD ", StringComparison.Ordinal)) head = field[5..];
            else if (field.StartsWith("branch ", StringComparison.Ordinal)) branch = field[7..].Replace("refs/heads/", "", StringComparison.Ordinal);
            else if (field == "bare") bare = true;
            else if (field.StartsWith("locked", StringComparison.Ordinal)) locked = true;
        }

        Flush();
        return list.ToImmutable();
    }
}
