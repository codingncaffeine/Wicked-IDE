using System.Collections.Immutable;
using System.Globalization;

namespace Wicked.Core.Git;

/// <summary>One stretch of lines of a file last changed by one commit (StartLine is 0-based, in the file as blamed).</summary>
public sealed record BlameHunk(int StartLine, int LineCount, string Commit, string Author, string AuthorEmail, DateTimeOffset Date, string Summary)
{
    /// <summary>The lines are not in any commit yet (edited, unsaved or unstaged).</summary>
    public bool IsUncommitted => Commit.All(c => c == '0');

    public string ShortCommit => Commit.Length > 7 ? Commit[..7] : Commit;
}

public sealed partial class GitRepository
{
    private const string LogFormat = "--format=%H%x1f%P%x1f%an%x1f%ae%x1f%at%x1f%D%x1f%s";

    /// <summary>
    /// A page of the log, newest first, every branch and HEAD together (the graph's source). The page is
    /// <paramref name="count"/> commits from <paramref name="skip"/>; git's date order keeps a parent after all its children.
    /// </summary>
    public async Task<ImmutableArray<GitCommit>> LogAsync(int skip, int count, IReadOnlyList<string>? revisions = null, CancellationToken ct = default)
    {
        List<string> args = ["log", "--date-order", "--decorate=full", "-z", LogFormat, $"--skip={skip}", $"-n{count}"];
        if (revisions is { Count: > 0 })
        {
            args.AddRange(revisions);
        }
        else
        {
            args.Add("--all");
            // --all leaves out a detached HEAD.
            if (await IsDetachedAsync(ct).ConfigureAwait(false)) args.Add("HEAD");
        }

        args.Add("--");
        var result = await TryReadAsync(args, null, ct).ConfigureAwait(false);
        if (!result.Ok)
        {
            // A repository with no commit has no log.
            if (result.Error.Contains("does not have any commits", StringComparison.Ordinal) || result.Error.Contains("bad default revision", StringComparison.Ordinal)) return [];
            throw new GitException(Describe(result, "git log failed"), result);
        }

        return ParseLog(result.Output);
    }

    private async Task<bool> IsDetachedAsync(CancellationToken ct)
    {
        var result = await TryReadAsync(["symbolic-ref", "-q", "HEAD"], null, ct).ConfigureAwait(false);
        return !result.Ok && (await TryReadAsync(["rev-parse", "--verify", "-q", "HEAD"], null, ct).ConfigureAwait(false)).Ok;
    }

    /// <summary>The history of one file across renames, newest first (the Timeline).</summary>
    public async Task<ImmutableArray<GitCommit>> FileHistoryAsync(string path, int skip, int count, CancellationToken ct = default)
    {
        var result = await TryReadAsync(["--literal-pathspecs", "log", "--follow", "--decorate=full", "-z", LogFormat, $"--skip={skip}", $"-n{count}", "--", path], null, ct).ConfigureAwait(false);
        if (!result.Ok) return [];
        return ParseLog(result.Output);
    }

    public static ImmutableArray<GitCommit> ParseLog(string output)
    {
        var list = ImmutableArray.CreateBuilder<GitCommit>();
        foreach (var record in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = record.TrimStart('\n').Split('\u001f');
            if (f.Length < 7) continue;
            var parents = f[1].Length == 0 ? [] : f[1].Split(' ').ToImmutableArray();
            list.Add(new GitCommit(f[0], parents, f[2], f[3], UnixTime(f[4]), f[6], ParseRefs(f[5])));
        }

        return list.ToImmutable();
    }

    /// <summary>"HEAD -&gt; refs/heads/main, refs/remotes/origin/main, tag: refs/tags/v1" as refs.</summary>
    internal static ImmutableArray<GitRef> ParseRefs(string decorations)
    {
        if (decorations.Length == 0) return [];
        var list = ImmutableArray.CreateBuilder<GitRef>();
        foreach (var raw in decorations.Split(", ", StringSplitOptions.RemoveEmptyEntries))
        {
            var d = raw.Trim();
            if (d.StartsWith("HEAD -> ", StringComparison.Ordinal))
            {
                list.Add(new GitRef("HEAD", GitRefKind.Head));
                d = d["HEAD -> ".Length..];
            }

            if (d == "HEAD") list.Add(new GitRef("HEAD", GitRefKind.Head));
            else if (d.StartsWith("tag: refs/tags/", StringComparison.Ordinal)) list.Add(new GitRef(d["tag: refs/tags/".Length..], GitRefKind.Tag));
            else if (d.StartsWith("refs/heads/", StringComparison.Ordinal)) list.Add(new GitRef(d["refs/heads/".Length..], GitRefKind.LocalBranch));
            else if (d.StartsWith("refs/remotes/", StringComparison.Ordinal))
            {
                var name = d["refs/remotes/".Length..];
                if (!name.EndsWith("/HEAD", StringComparison.Ordinal)) list.Add(new GitRef(name, GitRefKind.RemoteBranch)); // not a path: a ref name
            }
        }

        return list.ToImmutable();
    }

