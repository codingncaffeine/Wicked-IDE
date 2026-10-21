using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.Shell;
using Wicked.Core;
using Wicked.Core.Git;

namespace Wicked.App.Git;

public sealed partial class SourceControlViewModel
{
    // ── The commit box ───────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubjectLength), nameof(SubjectHint), nameof(SubjectTooLong), nameof(CanCommit))]
    private string _commitMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCommit))]
    private bool _amend;

    [ObservableProperty]
    private bool _signOff;

    /// <summary>An error from the last action, shown over the list until dismissed or the next action succeeds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy), nameof(CanCommit))]
    private int _busyCount;

    [ObservableProperty]
    private string _busyText = "";

    public bool HasError => ErrorText.Length > 0;

    /// <summary>Writes a commit message from the changes (the assistant's model; the shell sets it). Null: no button.</summary>
    public Func<CancellationToken, Task<string?>>? MessageWriter { get; set; }

    public bool CanWriteMessage => MessageWriter is not null;

    [ObservableProperty]
    private bool _writingMessage;

    /// <summary>The sparkle in the commit box: a message for what is staged (or, with nothing staged, every change).</summary>
    [RelayCommand]
    private async Task WriteMessage()
    {
        if (MessageWriter is not { } write || WritingMessage) return;
        WritingMessage = true;
        try
        {
            if (await write(CancellationToken.None) is { Length: > 0 } message) CommitMessage = message;
        }
        finally
        {
            WritingMessage = false;
        }
    }

    public bool IsBusy => BusyCount > 0;

    /// <summary>Length of the message's first line; the box warns past the usual 72.</summary>
    public int SubjectLength => (CommitMessage.Split('\n', 2)[0]).TrimEnd('\r').Length;

    public bool SubjectTooLong => SubjectLength > 72;

    public string SubjectHint => SubjectLength >= 50 ? $"{SubjectLength}/72" : "";

    public string CommitPlaceholder => ActiveRepository is null ? "" : $"Message (Ctrl+Enter to commit on \"{BranchName}\")";

    public bool CanCommit => HasRepository && !IsBusy && (CommitMessage.Trim().Length > 0 || Amend);

    /// <summary>Asks the view to put the keyboard in the commit box.</summary>
    public event Action? FocusCommitRequested;

    public event Action? OutputRequested;

    partial void OnAmendChanged(bool value)
    {
        if (!value || CommitMessage.Trim().Length > 0 || ActiveGit is not { } repo) return;
        _ = Task.Run(async () =>
        {
            var last = await repo.LastCommitMessageAsync(_stop.Token).ConfigureAwait(false);
            _post(() =>
            {
                if (Amend && CommitMessage.Length == 0 && last is not null) CommitMessage = last;
            });
        });
    }

    [RelayCommand]
    private void DismissError() => ErrorText = "";

    [RelayCommand]
    private void ShowOutput() => OutputRequested?.Invoke();

    // ── Running git ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs a git action on a worker with the panel marked busy, reports a failure (a notice, the error strip, the Output log
    /// has the command), then reads the repository again. Returns whether git succeeded.
    /// </summary>
    private async Task<bool> RunAsync(string busyText, Func<GitRepository, Task<GitResult>> action, string? done = null, bool graphChanged = true, Func<GitResult, Task<bool>>? handled = null)
    {
        if (ActiveSlot is not { } slot) return false;
        BusyCount++;
        BusyText = busyText;
        GitResult result;
        try
        {
            result = await Task.Run(() => action(slot.Repository), _stop.Token);
        }
        catch (GitException ex)
        {
            result = ex.Details;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            BusyCount--;
            if (BusyCount == 0) BusyText = "";
        }

        if (graphChanged) _graphStale = true;
        if (!result.Ok && handled is not null && await handled(result))
        {
            await RefreshSlotAsync(slot);
            return false;
        }

        if (!result.Ok)
        {
            var text = Summarise(result);
            ErrorText = text;
            _notices.Post(new Notice("git", "Git: " + text.Split('\n')[0], NoticeKind.Error, ShellRoutes.SourceControl));
            await RefreshSlotAsync(slot);
            return false;
        }

        ErrorText = "";
        _notices.Clear("git");
        if (done is not null) _notices.Post(new Notice("git-done", done, NoticeKind.Success));
        await RefreshSlotAsync(slot);
        return true;
    }

    /// <summary>What went wrong, from git's own words: stderr (or stdout), the first few lines.</summary>
    private static string Summarise(GitResult result)
    {
        var text = (result.Error.Trim().Length > 0 ? result.Error : result.Output).Trim();
        if (text.Length == 0) text = $"git exited with code {result.ExitCode}";
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).Take(8).ToList();
        return string.Join('\n', lines);
    }

    // ── Staging ──────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public Task StageAllAsync() => RunAsync("Staging", r => r.StageAllAsync(_stop.Token), graphChanged: false);

    [RelayCommand]
    public Task UnstageAllAsync() => RunAsync("Unstaging", r => r.UnstageAllAsync(_stop.Token), graphChanged: false);

    private static IEnumerable<string> PathsOf(IEnumerable<ScmFileRow> rows, bool withOriginals) =>
        rows.SelectMany(r => withOriginals && r.Change.OriginalPath is { } from ? new[] { r.Path, from } : [r.Path]).Distinct();

    [RelayCommand]
    public async Task StageRowAsync(ScmFileRow row) => await StageRowsAsync([row]);

    [RelayCommand]
    public async Task UnstageRowAsync(ScmFileRow row) => await UnstageRowsAsync([row]);

    [RelayCommand]
    public async Task DiscardRowAsync(ScmFileRow row) => await DiscardRowsAsync([row]);

    public async Task StageRowsAsync(IReadOnlyList<ScmFileRow> rows)
    {
        var targets = rows.Where(r => r.CanStage).ToList();
        if (targets.Count == 0) return;
        // A file that still holds conflict markers is not resolved: ask before staging it as if it were.
        if (targets.Any(r => r.IsConflict) && await StillConflictedAsync(targets.Where(r => r.IsConflict)) is { } unresolved)
        {
            var ok = _bench.Dialogs is { } d && await d.ConfirmAsync($"{unresolved} still has conflict markers.", "Stage it anyway, as resolved?", "Stage Anyway");
            if (!ok) return;
        }

        var paths = PathsOf(targets, withOriginals: false).ToList();
        await RunAsync("Staging", r => r.StageAsync(paths, _stop.Token), graphChanged: false);
    }

    private async Task<string?> StillConflictedAsync(IEnumerable<ScmFileRow> rows)
    {
        var root = ActiveGit?.Root;
        if (root is null) return null;
        var paths = rows.Select(r => r.Path).ToList();
        return await Task.Run(() =>
        {
            foreach (var path in paths)
            {
                try
                {
                    var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(full) && !ConflictParser.Parse(TextLines.Split(File.ReadAllText(full))).IsEmpty) return path;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // An unreadable file is staged as git finds it.
                }
            }

            return null;
        });
    }

    public async Task UnstageRowsAsync(IReadOnlyList<ScmFileRow> rows)
    {
        var paths = PathsOf(rows.Where(r => r.IsStaged), withOriginals: true).ToList();
        if (paths.Count == 0) return;
        await RunAsync("Unstaging", r => r.UnstageAsync(paths, _stop.Token), graphChanged: false);
    }

    public async Task DiscardRowsAsync(IReadOnlyList<ScmFileRow> rows)
    {
        var targets = rows.Where(r => r.CanDiscard).ToList();
        if (targets.Count == 0 || ActiveGit is null) return;
        if (!await ConfirmDiscardAsync(targets)) return;
        var tracked = targets.Where(r => r.Group == ScmGroup.Changes).Select(r => r.Path).ToList();
        var untracked = targets.Where(r => r.Group == ScmGroup.Untracked).Select(r => r.Path).ToList();
        await RunAsync("Discarding", async r =>
        {
            var result = await r.DiscardAsync(tracked, _stop.Token).ConfigureAwait(false);
            return result.Ok && untracked.Count > 0 ? await r.CleanAsync(untracked, _stop.Token).ConfigureAwait(false) : result;
        }, graphChanged: false);
    }

    private async Task<bool> ConfirmDiscardAsync(IReadOnlyList<ScmFileRow> targets)
    {
        if (_bench.Dialogs is not { } dialogs) return false;
        var onlyUntracked = targets.All(r => r.Group == ScmGroup.Untracked);
        var name = targets.Count == 1 ? $"“{targets[0].Name}”" : $"{targets.Count} files";
        return await dialogs.ConfirmAsync(
            onlyUntracked ? $"Delete {name}?" : $"Discard changes to {name}?",
            onlyUntracked ? "The file is not in Git: deleting it cannot be undone." : "This cannot be undone. Staged changes are kept.",
            onlyUntracked ? "Delete" : "Discard");
    }

    [RelayCommand]
    public async Task StageGroupAsync(ScmGroupRow group)
    {
        var rows = FileRowsOf(group.Group);
        if (group.Group == ScmGroup.Merge) await StageRowsAsync(rows);
        else await RunAsync("Staging", r => r.StageAsync([.. PathsOf(rows, withOriginals: false)], _stop.Token), graphChanged: false);
    }

    [RelayCommand]
    public Task UnstageGroupAsync(ScmGroupRow group) => UnstageAllAsync();

    [RelayCommand]
    public async Task DiscardGroupAsync(ScmGroupRow group) => await DiscardRowsAsync(FileRowsOf(group.Group));

    private List<ScmFileRow> FileRowsOf(ScmGroup group)
    {
        // Rows of a folded group are not in the list, so they are made from the status instead.
        var status = Status;
        IEnumerable<GitChange> changes = group switch
        {
            ScmGroup.Merge => status.Conflicts,
            ScmGroup.Staged => status.Staged,
            ScmGroup.Changes => status.Unstaged,
            _ => status.Untracked,
        };
        return [.. changes.Select(c => new ScmFileRow(c, group, 0, false))];
    }

    // ── Commit ───────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public Task CommitAsync() => CommitCoreAsync(push: false);

    [RelayCommand]
    public Task CommitAndPushAsync() => CommitCoreAsync(push: true);

    private async Task CommitCoreAsync(bool push)
    {
        if (ActiveSlot is null || IsBusy) return;
        var message = CommitMessage;
        if (message.Trim().Length == 0 && !Amend)
        {
            ErrorText = "Enter a commit message first.";
            FocusCommitRequested?.Invoke();
            return;
        }

        var status = Status;
        var amend = Amend;
        if (!amend && !status.Staged.Any() && status.Operation == GitOperation.None)
        {
            if (!status.Unstaged.Any() && !status.Untracked.Any())
            {
                ErrorText = "There is nothing to commit.";
                return;
            }

            // VS Code's smart commit: nothing is staged, so offer to stage everything first.
            var yes = _bench.Dialogs is { } dialogs && await dialogs.ConfirmAsync("There are no staged changes to commit.", "Stage all your changes and commit them directly?", "Stage All and Commit");
            if (!yes) return;
            if (!await RunAsync("Staging", r => r.StageAllAsync(_stop.Token), graphChanged: false)) return;
        }

        var options = new GitCommitOptions(message, amend, SignOff);
        var ok = await RunAsync(amend ? "Amending" : "Committing", r => r.CommitAsync(options, _stop.Token), amend ? "Commit amended" : "Committed");
        if (!ok) return;
        CommitMessage = "";
        Amend = false;
        if (push) await PushAsync();
    }

    [RelayCommand]
    public async Task UndoLastCommitAsync()
    {
        if (ActiveSlot is not { } slot || IsBusy) return;
        string? message = null;
        var ok = await RunAsync("Undoing the last commit", async r =>
        {
            var (result, old) = await r.UndoLastCommitAsync(_stop.Token).ConfigureAwait(false);
            message = old;
            return result;
        }, "Last commit undone; its changes are staged");
        if (ok && message is not null && CommitMessage.Trim().Length == 0 && slot == ActiveSlot) CommitMessage = message;
    }

    // ── Remotes ──────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public Task FetchAsync() => RunAsync("Fetching", r => r.FetchAsync(null, _stop.Token), "Fetched");

    [RelayCommand]
    public Task PullAsync() => RunAsync("Pulling", r => r.PullAsync(GitPullMode.Configured, _stop.Token), "Pulled");

    [RelayCommand]
    public Task PushAsync() => RunAsync("Pushing", r => r.PushAsync(false, _stop.Token), "Pushed");

    [RelayCommand]
    public async Task ForcePushAsync()
    {
        var yes = _bench.Dialogs is { } d && await d.ConfirmAsync("Force push with lease?", "This replaces the remote branch with yours, but only if nobody pushed to it since you last fetched.", "Force Push");
        if (yes) await RunAsync("Force pushing", r => r.PushAsync(true, _stop.Token), "Force pushed");
    }

    /// <summary>The Sync Changes button: pull what is behind, then push what is ahead; a branch with no upstream is published.</summary>
    [RelayCommand]
    public async Task SyncAsync()
    {
        var branch = Status.Branch;
        if (!branch.HasUpstream)
        {
            await PushAsync();
            return;
        }

        if (branch.Behind > 0 && !await RunAsync("Pulling", r => r.PullAsync(GitPullMode.Configured, _stop.Token))) return;
        if (Status.Branch.Ahead > 0 || branch.Ahead > 0) await RunAsync("Pushing", r => r.PushAsync(false, _stop.Token), "Synced");
        else _notices.Post(new Notice("git-done", "Synced", NoticeKind.Success));
    }

    // ── Operations that stopped halfway ──────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task AbortOperationAsync()
    {
        var operation = Status.Operation;
        if (operation == GitOperation.None) return;
        var yes = _bench.Dialogs is { } d && await d.ConfirmAsync($"Abort the {operation.ToString().ToLowerInvariant()}?", "Everything done since it started is undone.", "Abort");
        if (yes) await RunAsync("Aborting", r => r.AbortAsync(operation, _stop.Token), "Aborted");
    }

    [RelayCommand]
    public Task ContinueOperationAsync()
    {
        var operation = Status.Operation;
        return operation == GitOperation.None ? Task.CompletedTask : RunAsync("Continuing", r => r.ContinueAsync(operation, _stop.Token), "Continued");
    }

    // ── Init ─────────────────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    public async Task InitializeAsync()
    {
        if (Folder is not { } folder) return;
        try
        {
            BusyCount++;
            await GitRepository.InitAsync(folder, ComposeEnvironment(), _stop.Token);
        }
        catch (GitException ex)
        {
            ErrorText = Summarise(ex.Details);
            return;
        }
        finally
        {
            BusyCount--;
        }

        await OpenFolderAsync(folder);
    }

    // ── Credentials ──────────────────────────────────────────────────────────────────────────────

    private readonly SemaphoreSlim _promptGate = new(1, 1);

    /// <summary>A question from git, asked one at a time (a pull may ask for a name and then a password).</summary>
    private async Task<string?> AskAsync(AskpassRequest request)
    {
        await _promptGate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            var answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _post(() => Prompt = new GitPromptViewModel(request, text =>
            {
                Prompt = null;
                answer.TrySetResult(text);
            }));
            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            _promptGate.Release();
        }
    }

    /// <summary>Palette entries for the shell's command list (Git: Commit, Stage All, Pull, Push, Fetch, Checkout to..., ...).</summary>
    public IReadOnlyList<WorkbenchCommand> Commands { get; }

    private List<WorkbenchCommand> BuildCommands()
    {
        Task Show(Func<Task> run) => run();
        WorkbenchCommand Cmd(string id, string title, Func<Task> run) => new($"git.{id}", "Git", title, null, run);
        return
        [
            Cmd("commit", "Commit", () => Show(CommitAsync)),
            Cmd("commitAndPush", "Commit and Push", () => Show(CommitAndPushAsync)),
            Cmd("undoCommit", "Undo Last Commit", () => Show(UndoLastCommitAsync)),
            Cmd("stageAll", "Stage All Changes", () => Show(StageAllAsync)),
            Cmd("unstageAll", "Unstage All Changes", () => Show(UnstageAllAsync)),
            Cmd("pull", "Pull", () => Show(PullAsync)),
            Cmd("push", "Push", () => Show(PushAsync)),
            Cmd("pushForce", "Push (Force with Lease)", () => Show(ForcePushAsync)),
            Cmd("fetch", "Fetch", () => Show(FetchAsync)),
            Cmd("sync", "Sync", () => Show(SyncAsync)),
            Cmd("checkout", "Checkout to...", CheckoutToAsync),
            Cmd("branch", "Create Branch...", CreateBranchAsync),
            Cmd("branchFrom", "Create Branch From...", CreateBranchFromAsync),
            Cmd("renameBranch", "Rename Branch...", RenameBranchAsync),
            Cmd("deleteBranch", "Delete Branch...", DeleteBranchAsync),
            Cmd("merge", "Merge Branch...", MergeBranchAsync),
            Cmd("tag", "Create Tag...", CreateTagAsync),
            Cmd("stash", "Stash", StashPushAsync),
            Cmd("stashPop", "Pop Latest Stash", PopLatestStashAsync),
            Cmd("stashApply", "Apply Stash...", () => PickStashAsync("Apply", s => ApplyStashAsync(s))),
            Cmd("stashDrop", "Drop Stash...", () => PickStashAsync("Drop", s => DropStashAsync(s))),
            Cmd("refresh", "Refresh", () => Show(RefreshAsync)),
            Cmd("init", "Initialize Repository", () => Show(InitializeAsync)),
            Cmd("abort", "Abort Merge, Rebase or Cherry-pick", () => Show(AbortOperationAsync)),
            Cmd("showOutput", "Show Git Output", () =>
            {
                OutputRequested?.Invoke();
                return Task.CompletedTask;
            }),
            Cmd("stageSelected", "Stage Selected Ranges", StageSelectedRangesAsync),
            Cmd("revertSelected", "Revert Selected Ranges", RevertSelectedRangesAsync),
            Cmd("stageChange", "Stage Change at Cursor", StageChangeAtCaretAsync),
            Cmd("revertChange", "Revert Change at Cursor", RevertChangeAtCaretAsync),
            Cmd("fileHistory", "View File History", ShowFileHistoryAsync),
            Cmd("toggleBlame", "Toggle Blame in the Status Bar", () =>
            {
                ShowBlame = !ShowBlame;
                return Task.CompletedTask;
            }),
        ];
    }
}

