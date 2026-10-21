using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wicked.App.ViewModels;
using Wicked.Core.Git;

namespace Wicked.App.Git;

public sealed partial class SourceControlViewModel
{
    private static string BranchDetail(GitBranch b)
    {
        var sync = b.Ahead > 0 || b.Behind > 0 ? $"  {(b.Behind > 0 ? $"↓{b.Behind} " : "")}{(b.Ahead > 0 ? $"↑{b.Ahead}" : "")}".TrimEnd() : "";
        return $"{(b.IsCurrent ? "current  " : "")}{b.Subject}{sync}";
    }

    private async Task<(IReadOnlyList<GitBranch> Branches, IReadOnlyList<GitTag> Tags)> ReadRefsAsync(GitRepository repo) =>
        await Task.Run(async () => (
            (IReadOnlyList<GitBranch>)await repo.BranchesAsync(_stop.Token).ConfigureAwait(false),
            (IReadOnlyList<GitTag>)await repo.TagsAsync(_stop.Token).ConfigureAwait(false)), _stop.Token);

    /// <summary>Checkout to...: the branches, remote branches and tags in the palette, with the two ways to make a new branch on top.</summary>
    public async Task CheckoutToAsync()
    {
        if (ActiveGit is not { } repo) return;
        var (branches, tags) = await ReadRefsAsync(repo);
        var items = new List<QuickItem>
        {
            new("Create new branch...", "", "", CreateBranchAsync),
            new("Create new branch from...", "", "", CreateBranchFromAsync),
        };
        var locals = branches.Where(b => !b.IsRemote).ToList();
        items.AddRange(locals.Select(b => new QuickItem(b.Name, BranchDetail(b), "", () => CheckoutAsync(b.Name, remote: false, detach: false))));
        items.AddRange(branches.Where(b => b.IsRemote && locals.All(l => l.Name != b.ShortName)).Select(b => new QuickItem(b.Name, "remote branch", "", () => CheckoutAsync(b.Name, remote: true, detach: false))));
        items.AddRange(tags.Select(t => new QuickItem(t.Name, "tag (detached HEAD)", "", () => CheckoutAsync(t.Name, remote: false, detach: true))));
        _shell.ShowList("Select a branch or tag to checkout", items, 0);
    }

    /// <summary>Switches to a branch, a remote branch (a local tracking branch is made) or a tag. Local changes in the way are offered a stash.</summary>
    public async Task CheckoutAsync(string name, bool remote, bool detach)
    {
        string[] args = detach ? ["switch", "--detach", name] : remote ? ["switch", "--track", name] : ["switch", name];
        await RunAsync("Switching", r => r.WriteAsync(args, null, _stop.Token), $"Switched to {name}", handled: result => OfferStashingAsync(result, args));
    }

    private async Task<bool> OfferStashingAsync(GitResult failed, string[] switchArgs)
    {
        var text = failed.Error;
        if (!text.Contains("would be overwritten", StringComparison.Ordinal) && !text.Contains("commit your changes or stash", StringComparison.Ordinal)) return false;
        var yes = _bench.Dialogs is { } d && await d.ConfirmAsync("Your local changes would be overwritten by the switch.", "Stash them, switch, and put them back on the new branch?", "Stash and Switch");
        if (!yes) return true; // the question was the answer; no error strip
        await RunAsync("Stashing and switching", r => r.SwitchCarryingChangesAsync(switchArgs, _stop.Token), "Switched, carrying your changes");
        return true;
    }

    public async Task CreateBranchAsync()
    {
        if (ActiveGit is null || _bench.Dialogs is not { } dialogs) return;
        var name = (await dialogs.AskNameAsync("Branch name (created from the current commit)", ""))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await RunAsync("Creating the branch", r => r.CreateBranchAsync(name, null, true, _stop.Token), $"Created and switched to {name}");
    }

    public async Task CreateBranchFromAsync()
    {
        if (ActiveGit is not { } repo) return;
        var (branches, tags) = await ReadRefsAsync(repo);
        var items = new List<QuickItem>();
        items.AddRange(branches.Select(b => new QuickItem(b.Name, b.IsRemote ? "remote branch" : BranchDetail(b), "", () => AskAndCreateAsync(b.Name))));
        items.AddRange(tags.Select(t => new QuickItem(t.Name, "tag", "", () => AskAndCreateAsync(t.Name))));
        _shell.ShowList("Select a reference to create the branch from", items, 0);
    }

    public async Task CreateBranchAtAsync(string startPoint) => await AskAndCreateAsync(startPoint);

    private async Task AskAndCreateAsync(string startPoint)
    {
        if (_bench.Dialogs is not { } dialogs) return;
        var name = (await dialogs.AskNameAsync($"Branch name (created from {startPoint})", ""))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await RunAsync("Creating the branch", r => r.CreateBranchAsync(name, startPoint, true, _stop.Token), $"Created and switched to {name}");
    }

    public async Task RenameBranchAsync()
    {
        if (Status.Branch.Head is not { } current || _bench.Dialogs is not { } dialogs) return;
        var name = (await dialogs.AskNameAsync($"New name for branch {current}", current))?.Trim();
        if (string.IsNullOrEmpty(name) || name == current) return;
        await RunAsync("Renaming", r => r.RenameBranchAsync(current, name, _stop.Token), $"Renamed {current} to {name}");
    }