    /// <summary>The files a commit changed (against its first parent), with renames detected.</summary>
    public async Task<ImmutableArray<GitCommitFile>> CommitFilesAsync(string hash, CancellationToken ct = default)
    {
        var hasParent = (await TryReadAsync(["rev-parse", "--verify", "-q", hash + "^"], null, ct).ConfigureAwait(false)).Ok;
        var output = hasParent
            ? await ReadAsync(["diff", "--name-status", "-z", "-M", hash + "^1", hash], null, ct).ConfigureAwait(false)
            : await ReadAsync(["diff-tree", "--root", "-r", "--no-commit-id", "--name-status", "-z", "-M", hash], null, ct).ConfigureAwait(false);
        return ParseNameStatus(output);
    }

    public static ImmutableArray<GitCommitFile> ParseNameStatus(string output)
    {
        var fields = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var list = ImmutableArray.CreateBuilder<GitCommitFile>();
        for (var i = 0; i < fields.Length; i++)
        {
            var code = fields[i].Trim();
            if (code.Length == 0) continue;
            var kind = code[0] switch
            {
                'M' => GitChangeKind.Modified,
                'A' => GitChangeKind.Added,
                'D' => GitChangeKind.Deleted,
                'R' => GitChangeKind.Renamed,
                'C' => GitChangeKind.Copied,
                'T' => GitChangeKind.TypeChanged,
                _ => GitChangeKind.Modified,
            };
            if (code[0] is 'R' or 'C' && i + 2 < fields.Length)
            {
                list.Add(new GitCommitFile(fields[i + 2], fields[i + 1], kind));
                i += 2;
            }
            else if (i + 1 < fields.Length)
            {
                list.Add(new GitCommitFile(fields[i + 1], null, kind));
                i++;
            }
        }

        return list.ToImmutable();
    }

    /// <summary>A file as a revision holds it ("HEAD", a commit, or ":" for the index), or null when it is not there.</summary>
    public async Task<string?> ShowAsync(string revision, string path, CancellationToken ct = default)
    {
        var result = await TryReadAsync(["show", $"{revision}:{path}"], null, ct).ConfigureAwait(false);
        return result.Ok ? result.Output : null;
    }

    /// <summary>The file as the index holds it, or null when it is not staged.</summary>
    public Task<string?> ShowIndexAsync(string path, CancellationToken ct = default) => ShowAsync("", path, ct);

    /// <summary>The unified diff of one file: the work tree against the index, or (staged) the index against HEAD.</summary>
    public async Task<string> DiffAsync(string path, bool staged, int context = 3, CancellationToken ct = default)
    {
        List<string> args = ["--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", $"-U{context}"];
        if (staged) args.Add("--cached");
        args.AddRange(["--", path]);
        return await ReadAsync(args, null, ct).ConfigureAwait(false);
    }

    /// <summary>The unified diff one commit made to one file (against its first parent).</summary>
    public async Task<string> CommitDiffAsync(string hash, string path, string? originalPath = null, int context = 3, CancellationToken ct = default)
    {
        var hasParent = (await TryReadAsync(["rev-parse", "--verify", "-q", hash + "^"], null, ct).ConfigureAwait(false)).Ok;
        List<string> args = hasParent
            ? ["--literal-pathspecs", "diff", "--no-color", "--no-ext-diff", "-M", $"-U{context}", hash + "^1", hash, "--", path]
            : ["--literal-pathspecs", "show", "--no-color", "--no-ext-diff", "--format=", $"-U{context}", hash, "--", path];
        if (hasParent && originalPath is not null) args.Add(originalPath);
        return await ReadAsync(args, null, ct).ConfigureAwait(false);
    }

