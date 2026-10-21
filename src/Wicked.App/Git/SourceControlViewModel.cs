using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.Core;
using Wicked.Core.Git;
using Bench = Wicked.App.Workbench.Workbench;

namespace Wicked.App.Git;

/// <summary>A repository the open folder holds, as the panel's repository list shows it.</summary>
public sealed partial class RepositoryChoice(GitRepository repository) : ObservableObject
{
    public GitRepository Repository { get; } = repository;

    public string Name => Repository.Name;

    public string Root => Repository.Root;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private string _branch = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _changes;

    public string Label => $"{Name}  •  {Branch}{(Changes > 0 ? $"  ({Changes})" : "")}";
}

/// <summary>
/// The Source Control panel's state, VS Code's shape: the repositories of the open folder, the groups of changes, the commit
/// box, the branch and its sync state, the stash list, the commit graph and the file's history. It lives on the UI thread;
/// every git call, parse and layout runs on a worker and posts its result back, one refresh at a time per repository.
/// Refreshes come from the file watcher (the git folder through <see cref="GitWatcher"/>, the work tree through the workbench's
/// own watcher) and from the panel's own actions, never from polling.
/// </summary>
public sealed partial class SourceControlViewModel : ObservableObject, IDisposable
{
    private const int GraphPage = 200;

    private sealed class Slot(GitRepository repository, RepositoryChoice choice) : IDisposable
    {
        public GitRepository Repository { get; } = repository;

        public RepositoryChoice Choice { get; } = choice;

        public GitWatcher? Watcher { get; set; }

        public GitStatus Status { get; set; } = GitStatus.Empty;

        public bool Running { get; set; }

        public bool Again { get; set; }

        public void Dispose() => Watcher?.Dispose();
    }

    private readonly ViewModels.ShellViewModel _shell;
    private readonly WickedPaths _paths;
    private readonly NoticeBoard _notices;
    private readonly Bench _bench;
    private readonly Action<Action> _post;
    private readonly List<Slot> _slots = [];
    private readonly HashSet<ScmGroup> _collapsedGroups = [];
    private readonly HashSet<string> _collapsedFolders = [];
    private readonly CancellationTokenSource _stop = new();
    private Slot[] _slotSnapshot = [];
    private GitEnvironment _baseEnvironment;
    private GitEnvironment? _environment;
    private AskpassRelay? _relay;
    private int _generation;
    private string? _userEmail;

    public SourceControlViewModel(ViewModels.ShellViewModel shell, WickedPaths paths, NoticeBoard notices, Action<Action> post)
    {
        _shell = shell;
        _paths = paths;
        _notices = notices;
        _bench = shell.Workbench;
        _post = post;
        Output = new GitOutputLog(post);
        _baseEnvironment = new GitEnvironment();
        Output.Follow(_baseEnvironment.Log);
        _bench.FolderOpened += folder => _ = OpenFolderAsync(folder);
        _bench.PathChanged += OnPathChanged;
        _bench.ActiveEditorChanged += OnActiveEditorChanged;
        Commands = BuildCommands();
    }

    /// <summary>Every git command and what it said; the shell mounts it in the bottom panel's Output tab.</summary>
    public GitOutputLog Output { get; }

    /// <summary>
    /// How git is run (program, extra variables, command log). The default is plain <c>git</c> in the app's own environment;
    /// a test sets a sandbox here before opening a folder. The relay for credentials is added on top of it.
    /// </summary>
    public GitEnvironment Environment
    {
        get => _baseEnvironment;
        set
        {
            Output.Stop(_baseEnvironment.Log);
            _baseEnvironment = value;
            Output.Follow(value.Log);
            _environment = null;
        }
    }

    public ObservableCollection<RepositoryChoice> Repositories { get; } = [];

    [ObservableProperty]
    private RepositoryChoice? _activeRepository;

