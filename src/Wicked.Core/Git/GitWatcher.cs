namespace Wicked.Core.Git;

/// <summary>
/// Tells a repository's owner that its state may have changed: the index, HEAD, a ref, or a file in the work tree. Events are
/// gathered for a short while and raised once, on a pool thread, so a build writing a thousand files asks for one refresh.
/// The git directory is watched here; changes in the work tree arrive through <see cref="Notify"/> (the workbench already
/// watches the folder, and one inotify watch per folder is enough), or, for a repository with no one to feed it, from
/// a watcher of its own when <c>watchWorkTree</c> is set.
/// </summary>
public sealed class GitWatcher : IDisposable
{
    private static readonly HashSet<string> GitDirNames = new(StringComparer.Ordinal)
    {
        "index", "HEAD", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "REBASE_HEAD", "BISECT_LOG", "packed-refs", "rebase-merge", "rebase-apply", "refs", "config",
    };

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private readonly TimeSpan _debounce;
    private readonly string _root;
    private bool _disposed;

    public GitWatcher(GitRepository repository, TimeSpan? debounce = null, bool watchWorkTree = false)
    {
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        _root = repository.Root;
        _timer = new Timer(_ => Raise(), null, Timeout.Infinite, Timeout.Infinite);
        Watch(repository.GitDir, recursive: false, name => GitDirNames.Contains(name));
        Watch(Path.Combine(repository.CommonDir, "refs"), recursive: true, _ => true);
        if (repository.CommonDir != repository.GitDir) Watch(repository.CommonDir, recursive: false, name => name is "packed-refs" or "config");
        if (watchWorkTree) Watch(repository.Root, recursive: true, _ => true, workTree: true);
    }

    /// <summary>Raised once after a burst of changes settles.</summary>
    public event Action? Changed;

    /// <summary>A file or folder in the work tree changed (paths inside <c>.git</c> are ignored: the git directory is watched separately).</summary>
    public void Notify(string path)
    {
        if (path.StartsWith(_root, StringComparison.Ordinal) && IsInGitDir(path)) return;
        Schedule();
    }

    private bool IsInGitDir(string path)
    {
        var relative = path.Length > _root.Length ? path[(_root.Length + 1)..] : "";
        return relative == ".git" || relative.StartsWith(".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private void Watch(string folder, bool recursive, Func<string, bool> interesting, bool workTree = false)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            void OnEvent(string? full, string? name)
            {
                if (name is null || full is null) return;
                if (workTree ? IsInGitDir(full) : !interesting(name.Split(Path.DirectorySeparatorChar)[0]) || name.EndsWith(".lock", StringComparison.Ordinal)) return;
                Schedule();
            }

            watcher.Created += (_, e) => OnEvent(e.FullPath, e.Name);
            watcher.Changed += (_, e) => OnEvent(e.FullPath, e.Name);
            watcher.Deleted += (_, e) => OnEvent(e.FullPath, e.Name);
            watcher.Renamed += (_, e) => OnEvent(e.FullPath, e.Name);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Warn($"Cannot watch {folder} for git changes: {ex.Message}");
        }
    }

    private void Schedule()
    {
        if (_disposed) return;
        try
        {
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Raise()
    {
        if (!_disposed) Changed?.Invoke();
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}