public sealed partial class SourceControlViewModel
{
    /// <summary>Takes one whole side of each conflicted file (the version of the branch being merged into, or the incoming one) and stages it.</summary>
    public async Task TakeSideAsync(IReadOnlyList<ScmFileRow> rows, bool ours)
    {
        var paths = rows.Select(r => r.Path).ToList();
        if (paths.Count == 0) return;
        await RunAsync("Resolving", async r =>
        {
            var last = new GitResult(0, "", "");
            foreach (var path in paths)
            {
                last = await r.TakeSideAsync(path, ours, _stop.Token).ConfigureAwait(false);
                if (!last.Ok) return last;
            }

            return last;
        }, graphChanged: false);
    }

    /// <summary>Adds untracked files to the repository's <c>.gitignore</c> (one anchored line each), on a worker.</summary>
    public async Task IgnoreAsync(IReadOnlyList<ScmFileRow> rows)
    {
        if (ActiveGit is not { } repo) return;
        var lines = rows.Where(r => r.Group == ScmGroup.Untracked).Select(r => "/" + r.Path).Distinct().ToList(); // not a path: gitignore syntax
        if (lines.Count == 0) return;
        var file = Path.Combine(repo.Root, ".gitignore");
        try
        {
            await Task.Run(() =>
            {
                var existing = File.Exists(file) ? File.ReadAllText(file) : "";
                var separator = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";
                File.AppendAllText(file, separator + string.Join('\n', lines) + "\n");
            }, _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = $".gitignore could not be written: {ex.Message}";
            return;
        }

        await RefreshAsync();
    }

    public async Task CopyPathsAsync(IReadOnlyList<ScmFileRow> rows, bool relative)
    {
        if (ActiveGit is not { } repo || _shell.Clipboard is not { } copy) return;
        var text = string.Join('\n', rows.Select(r => relative ? r.Path : Path.Combine(repo.Root, r.Path.Replace('/', Path.DirectorySeparatorChar))));
        await copy(text);
    }
}

public sealed partial class SourceControlViewModel
{
    // ── Lines and hunks of the open file (the editor's gutter and the selection commands) ────────

