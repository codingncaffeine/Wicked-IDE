using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Git;

namespace Wicked.App.Git;

/// <summary>A name on a commit row: a branch, a remote branch, a tag or HEAD.</summary>
public sealed record GraphRefChip(string Name, GitRefKind Kind)
{
    public bool IsHead => Kind == GitRefKind.Head;

    public bool IsRemote => Kind == GitRefKind.RemoteBranch;

    public bool IsTag => Kind == GitRefKind.Tag;
}

/// <summary>A commit as a list row: used by the graph (with lanes) and by the file's history (without).</summary>
public class CommitRowVm(GitCommit commit)
{
    public GitCommit Commit { get; } = commit;

    public string Subject => Commit.Subject;

    public string Author => Commit.Author;

    public string ShortHash => Commit.ShortHash;

    public string DateText => BlameText.Ago(DateTimeOffset.Now - Commit.Date);

    public string Tooltip => $"{Commit.Subject}\n{Commit.Author} <{Commit.AuthorEmail}>\n{Commit.Date:yyyy-MM-dd HH:mm}\n{Commit.Hash}";

    public IReadOnlyList<GraphRefChip> Refs { get; } = [.. commit.Refs.Select(r => new GraphRefChip(r.Name, r.Kind))];

    public bool HasRefs => Refs.Count > 0;
}

/// <summary>A commit row with the lanes drawn beside it.</summary>
public sealed class GraphRowVm(GraphRow row) : CommitRowVm(row.Commit)
{
    public GraphRow Row { get; } = row;
}

/// <summary>A file a selected commit changed.</summary>
public sealed class CommitFileRow
{
    public CommitFileRow(GitCommitFile file)
    {
        File = file;
        var slash = file.Path.LastIndexOf('/'); // not a path: a repository path with '/'
        Name = slash < 0 ? file.Path : file.Path[(slash + 1)..];
        Directory = slash < 0 ? "" : file.Path[..slash];
        (Letter, Tone) = file.Kind switch
        {
            GitChangeKind.Added => ("A", "added"),
            GitChangeKind.Deleted => ("D", "deleted"),
            GitChangeKind.Renamed => ("R", "renamed"),
            GitChangeKind.Copied => ("C", "renamed"),
            _ => ("M", "modified"),
        };
    }

    public GitCommitFile File { get; }

    public string Name { get; }

    public string Directory { get; }

    public string Letter { get; }

    public string Tone { get; }

    public string Tooltip => File.OriginalPath is null ? File.Path : $"{File.OriginalPath} -> {File.Path}";
}

public sealed partial class SourceControlViewModel
{
    // ── The graph ────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _graphExpanded;

    [ObservableProperty]
    private GraphRowVm[] _graphRows = [];

    /// <summary>Width, in lanes, of the widest row loaded (the text column starts after it).</summary>
    [ObservableProperty]
    private int _graphLanes = 1;

    [ObservableProperty]
    private bool _graphHasMore;

    [ObservableProperty]
    private GitCommit? _selectedCommit;

    [ObservableProperty]
    private CommitFileRow[] _commitFiles = [];

    private LogGraphBuilder _graph = new();
    private bool _graphLoading;
    private bool _graphAgain;
    private bool _graphStale;

    partial void OnGraphExpandedChanged(bool value)
    {
        if (value) _ = LoadGraphAsync(reset: true);
    }