    public async Task DeleteBranchAsync()
    {
        if (ActiveGit is not { } repo) return;
        var (branches, _) = await ReadRefsAsync(repo);
        var items = branches.Where(b => !b.IsRemote && !b.IsCurrent).Select(b => new QuickItem(b.Name, BranchDetail(b), "", () => DeleteNamedBranchAsync(b.Name))).ToList();
        if (items.Count == 0)
        {
            _notices.Post(new Core.Notice("git-done", "There is no other branch to delete", Core.NoticeKind.Info));
            return;
        }

        _shell.ShowList("Select a branch to delete", items, 0);
    }

    private async Task DeleteNamedBranchAsync(string name)
    {
        await RunAsync("Deleting the branch", r => r.DeleteBranchAsync(name, false, _stop.Token), $"Deleted {name}", handled: async result =>
        {
            if (!result.Error.Contains("not fully merged", StringComparison.Ordinal)) return false;
            var yes = _bench.Dialogs is { } d && await d.ConfirmAsync($"The branch {name} is not fully merged.", "Delete it anyway? Its commits will only be reachable through the reflog.", "Delete Branch");
            if (yes) await RunAsync("Deleting the branch", r => r.DeleteBranchAsync(name, true, _stop.Token), $"Deleted {name}");
            return true;
        });
    }

    public async Task MergeBranchAsync()
    {
        if (ActiveGit is not { } repo) return;
        var (branches, _) = await ReadRefsAsync(repo);
        var items = branches.Where(b => !b.IsCurrent).Select(b => new QuickItem(b.Name, b.IsRemote ? "remote branch" : BranchDetail(b), "", () => MergeNamedAsync(b.Name))).ToList();
        _shell.ShowList("Select a branch to merge into the current one", items, 0);
    }

    private Task MergeNamedAsync(string name) => RunAsync("Merging", r => r.MergeAsync(name, _stop.Token), $"Merged {name}");

    public async Task CreateTagAsync()
    {
        if (ActiveGit is null || _bench.Dialogs is not { } dialogs) return;
        var name = (await dialogs.AskNameAsync("Tag name", ""))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        var message = (await dialogs.AskNameAsync("Message (leave empty for a lightweight tag)", ""))?.Trim();
        await RunAsync("Tagging", r => r.CreateTagAsync(name, string.IsNullOrEmpty(message) ? null : message, null, _stop.Token), $"Created tag {name}");
    }

    public async Task CreateTagAtAsync(string hash)
    {
        if (_bench.Dialogs is not { } dialogs) return;
        var name = (await dialogs.AskNameAsync($"Tag name (for {hash[..Math.Min(7, hash.Length)]})", ""))?.Trim();
        if (string.IsNullOrEmpty(name)) return;
        await RunAsync("Tagging", r => r.CreateTagAsync(name, null, hash, _stop.Token), $"Created tag {name}");
    }

    // ── Stash ────────────────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    private GitStash[] _stashes = [];

    [ObservableProperty]
    private bool _stashesExpanded;

    public async Task StashPushAsync()
    {
        if (ActiveGit is null || _bench.Dialogs is not { } dialogs) return;
        if (Status.IsClean)
        {
            _notices.Post(new Core.Notice("git-done", "There are no changes to stash", Core.NoticeKind.Info));
            return;
        }

        var message = await dialogs.AskNameAsync("Stash message (optional)", "");
        if (message is null) return;
        await RunAsync("Stashing", r => r.StashPushAsync(message, includeUntracked: true, _stop.Token), "Changes stashed", graphChanged: false);
    }

    public async Task PopLatestStashAsync()
    {
        if (Stashes.Length == 0)
        {
            _notices.Post(new Core.Notice("git-done", "There are no stashes", Core.NoticeKind.Info));
            return;
        }

        await RunAsync("Popping the stash", r => r.StashPopAsync(Stashes[0].Ref, _stop.Token), "Stash applied and dropped", graphChanged: false);
    }

    [RelayCommand]
    public Task ApplyStashAsync(GitStash stash) => RunAsync("Applying the stash", r => r.StashApplyAsync(stash.Ref, _stop.Token), "Stash applied", graphChanged: false);

    [RelayCommand]
    public Task PopStashAsync(GitStash stash) => RunAsync("Popping the stash", r => r.StashPopAsync(stash.Ref, _stop.Token), "Stash applied and dropped", graphChanged: false);

    [RelayCommand]
    public async Task DropStashAsync(GitStash stash)
    {
        var yes = _bench.Dialogs is { } d && await d.ConfirmAsync($"Drop {stash.Ref}?", string.IsNullOrEmpty(stash.Message) ? "The stash is deleted for good." : $"“{stash.Message}” is deleted for good.", "Drop Stash");
        if (yes) await RunAsync("Dropping the stash", r => r.StashDropAsync(stash.Ref, _stop.Token), "Stash dropped", graphChanged: false);
    }

    private Task PickStashAsync(string verb, Func<GitStash, Task> act)
    {
        if (Stashes.Length == 0)
        {
            _notices.Post(new Core.Notice("git-done", "There are no stashes", Core.NoticeKind.Info));
            return Task.CompletedTask;
        }

        var items = Stashes.Select(s => new QuickItem(s.Ref, string.IsNullOrEmpty(s.Message) ? s.Branch : $"{s.Message}  ({s.Branch})", "", () => act(s))).ToList();
        _shell.ShowList($"Select a stash to {verb.ToLowerInvariant()}", items, 0);
        return Task.CompletedTask;
    }
}