    /// <summary>
    /// Stages the changes of a file that fall in a range of its working text (0-based lines, inclusive), by applying a patch
    /// built from just those lines. The file must be saved: the patch is made from what is on disk. Returns whether git applied it.
    /// </summary>
    public Task<bool> StageLinesAsync(string absolutePath, int firstLine, int lastLine) => ApplyLinesAsync(absolutePath, PatchAction.Stage, firstLine, lastLine, false);

    /// <summary>Reverts the changes of a file in a range of lines to what the index holds (the work tree file changes; unsaved edits are not touched, and the file must be saved).</summary>
    public Task<bool> RevertLinesAsync(string absolutePath, int firstLine, int lastLine) => ApplyLinesAsync(absolutePath, PatchAction.Discard, firstLine, lastLine, false);

    /// <summary>Stages the whole change (hunk) that covers a line.</summary>
    public Task<bool> StageHunkAtAsync(string absolutePath, int line) => ApplyLinesAsync(absolutePath, PatchAction.Stage, line, line, true);

    /// <summary>Reverts the whole change (hunk) that covers a line.</summary>
    public Task<bool> RevertHunkAtAsync(string absolutePath, int line) => ApplyLinesAsync(absolutePath, PatchAction.Discard, line, line, true);

    private async Task<bool> ApplyLinesAsync(string absolutePath, PatchAction action, int firstLine, int lastLine, bool wholeHunk)
    {
        if (SlotFor(absolutePath) is not { } slot) return false;
        if (_bench.AllTabs.FirstOrDefault(t => t.Path == absolutePath) is { IsDirty: true })
        {
            _notices.Post(new Notice("git-done", "Save the file first: changes are staged from what is on disk", NoticeKind.Warning));
            return false;
        }

        var relative = RelativeTo(slot, absolutePath);
        return await RunAsync("Applying the change", async r =>
        {
            var patch = await r.PatchAsync(relative, staged: false, _stop.Token).ConfigureAwait(false);
            var selection = wholeHunk ? PatchSelection.ForHunkAt(patch, firstLine) : PatchSelection.ForNewLines(patch, firstLine, lastLine);
            return selection is null
                ? new GitResult(1, "", "There is no change in the selected lines.")
                : await r.ApplySelectionAsync(patch, action, selection, _stop.Token).ConfigureAwait(false);
        }, graphChanged: false);
    }