    // ── Blame ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Blames a file as git finds each stretch (<c>git blame --incremental</c>): <paramref name="onHunk"/> is called as hunks
    /// arrive, in no line order. <paramref name="contents"/> blames unsaved text instead of the file on disk, so an edit shows
    /// as "not committed yet". Returns false when git cannot blame the file (untracked, no commits).
    /// </summary>
    public async Task<bool> BlameAsync(string path, string? contents, Action<BlameHunk> onHunk, CancellationToken ct = default)
    {
        var parser = new BlameParser(onHunk);
        List<string> args = ["--literal-pathspecs", "blame", "--incremental"];
        if (contents is not null) args.AddRange(["--contents", "-"]);
        args.AddRange(["--", path]);
        var result = await GitProcess.RunAsync(Environment, Root, args, contents, parser.Feed, ct).ConfigureAwait(false);
        parser.Finish();
        return result.Ok;
    }

    /// <summary>Reads the incremental blame format: a header line for each stretch, then facts about the commit the first time it appears.</summary>
    internal sealed class BlameParser(Action<BlameHunk> onHunk)
    {
        private readonly Dictionary<string, (string Author, string Email, DateTimeOffset Date, string Summary)> _commits = [];
        private string _commit = "";
        private int _start;
        private int _count;
        private bool _open;
        private string _author = "", _email = "", _summary = "";
        private long _time;
        private TimeSpan _zone;

        public void Feed(string line)
        {
            if (!_open)
            {
                var parts = line.Split(' ');
                if (parts.Length == 4 && parts[0].Length >= 40 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var final)
                    && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
                {
                    _commit = parts[0];
                    _start = final - 1;
                    _count = count;
                    _open = true;
                    _author = _email = _summary = "";
                    _time = 0;
                    _zone = TimeSpan.Zero;
                }

                return;
            }

            var space = line.IndexOf(' ');
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? "" : line[(space + 1)..];
            switch (key)
            {
                case "author": _author = value; break;
                case "author-mail": _email = value.Trim('<', '>'); break;
                case "author-time": _ = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _time); break;
                case "author-tz": _zone = ParseZone(value); break;
                case "summary": _summary = value; break;
                case "filename": Emit(); break;
            }
        }

        public void Finish() => _open = false;

        private void Emit()
        {
            if (_author.Length > 0 || _time != 0) _commits[_commit] = (_author, _email, DateTimeOffset.FromUnixTimeSeconds(_time).ToOffset(_zone), _summary);
            if (_commits.TryGetValue(_commit, out var c)) onHunk(new BlameHunk(_start, _count, _commit, c.Author, c.Email, c.Date, c.Summary));
            _open = false;
        }

        private static TimeSpan ParseZone(string tz)
        {
            if (tz.Length != 5 || !int.TryParse(tz.AsSpan(1, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
                || !int.TryParse(tz.AsSpan(3, 2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)) return TimeSpan.Zero;
            var span = new TimeSpan(h, m, 0);
            return tz[0] == '-' ? -span : span;
        }
    }
}

/// <summary>The line the status bar shows for the caret's line: "You, 3 days ago • the commit's subject".</summary>
public static class BlameText
{
    public static string Format(BlameHunk hunk, string? currentUserEmail, DateTimeOffset now)
    {
        if (hunk.IsUncommitted) return "You, uncommitted changes";
        var who = currentUserEmail is not null && string.Equals(hunk.AuthorEmail, currentUserEmail, StringComparison.OrdinalIgnoreCase) ? "You" : hunk.Author;
        return $"{who}, {Ago(now - hunk.Date)} • {hunk.Summary}";
    }

    /// <summary>"3 days ago", "just now", "2 years ago".</summary>
    public static string Ago(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        static string Unit(long n, string name) => $"{n} {name}{(n == 1 ? "" : "s")} ago";
        if (age.TotalSeconds < 45) return "just now";
        if (age.TotalMinutes < 45) return Unit(Math.Max(1, (long)Math.Round(age.TotalMinutes)), "minute");
        if (age.TotalHours < 22) return Unit(Math.Max(1, (long)Math.Round(age.TotalHours)), "hour");
        if (age.TotalDays < 26) return Unit(Math.Max(1, (long)Math.Round(age.TotalDays)), "day");
        if (age.TotalDays < 320) return Unit(Math.Max(1, (long)Math.Round(age.TotalDays / 30)), "month");
        return Unit(Math.Max(1, (long)Math.Round(age.TotalDays / 365)), "year");
    }
}