    /// <summary>The open folder (null with none).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNoFolder), nameof(ShowsInit))]
    private string? _folder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsInit), nameof(ShowsRepository), nameof(ShowsRepositoryPicker))]
    private bool _hasRepository;

    [ObservableProperty]
    private bool _looking;

    /// <summary>Why there is nothing to show, when git is missing or the folder is not a repository.</summary>
    [ObservableProperty]
    private string _emptyText = "";

    public bool ShowsNoFolder => Folder is null;

    public bool ShowsInit => Folder is not null && !HasRepository && !Looking;

    public bool ShowsRepository => HasRepository;

    public bool ShowsRepositoryPicker => Repositories.Count > 1;

    /// <summary>The credential question git is waiting on, shown over the window.</summary>
    [ObservableProperty]
    private GitPromptViewModel? _prompt;

    public GitRepository? ActiveGit => _slots.FirstOrDefault(s => s.Choice == ActiveRepository)?.Repository;

    private Slot? ActiveSlot => _slots.FirstOrDefault(s => s.Choice == ActiveRepository);

    /// <summary>Raised on the UI thread after a repository's state was read again (the editor refreshes its gutter marks from it).</summary>
    public event Action<GitRepository>? RepositoryChanged;

    // ── Opening a folder ─────────────────────────────────────────────────────────────────────────

    private GitEnvironment ComposeEnvironment()
    {
        if (_environment is not null) return _environment;
        var variables = new Dictionary<string, string?>(_baseEnvironment.Variables);
        _relay ??= AskpassRelay.Create(Path.Combine(_paths.Runtime, "git"));
        if (_relay is { } relay)
        {
            relay.Handler = AskAsync;
            foreach (var (key, value) in relay.Variables) variables[key] = value;
        }

        return _environment = new GitEnvironment { Executable = _baseEnvironment.Executable, Timeout = _baseEnvironment.Timeout, Log = _baseEnvironment.Log, Variables = variables };
    }

    /// <summary>Finds the repositories of a folder on a worker and shows the first one's changes.</summary>
    public async Task OpenFolderAsync(string folder)
    {
        var generation = Interlocked.Increment(ref _generation);
        CloseSlots();
        Folder = folder;
        Looking = true;
        EmptyText = "";
        IReadOnlyList<GitRepository> repositories;
        try
        {
            repositories = await GitDiscovery.FindAsync(folder, ComposeEnvironment(), _stop.Token);
        }
        catch (GitException ex)
        {
            if (generation != _generation) return;
            Looking = false;
            HasRepository = false;
            EmptyText = $"Git could not run: {ex.Message}. Install git to use Source Control.";
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation) return;
        Looking = false;
        foreach (var repository in repositories)
        {
            var choice = new RepositoryChoice(repository);
            var slot = new Slot(repository, choice);
            slot.Watcher = new GitWatcher(repository);
            slot.Watcher.Changed += () => _post(() => _ = RefreshSlotAsync(slot));
            _slots.Add(slot);
            Repositories.Add(choice);
        }

        _slotSnapshot = [.. _slots];
        OnPropertyChanged(nameof(ShowsRepositoryPicker));
        HasRepository = _slots.Count > 0;
        if (!HasRepository)
        {
            EmptyText = "The folder you opened is not a Git repository.";
            return;
        }

        // The repository the folder is in (or the first inside it) comes first.
        ActiveRepository = Repositories[0];
        var first = _slots[0].Repository;
        _userEmail = await Task.Run(() => first.ConfigAsync("user.email"), _stop.Token);
        foreach (var slot in _slots.ToList()) await RefreshSlotAsync(slot);
    }

    private void CloseSlots()
    {
        foreach (var slot in _slots) slot.Dispose();
        _slots.Clear();
        _slotSnapshot = [];
        Repositories.Clear();
        ActiveRepository = null;
        HasRepository = false;
        Rows = [];
        Stashes = [];
        GraphRows = [];
        HistoryRows = [];
        SelectedCommit = null;
        CommitFiles = [];
        Status = GitStatus.Empty;
        BranchName = "";
        BlameStatus = "";
        ErrorText = "";
        OnPropertyChanged(nameof(ShowsRepositoryPicker));
    }

    partial void OnActiveRepositoryChanged(RepositoryChoice? oldValue, RepositoryChoice? newValue)
    {
        // Each repository keeps its own commit message, as VS Code does.
        if (oldValue is not null) _messages[oldValue.Root] = CommitMessage;
        CommitMessage = newValue is not null && _messages.TryGetValue(newValue.Root, out var kept) ? kept : "";
        Rows = [];
        Stashes = [];
        GraphRows = [];
        if (ActiveSlot is { } slot) _ = RefreshSlotAsync(slot);
    }

    private readonly Dictionary<string, string> _messages = [];

    private void OnPathChanged(string path)
    {
        foreach (var slot in _slotSnapshot)
        {
            if (path.StartsWith(slot.Repository.Root, StringComparison.Ordinal)) slot.Watcher?.Notify(path);
        }
    }

    // ── Refresh ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a repository's status (and stashes) on a worker and shows it. One read runs at a time per repository: a request
    /// that arrives meanwhile makes it run once more when it ends, so a burst of changes is one more read, not many.
    /// </summary>
    private async Task RefreshSlotAsync(Slot slot)
    {
        if (!_slots.Contains(slot)) return;
        if (slot.Running)
        {
            slot.Again = true;
            return;
        }

        slot.Running = true;
        try
        {
            do
            {
                slot.Again = false;
                await RefreshOnceAsync(slot);
            }
            while (slot.Again && _slots.Contains(slot));
        }
        finally
        {
            slot.Running = false;
        }
    }

    /// <summary>Reads the active repository again (the panel's Refresh button, and after every action).</summary>
    [RelayCommand]
    public Task RefreshAsync() => ActiveSlot is { } slot ? RefreshSlotAsync(slot) : Task.CompletedTask;

    private async Task RefreshOnceAsync(Slot slot)
    {
        var generation = _generation;
        var withStashes = slot.Choice == ActiveRepository;
        GitStatus status;
        System.Collections.Immutable.ImmutableArray<GitStash> stashes;
        try
        {
            (status, stashes) = await Task.Run(async () =>
            {
                var s = await slot.Repository.StatusAsync(_stop.Token).ConfigureAwait(false);
                var st = withStashes ? await slot.Repository.StashesAsync(_stop.Token).ConfigureAwait(false) : [];
                return (s, st);
            }, _stop.Token);
        }
        catch (GitException ex)
        {
            // The folder went away or git failed: say so in the log (it already has) and leave the last state up.
            Log.Warn($"Git status failed in {slot.Repository.Root}: {ex.Message}");
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _generation || !_slots.Contains(slot)) return;
        var previous = slot.Status;
        slot.Status = status;
        slot.Choice.Branch = BranchLabel(status.Branch);
        slot.Choice.Changes = status.Changes.Length;
        if (slot.Choice != ActiveRepository) return;

        var rows = await Task.Run(() => ScmRowBuilder.Build(status, TreeMode, _collapsedGroups, _collapsedFolders), _stop.Token);
        if (generation != _generation || slot.Choice != ActiveRepository) return;
        ApplyStatus(status, slot.Repository, rows);
        Stashes = [.. stashes];
        RepositoryChanged?.Invoke(slot.Repository);
        var moved = previous.Branch.Oid != status.Branch.Oid || previous.Branch.Head != status.Branch.Head;
        if (GraphExpanded && (moved || GraphRows.Length == 0 || _graphStale)) _ = LoadGraphAsync(reset: true);
        _graphStale = false;
        if (HistoryExpanded && HistoryFile is not null) _ = LoadHistoryAsync(HistoryFile);
        _blameVersion = -1;
        UpdateBlameSoon(immediately: true);
    }

    private static string BranchLabel(GitBranchInfo branch) =>
        branch.IsDetached ? $"HEAD ({branch.Oid?[..Math.Min(7, branch.Oid.Length)] ?? "no commits"})" : branch.Head ?? "";

    [ObservableProperty]
    private GitStatus _status = GitStatus.Empty;

    [ObservableProperty]
    private ScmRow[] _rows = [];

    [ObservableProperty]
    private string _branchName = "";

    [ObservableProperty]
    private string _syncText = "";

    [ObservableProperty]
    private string _syncLabel = "";

    [ObservableProperty]
    private bool _canSync;

    [ObservableProperty]
    private string _operationText = "";

    [ObservableProperty]
    private int _changeCount;

    [ObservableProperty]
    private bool _hasChanges;

    [ObservableProperty]
    private bool _treeMode;

    /// <summary>Whether the staged, unstaged or conflicted lists are empty: the panel then says the tree is clean.</summary>
    [ObservableProperty]
    private bool _isClean;

    partial void OnTreeModeChanged(bool value) => _ = RebuildRowsAsync();

    private void ApplyStatus(GitStatus status, GitRepository repository, ScmRow[] rows)
    {
        _ = repository;
        Status = status;
        Rows = rows;
        BranchName = BranchLabel(status.Branch);
        ChangeCount = status.Changes.Length;
        HasChanges = status.Changes.Length > 0;
        IsClean = status.IsClean;
        var b = status.Branch;
        SyncText = b.HasUpstream ? $"{(b.Behind > 0 ? $"↓{b.Behind} " : "")}{(b.Ahead > 0 ? $"↑{b.Ahead}" : "")}".Trim() : "";
        CanSync = !b.IsDetached && !b.IsUnborn && (!b.HasUpstream || b.Ahead > 0 || b.Behind > 0);
        SyncLabel = !b.HasUpstream ? "Publish Branch" : $"Sync Changes {SyncText}".Trim();
        OperationText = status.Operation switch
        {
            GitOperation.Merge => "Merge in progress: resolve the conflicts, stage them, then commit.",
            GitOperation.Rebase => "Rebase in progress: resolve the conflicts, stage them, then continue.",
            GitOperation.CherryPick => "Cherry-pick in progress: resolve the conflicts, stage them, then continue.",
            GitOperation.Revert => "Revert in progress: resolve the conflicts, stage them, then continue.",
            GitOperation.Bisect => "Bisect in progress.",
            _ => "",
        };
        OnPropertyChanged(nameof(CommitPlaceholder));
        OnPropertyChanged(nameof(CanCommit));
        OnPropertyChanged(nameof(HasOperation));
        OnPropertyChanged(nameof(ShowsSync));
    }

    public bool HasOperation => OperationText.Length > 0;

    public bool ShowsSync => CanSync;

    /// <summary>Rebuilds the rows from the last status (tree/list toggled, a group or folder folded).</summary>
    public async Task RebuildRowsAsync()
    {
        var status = Status;
        var tree = TreeMode;
        var rows = await Task.Run(() => ScmRowBuilder.Build(status, tree, _collapsedGroups, _collapsedFolders), _stop.Token);
        if (ReferenceEquals(status, Status)) Rows = rows;
    }

    /// <summary>Folds or opens a group or folder row.</summary>
    public void ToggleRow(ScmRow row)
    {
        switch (row)
        {
            case ScmGroupRow g:
                if (!_collapsedGroups.Remove(g.Group)) _collapsedGroups.Add(g.Group);
                break;
            case ScmFolderRow f:
                var key = $"{f.Group}:{f.Path}";
                if (!_collapsedFolders.Remove(key)) _collapsedFolders.Add(key);
                break;
            default:
                return;
        }

        _ = RebuildRowsAsync();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _bench.PathChanged -= OnPathChanged;
        _bench.ActiveEditorChanged -= OnActiveEditorChanged;
        CloseSlots();
        _relay?.Dispose();
        _blameDebounce?.Cancel();
        _historyCts?.Cancel();
        _stop.Dispose();
    }
}