    private (string Path, int First, int Last)? ActiveSelection()
    {
        if (_bench.ActiveTab is not { Path: { } path } tab) return null;
        var doc = tab.Document;
        var selection = doc.Primary;
        var first = doc.Text.LineOf(selection.Start);
        var last = doc.Text.LineOf(selection.End);
        // A selection that ends at the very start of a line did not take that line.
        if (!selection.IsEmpty && last > first && doc.Text.LineStart(last) == selection.End) last--;
        return (path, first, last);
    }

    private Task StageSelectedRangesAsync() => ActiveSelection() is { } s ? StageLinesAsync(s.Path, s.First, s.Last) : Task.CompletedTask;

    private Task RevertSelectedRangesAsync() => ActiveSelection() is { } s ? ConfirmThenAsync("Revert the changes in the selected lines?", "The lines go back to what the index holds. This cannot be undone.", "Revert", () => RevertLinesAsync(s.Path, s.First, s.Last)) : Task.CompletedTask;

    private Task StageChangeAtCaretAsync() => ActiveSelection() is { } s ? StageHunkAtAsync(s.Path, s.First) : Task.CompletedTask;

    private Task RevertChangeAtCaretAsync() => ActiveSelection() is { } s ? ConfirmThenAsync("Revert this change?", "The lines go back to what the index holds. This cannot be undone.", "Revert", () => RevertHunkAtAsync(s.Path, s.First)) : Task.CompletedTask;

    private async Task ConfirmThenAsync(string message, string detail, string verb, Func<Task> act)
    {
        if (_bench.Dialogs is { } dialogs && await dialogs.ConfirmAsync(message, detail, verb)) await act();
    }

    /// <summary>Applies a prepared selection of hunks or lines of one file (the side-by-side diff editor's stage, unstage and revert buttons).</summary>
    public Task<bool> ApplyPatchAsync(string relativePath, PatchAction action, PatchSelection selection) =>
        RunAsync("Applying the change", r => r.ApplySelectionAsync(relativePath, action, selection, _stop.Token), graphChanged: false);
}