    /// <summary>Reads a page of the log and lays its lanes out on a worker; <paramref name="reset"/> starts over from the newest commit.</summary>
    public async Task LoadGraphAsync(bool reset)
    {
        if (ActiveGit is not { } repo) return;
        if (_graphLoading)
        {
            _graphAgain |= reset;
            return;
        }

        _graphLoading = true;
        try
        {
            var existing = reset ? [] : GraphRows;
            var builder = reset ? new LogGraphBuilder() : _graph;
            var generation = _generation;
            var (rows, loaded) = await Task.Run(async () =>
            {
                var page = await repo.LogAsync(existing.Length, GraphPage, null, _stop.Token).ConfigureAwait(false);
                return ((GraphRowVm[])[.. existing, .. builder.Add(page).Select(r => new GraphRowVm(r))], page.Length);
            }, _stop.Token);
            if (generation != _generation || repo != ActiveGit) return;
            _graph = builder;
            GraphRows = rows;
            GraphLanes = Math.Clamp(rows.Length == 0 ? 1 : rows.Max(r => r.Row.LaneCount), 1, 12);
            GraphHasMore = loaded == GraphPage;
        }
        catch (GitException ex)
        {
            Log.Warn($"The graph could not be read: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _graphLoading = false;
            if (_graphAgain)
            {
                _graphAgain = false;
                _ = LoadGraphAsync(reset: true);
            }
        }
    }

    [RelayCommand]
    private void ToggleGraph() => GraphExpanded = !GraphExpanded;

    [RelayCommand]
    private void ToggleHistory() => HistoryExpanded = !HistoryExpanded;

    [RelayCommand]
    private void ToggleStashes() => StashesExpanded = !StashesExpanded;

    public Task LoadMoreGraphAsync() => GraphHasMore ? LoadGraphAsync(reset: false) : Task.CompletedTask;

    /// <summary>A click on a graph row: the commit and the files it changed.</summary>
    public async Task SelectCommitAsync(CommitRowVm? row)
    {
        if (row is null || ActiveGit is not { } repo)
        {
            SelectedCommit = null;
            CommitFiles = [];
            return;
        }

        SelectedCommit = row.Commit;
        try
        {
            var files = await Task.Run(() => repo.CommitFilesAsync(row.Commit.Hash, _stop.Token), _stop.Token);
            if (SelectedCommit?.Hash == row.Commit.Hash) CommitFiles = [.. files.Select(f => new CommitFileRow(f))];
        }
        catch (GitException ex)
        {
            Log.Warn($"The commit's files could not be read: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand]
    public async Task CheckoutCommitAsync(CommitRowVm row) => await CheckoutAsync(row.Commit.Hash, remote: false, detach: true);

    [RelayCommand]
    public Task BranchAtCommitAsync(CommitRowVm row) => CreateBranchAtAsync(row.Commit.Hash);

    [RelayCommand]
    public Task TagAtCommitAsync(CommitRowVm row) => CreateTagAtAsync(row.Commit.Hash);

    [RelayCommand]
    public Task CherryPickAsync(CommitRowVm row) => RunAsync("Cherry-picking", r => r.CherryPickAsync(row.Commit.Hash, _stop.Token), $"Cherry-picked {row.ShortHash}");

    [RelayCommand]
    public Task RevertCommitAsync(CommitRowVm row) => RunAsync("Reverting", r => r.RevertAsync(row.Commit.Hash, _stop.Token), $"Reverted {row.ShortHash}");

    [RelayCommand]
    public async Task CopyHashAsync(CommitRowVm row)
    {
        if (_shell.Clipboard is { } copy) await copy(row.Commit.Hash);
    }

    // ── A file's history (the Timeline) ──────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _historyExpanded;

    /// <summary>The active editor's file, relative to its repository; null when it is in none.</summary>
    [ObservableProperty]
    private string? _historyFile;

    [ObservableProperty]
    private CommitRowVm[] _historyRows = [];

    private CancellationTokenSource? _historyCts;

    partial void OnHistoryExpandedChanged(bool value)
    {
        if (value && HistoryFile is { } file) _ = LoadHistoryAsync(file);
    }

    public Task ShowFileHistoryAsync()
    {
        HistoryExpanded = true;
        _shell.Navigate(ShellRoutes.SourceControl);
        return Task.CompletedTask;
    }

    public async Task LoadHistoryAsync(string relativePath)
    {
        if (ActiveGit is not { } repo) return;
        _historyCts?.Cancel();
        var cts = _historyCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            var commits = await Task.Run(() => repo.FileHistoryAsync(relativePath, 0, 100, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested && HistoryFile == relativePath) HistoryRows = [.. commits.Select(c => new CommitRowVm(c))];
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Clicking an entry of a file's history shows what that commit did to the file.</summary>
    public async Task OpenHistoryEntryAsync(CommitRowVm row)
    {
        if (ActiveGit is not { } repo || HistoryFile is not { } file) return;
        await OpenDiffAsync(new ScmDiffRequest(repo, file, null, ScmDiffKind.Commit, row.Commit.Hash, $"{Path.GetFileName(file)} ({row.ShortHash})"));
    }

    [RelayCommand]
    public Task OpenCommitFileAsync(CommitFileRow row)
    {
        if (ActiveGit is not { } repo || SelectedCommit is not { } commit) return Task.CompletedTask;
        return OpenDiffAsync(new ScmDiffRequest(repo, row.File.Path, row.File.OriginalPath, ScmDiffKind.Commit, commit.Hash, $"{row.Name} ({commit.ShortHash})"));
    }

    // ── The editor follows ───────────────────────────────────────────────────────────────────────

    private Wicked.Editor.Model.Document? _watchedDocument;
    private BlameHunk[] _blameHunks = [];
    private string? _blamePath;
    private long _blameVersion = -1;

    /// <summary>The line the status bar shows for the caret's line: "You, 3 days ago • message". Empty when there is nothing to say.</summary>
    [ObservableProperty]
    private string _blameStatus = "";

    /// <summary>Whether blame is worked out at all (the Toggle Blame command).</summary>
    [ObservableProperty]
    private bool _showBlame = true;

    partial void OnShowBlameChanged(bool value)
    {
        if (value) UpdateBlameSoon(immediately: true);
        else BlameStatus = "";
    }

    private void OnActiveEditorChanged()
    {
        if (_watchedDocument is not null)
        {
            _watchedDocument.SelectionsChanged -= UpdateBlameLine;
            _watchedDocument.Changed -= OnWatchedDocumentChanged;
        }

        _watchedDocument = _bench.ActiveTab?.Document;
        if (_watchedDocument is not null)
        {
            _watchedDocument.SelectionsChanged += UpdateBlameLine;
            _watchedDocument.Changed += OnWatchedDocumentChanged;
        }

        var path = _bench.ActiveTab?.Path;
        var slot = path is null ? null : SlotFor(path);
        HistoryFile = slot is null ? null : RelativeTo(slot, path!);
        if (HistoryFile is null) HistoryRows = [];
        else if (HistoryExpanded && ActiveSlot == slot) _ = LoadHistoryAsync(HistoryFile);
        _blameHunks = [];
        _blamePath = null;
        UpdateBlameSoon(immediately: true);
    }

    // The document raises Changed in the middle of the edit (its dirty state is not up to date yet), so the blame is worked out once the edit is done.
    private void OnWatchedDocumentChanged(Wicked.Editor.Model.DocumentChange change) => _post(() => UpdateBlameSoon(immediately: false));

    private Slot? SlotFor(string absolutePath) =>
        _slots.Where(s => absolutePath.StartsWith(s.Repository.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)).OrderByDescending(s => s.Repository.Root.Length).FirstOrDefault();

    private static string RelativeTo(Slot slot, string absolutePath) => Path.GetRelativePath(slot.Repository.Root, absolutePath).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>The repository holding a file, for the editor's gutter (null when the file is in none).</summary>
    public GitRepository? RepositoryOf(string absolutePath) => SlotFor(absolutePath)?.Repository;

    /// <summary>
    /// A tracker for an open file's gutter marks. Give it the text as it changes (<see cref="LineChangeTracker.TextChanged"/>),
    /// call <see cref="LineChangeTracker.RefreshBaseAsync"/> now and whenever <see cref="RepositoryChanged"/> fires for its
    /// repository, and dispose it with the document. Null when the file is in no repository.
    /// </summary>
    public LineChangeTracker? CreateTracker(string absolutePath) =>
        SlotFor(absolutePath) is { } slot ? new LineChangeTracker(slot.Repository, RelativeTo(slot, absolutePath)) : null;

    private CancellationTokenSource? _blameDebounce;

    /// <summary>Works the blame of the active file out again after a pause (at once when the tab changed), on a worker.</summary>
    private void UpdateBlameSoon(bool immediately)
    {
        _blameDebounce?.Cancel();
        if (!ShowBlame || _bench.ActiveTab is not { Path: { } path } tab || SlotFor(path) is not { } slot)
        {
            BlameStatus = "";
            return;
        }

        var cts = _blameDebounce = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var doc = tab.Document;
        var version = doc.Version;
        var dirty = doc.IsDirty;
        var rope = doc.Text;
        var lineBreak = doc.LineBreak;
        var relative = RelativeTo(slot, path);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!immediately) await Task.Delay(500, cts.Token).ConfigureAwait(false);
                var hunks = new List<BlameHunk>();
                var contents = dirty ? rope.ToString().Replace("\n", lineBreak, StringComparison.Ordinal) : null;
                var ok = await slot.Repository.BlameAsync(relative, contents, h =>
                {
                    lock (hunks) hunks.Add(h);
                }, cts.Token).ConfigureAwait(false);
                BlameHunk[] sorted;
                lock (hunks) sorted = [.. hunks.OrderBy(h => h.StartLine)];
                _post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _blameHunks = ok ? sorted : [];
                    _blamePath = path;
                    _blameVersion = version;
                    UpdateBlameLine();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (GitException ex)
            {
                Log.Warn($"Blame failed for {relative}: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private void UpdateBlameLine()
    {
        if (!ShowBlame || _watchedDocument is not { } doc || _bench.ActiveTab?.Path != _blamePath || _blameHunks.Length == 0)
        {
            BlameStatus = "";
            return;
        }

        var line = doc.Text.Position(doc.Primary.Active).Line;
        var hunk = _blameHunks.FirstOrDefault(h => line >= h.StartLine && line < h.StartLine + h.LineCount);
        // Text typed since the blame was read is "not committed yet" until the next pass.
        BlameStatus = hunk is null ? "" : BlameText.Format(hunk, _userEmail, DateTimeOffset.Now);
    }

    // ── Opening a diff ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised to show a file's changes. A handler that shows them (the side-by-side diff editor) sets <see cref="ScmDiffRequest.Handled"/>;
    /// otherwise the panel opens the unified diff as a tab of its own.
    /// </summary>
    public event Action<ScmDiffRequest>? DiffRequested;

    [RelayCommand]
    public async Task OpenChangesAsync(ScmFileRow row)
    {
        if (ActiveGit is not { } repo) return;
        if (row.Group is ScmGroup.Merge or ScmGroup.Untracked || row.Change.WorkTree == GitChangeKind.Untracked)
        {
            await OpenFileAsync(row);
            return;
        }

        var kind = row.Group == ScmGroup.Staged ? ScmDiffKind.IndexVsHead : ScmDiffKind.WorkingVsIndex;
        var title = $"{row.Name} ({(row.Group == ScmGroup.Staged ? "Index" : "Working Tree")})";
        await OpenDiffAsync(new ScmDiffRequest(repo, row.Path, row.Change.OriginalPath, kind, null, title));
    }

    [RelayCommand]
    public async Task OpenFileAsync(ScmFileRow row)
    {
        if (ActiveGit is not { } repo) return;
        var full = Path.Combine(repo.Root, row.Path.Replace('/', Path.DirectorySeparatorChar));
        if (row.IsDeleted || !File.Exists(full))
        {
            await OpenChangesAsync(row);
            return;
        }

        await _bench.OpenAsync(full, preview: true);
    }

    public async Task OpenDiffAsync(ScmDiffRequest request)
    {
        DiffRequested?.Invoke(request);
        if (request.Handled) return;
        string text;
        try
        {
            text = await Task.Run(() => request.UnifiedAsync(_stop.Token), _stop.Token);
        }
        catch (GitException ex)
        {
            ErrorText = Summarise(ex.Details);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (text.Trim().Length == 0)
        {
            _notices.Post(new Core.Notice("git-done", "There is no difference to show", Core.NoticeKind.Info));
            return;
        }

        var (rope, _) = await Task.Run(() => Wicked.Editor.Text.TextLoading.Normalise(text));
        var document = new Wicked.Editor.Model.Document(rope);
        document.MarkSaved();
        var tab = new Workbench.EditorTab(null, document, Wicked.Editor.Syntax.Languages.ForName("diff"), Core.Files.FileEncoding.Utf8, default) { Label = request.Title };
        _bench.ActiveGroup.Add(tab);
    }
}

/// <summary>What a diff request compares.</summary>
public enum ScmDiffKind
{
    /// <summary>The file in the work tree against the index (the Changes group).</summary>
    WorkingVsIndex,

    /// <summary>The index against HEAD (the Staged group).</summary>
    IndexVsHead,

    /// <summary>A commit against its first parent (the graph, the file history).</summary>
    Commit,
}

/// <summary>
/// A request to show one file's changes. A diff editor reads <see cref="ReadOriginalAsync"/> (the left side) and
/// <see cref="ReadModifiedAsync"/> (the right side) and builds a <see cref="SideBySideDiff"/> from them; the panel's own fallback
/// shows <see cref="UnifiedAsync"/>.
/// </summary>
public sealed class ScmDiffRequest(GitRepository repository, string path, string? originalPath, ScmDiffKind kind, string? commit, string title)
{
    public GitRepository Repository { get; } = repository;

    /// <summary>The file relative to the repository root, with '/'.</summary>
    public string Path { get; } = path;

    /// <summary>Where a renamed file came from.</summary>
    public string? OriginalPath { get; } = originalPath;

    public ScmDiffKind Kind { get; } = kind;

    public string? Commit { get; } = commit;

    public string Title { get; } = title;

    /// <summary>The full path of the file in the work tree.</summary>
    public string FullPath => System.IO.Path.Combine(Repository.Root, Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <summary>Set by a handler that took the request over.</summary>
    public bool Handled { get; set; }

    /// <summary>The "before" text (empty for a file that did not exist then).</summary>
    public async Task<string> ReadOriginalAsync(CancellationToken ct = default) => Kind switch
    {
        ScmDiffKind.WorkingVsIndex => await Repository.ShowIndexAsync(Path, ct).ConfigureAwait(false) ?? "",
        ScmDiffKind.IndexVsHead => await Repository.ShowAsync("HEAD", OriginalPath ?? Path, ct).ConfigureAwait(false) ?? "",
        _ => await Repository.ShowAsync(Commit + "^", OriginalPath ?? Path, ct).ConfigureAwait(false) ?? "",
    };

    /// <summary>The "after" text: the file on disk, the index, or the commit's version.</summary>
    public async Task<string> ReadModifiedAsync(CancellationToken ct = default) => Kind switch
    {
        ScmDiffKind.WorkingVsIndex => File.Exists(FullPath) ? await File.ReadAllTextAsync(FullPath, ct).ConfigureAwait(false) : "",
        ScmDiffKind.IndexVsHead => await Repository.ShowIndexAsync(Path, ct).ConfigureAwait(false) ?? "",
        _ => await Repository.ShowAsync(Commit!, Path, ct).ConfigureAwait(false) ?? "",
    };

    /// <summary>The same changes as git's unified diff text.</summary>
    public Task<string> UnifiedAsync(CancellationToken ct = default) => Kind switch
    {
        ScmDiffKind.WorkingVsIndex => Repository.DiffAsync(Path, staged: false, 3, ct),
        ScmDiffKind.IndexVsHead => Repository.DiffAsync(Path, staged: true, 3, ct),
        _ => Repository.CommitDiffAsync(Commit!, Path, OriginalPath, 3, ct),
    };
}
