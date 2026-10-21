using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wicked.Core.Catalog;
using Wicked.Core.Chat;
using Wicked.Core.Downloads;
using Wicked.Core.Hub;
using Wicked.Core.Store;

namespace Wicked.Core.Models;

/// <summary>What one catalog refresh found: whether a newer catalog arrived, why not when it could not, and the picks that beat what the user has.</summary>
/// <param name="Changed">True when the catalog in force is a newer generation than before.</param>
/// <param name="Note">Why the online catalog could not be had, or null.</param>
/// <param name="Better">Recommended models for this machine that are not installed and clearly beat the user's installed model for the same job.</param>
public sealed record CatalogRefresh(bool Changed, string? Note, IReadOnlyList<Pick> Better);

public enum UpdateResult
{
    /// <summary>The new files answered a prompt and the old ones are gone.</summary>
    Updated,

    /// <summary>The new files failed to load or to answer; the old ones are back.</summary>
    RolledBack,

    /// <summary>The model is in use or another process is updating it; nothing was touched.</summary>
    Deferred,

    /// <summary>There is nothing newer, or Wicked cannot apply it (the repo renamed its files).</summary>
    NothingToDo,
}

/// <summary>How applying an update ended, in words fit to show.</summary>
public sealed record UpdateOutcome(string ModelId, UpdateResult Result, string Message, string? FromRevision = null, string? ToRevision = null);

public sealed partial class ModelServices
{
    private readonly UpdateStateStore _updateState;
    private readonly AppReleases _appReleases;
    private readonly Dictionary<string, SemaphoreSlim> _updateGates = new(StringComparer.Ordinal);

    /// <summary>The scheduler that keeps the models, the catalog, the engine and the app checked (D7); started by whoever hosts these services.</summary>
    public UpdateScheduler Updates { get; }

    internal AppReleases AppReleases => _appReleases;

    internal string RuntimeFolder => _paths.Runtime;

    internal UpdateStateStore UpdateStateStore => _updateState;

    /// <summary>The default policy and whether the scheduler looks at all, as the settings page put them.</summary>
    internal (UpdatePolicy Default, bool ChecksEnabled) UpdateSettings
    {
        get
        {
            lock (_gate) return (_defaultPolicy, _checksEnabled);
        }
    }

    private static bool IsLoopback(Uri? uri) => uri is not null && Http.WickedHttp.IsLoopback(uri);

    // ── Policy ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>What happens when <paramref name="model"/> has a newer upload: its own choice, else the default from Settings.</summary>
    public UpdatePolicy PolicyOf(StoredModel model)
    {
        if (model.Manifest?.Settings.TryGetValue(UpdatePolicies.SettingKey, out var value) == true && UpdatePolicies.Parse(value) is { } own) return own;
        lock (_gate) return _defaultPolicy;
    }

    /// <summary>True when the model has a policy of its own (the default does not apply).</summary>
    public static bool HasOwnPolicy(StoredModel model) =>
        model.Manifest?.Settings.TryGetValue(UpdatePolicies.SettingKey, out var value) == true && UpdatePolicies.Parse(value) is not null;

    /// <summary>Gives a model its own policy (null returns it to the default). Kept in the model's <c>wicked.json</c>.</summary>
    public async Task SetUpdatePolicyAsync(string modelId, UpdatePolicy? policy, CancellationToken ct = default)
    {
        var model = State.Installed.FirstOrDefault(m => m.Id == modelId) ?? throw new FileNotFoundException($"{modelId} is not installed");
        await StoreFor(model).SetSettingAsync(modelId, UpdatePolicies.SettingKey, policy is { } p ? UpdatePolicies.Name(p) : null, ct).ConfigureAwait(false);
        await RescanAsync(ct).ConfigureAwait(false);
    }

    /// <summary>"Don't tell me about this model": pins it. The update notice for it goes with the next look.</summary>
    public Task PinAsync(string modelId, CancellationToken ct = default) => SetUpdatePolicyAsync(modelId, UpdatePolicy.Pin, ct);

    /// <summary>Forgets the notice the user dismissed for a model's newer upload, so it is news again.</summary>
    public Task DismissUpdateAsync(string modelId, string revision, CancellationToken ct = default) =>
        _updateState.ChangeAsync(s => s.Dismissed[modelId] = revision, ct);

    // ── Looking ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks the Hub whether each installed model's repo has a newer upload (D7 channel a): the commit moved AND a file of the model
    /// has a different sha256 (a commit that touched only the README is not news), then the catalog whether a newer model replaces
    /// it (channel b). A pinned model is not asked about. A repo that cannot be reached is skipped and counted; a rate limit ends the
    /// round with the wait the Hub asked for. Nothing is replaced.
    /// </summary>
    public async Task<UpdateCheck> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var catalog = State.Catalog?.Catalog;
        var installed = State.Installed;
        var found = new List<ModelUpdate>();
        var saved = await _updateState.SnapshotAsync(ct).ConfigureAwait(false);
        var learned = new List<(string Repo, string? ETag, string Sha)>();
        var unchanged = new Dictionary<string, string>(StringComparer.Ordinal);
        int checkedCount = 0, failed = 0, pinned = 0;
        string? problem = null;
        TimeSpan? retryAfter = null;
        foreach (var model in installed)
        {
            ct.ThrowIfCancellationRequested();
            if (model.Manifest is not { SourceRepo: { Length: > 0 } repo, Revision: { Length: > 0 } revision } manifest) continue;
            if (PolicyOf(model) == UpdatePolicy.Pin)
            {
                pinned++;
                continue;
            }

            checkedCount++;
            try
            {
                if (await FindNewUploadAsync(model, manifest, repo, revision, saved, learned, unchanged, ct).ConfigureAwait(false) is { } upload)
                {
                    found.Add(upload);
                    continue;
                }
            }
            catch (RateLimitedException ex)
            {
                problem = ex.Message;
                retryAfter = ex.RetryAfter;
                failed++;
                break;
            }
            catch (Exception ex) when (ex is HubException or HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                failed++;
                Log.Warn($"Update check for {repo}: {ex.Message}");
                continue;
            }

            if (catalog is not null && manifest.CatalogId is { } catalogId
                && catalog.Models.FirstOrDefault(c => c.Supersedes.Contains(catalogId, StringComparer.Ordinal)) is { } newer)
            {
                found.Add(new ModelUpdate(model.Id, UpdateKind.Superseded, $"Superseded by {newer.Name}") { ReplacementName = newer.Name, InstalledRevision = revision });
            }
        }

        await _updateState.ChangeAsync(s =>
        {
            foreach (var (repo, etag, sha) in learned)
            {
                if (etag is not null) s.ETags[repo] = etag;
                s.Shas[repo] = sha;
            }

            foreach (var (id, sha) in unchanged) s.Unchanged[id] = sha;
        }, CancellationToken.None).ConfigureAwait(false);

        Update(s => s with { Updates = found.ToDictionary(u => u.ModelId, StringComparer.Ordinal) });
        return new UpdateCheck(checkedCount, failed, found, problem) { Pinned = pinned, RetryAfter = retryAfter };
    }

    private async Task<ModelUpdate?> FindNewUploadAsync(
        StoredModel model, ModelManifest manifest, string repo, string revision, UpdateStateFile saved,
        List<(string Repo, string? ETag, string Sha)> learned, Dictionary<string, string> unchanged, CancellationToken ct)
    {
        saved.ETags.TryGetValue(repo, out var etag);
        var (latest, newTag) = await _hub.GetModelAsync(repo, etag, ct).ConfigureAwait(false);
        var sha = latest?.Sha ?? (saved.Shas.TryGetValue(repo, out var remembered) ? remembered : "");
        if (latest is not null) learned.Add((repo, newTag, latest.Sha));
        if (sha.Length == 0 || string.Equals(sha, revision, StringComparison.Ordinal)) return null;
        if (saved.Unchanged.TryGetValue(model.Id, out var same) && same == sha) return null;

        // The commit moved. It only matters when a file of this model has other contents (the hash the Hub lists is the file's own).
        var tree = await _hub.ListFilesAsync(repo, sha, ct).ConfigureAwait(false);
        var changed = new List<UpdateFile>();
        var blind = false;
        var missing = false;
        long oldSize = 0;
        foreach (var file in manifest.Files)
        {
            var node = tree.FirstOrDefault(t => string.Equals(t.Path, file.Path, StringComparison.Ordinal));
            if (node?.LfsSha256 is not { Length: > 0 } hash)
            {
                if (node is null) missing = true;
                else blind = true;
                continue;
            }

            if (file.Sha256 is null)
            {
                blind = true;
                continue;
            }

            if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                changed.Add(new UpdateFile(file.Path, node.Size, hash));
                oldSize += file.Size;
            }
        }

        // A model whose files cannot be compared is flagged on the commit alone, as before; one whose files vanished from the repo too.
        if (changed.Count == 0 && !missing && !blind)
        {
            unchanged[model.Id] = sha;
            return null;
        }

        var titles = new List<string>();
        try
        {
            foreach (var commit in await _hub.GetCommitsAsync(repo, sha, 12, ct).ConfigureAwait(false))
            {
                if (string.Equals(commit.Id, revision, StringComparison.Ordinal)) break;
                if (commit.Title.Length > 0) titles.Add(commit.Title);
            }
        }
        catch (Exception ex) when (ex is HubException or HttpRequestException or IOException or JsonException)
        {
            Log.Debug($"No commit titles for {repo}: {ex.Message}");
        }

        var when = latest?.LastModified is { } m ? $" ({m.LocalDateTime:d MMM yyyy})" : "";
        return new ModelUpdate(model.Id, UpdateKind.NewUpload, $"A newer upload is available{when}", sha)
        {
            InstalledRevision = revision,
            InstalledSize = oldSize,
            NewSize = changed.Sum(c => c.Size),
            Titles = titles,
            Files = missing ? [] : changed,
        };
    }

    /// <summary>
    /// Loads the catalog again (online), puts it in force, and works out which recommended models beat what the user has installed
    /// for the same job by a clear margin (D25: recommendations re-run when the catalog updates).
    /// </summary>
    public async Task<CatalogRefresh> RefreshCatalogAsync(CancellationToken ct = default)
    {
        var before = State.Catalog;
        var load = await _catalog.LoadAsync(allowNetwork: true, ct).ConfigureAwait(false);
        var hardware = State.Hardware ?? await Hardware.HardwareProbe.DetectAsync(ct).ConfigureAwait(false);
        var picks = Recommender.Recommend(hardware, load.Catalog);
        Update(s => s with { Catalog = load, Picks = picks });
        var changed = before is null || before.Catalog.Generated != load.Catalog.Generated;
        return new CatalogRefresh(changed, load.Note, BetterPicks(picks, load.Catalog));
    }

    private IReadOnlyList<Pick> BetterPicks(IReadOnlyList<Pick> picks, ModelCatalog catalog)
    {
        const int Margin = 5;
        var owned = State.Installed
            .Select(m => m.Manifest?.CatalogId is { } id ? catalog.Find(id) : catalog.Models.FirstOrDefault(c => string.Equals(c.Repo, m.Id, StringComparison.OrdinalIgnoreCase)))
            .OfType<CatalogModel>().ToList();
        if (owned.Count == 0) return [];
        var better = new List<Pick>();
        foreach (var pick in picks)
        {
            if (State.IsInstalled(pick.Model, pick.Tier)) continue;
            if (owned.Any(o => pick.Model.Supersedes.Contains(o.Id, StringComparer.Ordinal))) continue; // reported as "superseded" already
            var role = pick.Kind switch { PickKind.Coding => Role.Coding, PickKind.Fast => Role.Fast, _ => Role.Everyday };
            var best = owned.Select(o => o.Scores.For(role)).Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty(-1).Max();
            if (best >= 0 && (pick.Model.Scores.For(role) ?? 0) - best >= Margin) better.Add(pick);
        }

        return better;
    }

    // ── Applying ─────────────────────────────────────────────────────────────────────────────────

    private SemaphoreSlim GateFor(string modelId)
    {
        lock (_gate)
        {
            if (!_updateGates.TryGetValue(modelId, out var gate)) _updateGates[modelId] = gate = new SemaphoreSlim(1, 1);
            return gate;
        }
    }

    private string UpdateLockPath(string modelId) =>
        Path.Combine(_paths.Runtime, "update-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(modelId)))[..12] + ".lock");

    private bool InUse(StoredModel model) => Runtime.Models.Any(m =>
        (m.Id == model.Id || m.Id.StartsWith(model.Id + ":", StringComparison.Ordinal)) && m.State is RunState.Loaded or RunState.Loading or RunState.Sleeping);

    /// <summary>
    /// Replaces a model's files with the newer upload, safely: the new files are downloaded beside the old ones and checked against
    /// the Hub's hashes, swapped in, loaded and asked one question through the router; only when that works are the old files
    /// deleted. A load that fails or a first prompt that errors puts the old files back and says so (the outcome is
    /// <see cref="UpdateResult.RolledBack"/>, not an exception). <paramref name="background"/> leaves a model that is in use alone.
    /// </summary>
    public async Task<UpdateOutcome> UpdateModelAsync(string modelId, bool background = false, CancellationToken ct = default)
    {
        var model = State.Installed.FirstOrDefault(m => m.Id == modelId) ?? throw new FileNotFoundException($"{modelId} is not installed");
        var update = State.UpdateFor(modelId);
        if (update is not { Kind: UpdateKind.NewUpload })
        {
            await CheckForUpdatesAsync(ct).ConfigureAwait(false);
            update = State.UpdateFor(modelId);
        }

        if (update is not { Kind: UpdateKind.NewUpload }) return new UpdateOutcome(modelId, UpdateResult.NothingToDo, "It is up to date.");
        if (!update.CanApply)
        {
            return new UpdateOutcome(modelId, UpdateResult.NothingToDo, "The repository changed its files, so this one has to be installed again from the Browse page.", update.InstalledRevision, update.LatestRevision);
        }

        if (background && InUse(model)) return new UpdateOutcome(modelId, UpdateResult.Deferred, "It is loaded right now; it will be updated once it is not.", update.InstalledRevision, update.LatestRevision);

        var gate = GateFor(modelId);
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return new UpdateOutcome(modelId, UpdateResult.Deferred, "It is already being updated.");
        FileStream? processLock = null;
        try
        {
            try
            {
                Directory.CreateDirectory(_paths.Runtime);
                processLock = new FileStream(UpdateLockPath(modelId), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return new UpdateOutcome(modelId, UpdateResult.Deferred, "Another Wicked process is updating it.");
            }

            return await ApplyUpdateAsync(model, update, ct).ConfigureAwait(false);
        }
        finally
        {
            if (processLock is not null) await processLock.DisposeAsync().ConfigureAwait(false);
            gate.Release();
        }
    }

    private async Task<UpdateOutcome> ApplyUpdateAsync(StoredModel model, ModelUpdate update, CancellationToken ct)
    {
        var id = model.Id;
        var store = StoreFor(model);
        var repo = model.Manifest!.SourceRepo!;
        var to = update.LatestRevision!;
        var from = model.Manifest.Revision;
        var name = id[(id.IndexOf('/') + 1)..]; // not a path: a model id
        var staged = new List<(string Relative, string Final, string Staged, string Previous)>();
        Busy(id, true);
        try
        {
            // 1. Beside the old files: the new ones, verified against the Hub's hashes as they arrive.
            var total = update.Files.Sum(f => f.Size);
            long before = 0;
            foreach (var file in update.Files)
            {
                ct.ThrowIfCancellationRequested();
                var final = store.PathFor(id, file.Path);
                var stage = final + ".update";
                staged.Add((file.Path, final, stage, final + ".previous"));
                var request = new DownloadRequest(_hub.ResolveUrl(repo, to, file.Path), stage, file.Size, file.Sha256, Authenticated: Token is not null, Title: $"{name} (update)");
                var job = Downloads.Enqueue(request);
                var done = before;
                void OnChanged(object? sender, DownloadSnapshot s)
                {
                    if (s.Id != job || s.State is DownloadState.Paused or DownloadState.Cancelled) return;
                    Progress?.Invoke(new InstallProgress(id, $"Downloading the update of {name}", total > 0 ? (done + s.BytesDone) / (double)total : null));
                }

                Downloads.Changed += OnChanged;
                try
                {
                    var result = await Downloads.WaitAsync(job, ct).ConfigureAwait(false);
                    if (result.State == DownloadState.Cancelled) throw new OperationCanceledException();
                    if (result.State != DownloadState.Done) throw DownloadFailure(repo, result);
                }
                finally
                {
                    Downloads.Changed -= OnChanged;
                }

                before += file.Size;
            }

            // 2. The swap, written down first so a crash in the middle is finished or undone at the next start.
            var runIds = model.Variants.Where(v => v.Files.Any(f => update.Files.Any(u => SamePath(model, f.Path, u.Path)))).Select(v => v.Id).ToList();
            var wasLoaded = InUse(model);
            foreach (var runId in runIds) await Runtime.UnloadAsync(runId, ct).ConfigureAwait(false);
            var oldManifest = JsonSerializer.Serialize(model.Manifest, ManifestJson);
            await _updateState.ChangeJournalAsync(j =>
            {
                j.RemoveAll(e => e.ModelId == id);
                j.Add(new UpdateJournalEntry { ModelId = id, StoreRoot = store.Root, FromRevision = from, ToRevision = to, Files = [.. staged.Select(s => s.Relative)], OldManifest = oldManifest, WasLoaded = wasLoaded });
            }, CancellationToken.None).ConfigureAwait(false);
            Progress?.Invoke(new InstallProgress(id, $"Checking the update of {name}", null));
            string? verdict;
            try
            {
                foreach (var (_, final, stage, previous) in staged)
                {
                    if (File.Exists(final)) File.Move(final, previous, overwrite: true);
                    File.Move(stage, final, overwrite: true);
                }

                await store.RegisterAsync(id, repo, to, update.Files.Select(f => (f.Path, (string?)f.Sha256)), ct: CancellationToken.None).ConfigureAwait(false);
                await RescanAsync(CancellationToken.None).ConfigureAwait(false);

                // 3. The new files must load and answer one prompt through the router.
                verdict = await VerifyAsync(id, update, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or HubException or HttpRequestException)
            {
                // The files are swapped (or half swapped): what was cancelled or broke is undone, never left between the two.
                verdict = ex is OperationCanceledException ? "The update was cancelled." : ex.Message;
            }

            if (verdict is null) return await FinishAsync(id, name, staged, from, to, wasLoaded, runIds).ConfigureAwait(false);
            return await RollBackAsync(id, name, staged, oldManifest, store, from, to, verdict, runIds).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            DiscardStaged(staged);
            throw;
        }
        catch (Exception ex) when (ex is IOException or HubException or HttpRequestException)
        {
            // Failed before the swap or while undoing it: whatever was staged is removed, the old files were never touched.
            Log.Warn($"Updating {id} failed: {ex.Message}");
            DiscardStaged(staged);
            throw;
        }
        finally
        {
            Busy(id, false);
        }
    }

    private static readonly JsonSerializerOptions ManifestJson = new(JsonSerializerDefaults.Web);

    private static bool SamePath(StoredModel model, string absolute, string relative) =>
        string.Equals(Path.GetRelativePath(model.Directory, absolute).Replace(Path.DirectorySeparatorChar, '/'), relative, StringComparison.Ordinal);

    /// <summary>Null when the new files work; otherwise the sentence that says how they failed.</summary>
    private async Task<string?> VerifyAsync(string id, ModelUpdate update, CancellationToken ct)
    {
        var model = State.Installed.FirstOrDefault(m => m.Id == id);
        var variant = model?.Variants.FirstOrDefault(v => v.Files.Any(f => update.Files.Any(u => SamePath(model, f.Path, u.Path)))) ?? model?.Variants.FirstOrDefault();
        if (variant is null) return "The model has no file the engine can load.";
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(UpdateVerifyTimeout);
        try
        {
            await Runtime.LoadAsync(variant.Id, limit.Token).ConfigureAwait(false);
            var answered = false;
            await foreach (var chunk in Chat.StreamAsync(
                               new ChatRequest(variant.Id, [new ChatMessage(ChatRole.User, "Reply with one short word.")], new ChatSampling(Temperature: 0, MaxTokens: 8)), limit.Token).ConfigureAwait(false))
            {
                if (chunk is ContentDelta { Text.Length: > 0 } or ReasoningDelta { Text.Length: > 0 }) answered = true;
            }

            return answered ? null : "The model answered with nothing.";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "The model did not answer in time.";
        }
        catch (Exception ex) when (ex is ChatException or IOException or HttpRequestException)
        {
            return ex.Message.Split('\n')[0];
        }
    }

    /// <summary>How long the new files get to load and answer before they are judged failed.</summary>
    internal TimeSpan UpdateVerifyTimeout { get; set; } = TimeSpan.FromMinutes(10);

    private async Task<UpdateOutcome> FinishAsync(string id, string name, List<(string Relative, string Final, string Staged, string Previous)> staged, string? from, string to, bool wasLoaded, List<string> runIds)
    {
        foreach (var (_, _, _, previous) in staged) TryDelete(previous);
        await _updateState.ChangeJournalAsync(j => j.RemoveAll(e => e.ModelId == id), CancellationToken.None).ConfigureAwait(false);
        await _updateState.ChangeAsync(s =>
        {
            s.Dismissed.Remove(id);
            s.AutoFailed.Remove(id);
            s.Unchanged.Remove(id);
        }, CancellationToken.None).ConfigureAwait(false);
        if (!wasLoaded)
        {
            foreach (var runId in runIds) await Runtime.UnloadAsync(runId, CancellationToken.None).ConfigureAwait(false);
        }

        Update(s => s with { Updates = Without(s.Updates, id) });
        Log.Info($"Updated {id}: {from} -> {to}");
        Progress?.Invoke(new InstallProgress(id, $"{name} is updated", 1));
        return new UpdateOutcome(id, UpdateResult.Updated, $"{name} is updated and answered a test prompt.", from, to);
    }

    private async Task<UpdateOutcome> RollBackAsync(
        string id, string name, List<(string Relative, string Final, string Staged, string Previous)> staged, string? oldManifest, ModelStore store,
        string? from, string to, string reason, List<string> runIds)
    {
        foreach (var runId in runIds)
        {
            try
            {
                await Runtime.UnloadAsync(runId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ChatException ex)
            {
                Log.Warn($"Could not unload {runId} before the rollback: {ex.Message}");
            }
        }

        foreach (var (_, final, _, previous) in staged)
        {
            if (File.Exists(previous)) File.Move(previous, final, overwrite: true);
            else TryDelete(final);
        }

        if (oldManifest is not null && JsonSerializer.Deserialize<ModelManifest>(oldManifest, ManifestJson) is { } restored)
        {
            await store.WriteManifestAsync(id, restored, CancellationToken.None).ConfigureAwait(false);
        }

        await _updateState.ChangeJournalAsync(j => j.RemoveAll(e => e.ModelId == id), CancellationToken.None).ConfigureAwait(false);
        await _updateState.ChangeAsync(s => s.AutoFailed[id] = to, CancellationToken.None).ConfigureAwait(false);
        await RescanAsync(CancellationToken.None).ConfigureAwait(false);
        Log.Warn($"Update of {id} to {to} rolled back: {reason}");
        Progress?.Invoke(new InstallProgress(id, $"{name} was put back", null));
        return new UpdateOutcome(id, UpdateResult.RolledBack, $"The new {name} did not work ({reason}), so the old one is back.", from, to);
    }

    private static void DiscardStaged(List<(string Relative, string Final, string Staged, string Previous)> staged)
    {
        foreach (var (_, _, stage, _) in staged) TryDelete(stage);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not remove {path}: {ex.Message}");
        }
    }

    private static IReadOnlyDictionary<string, ModelUpdate>? Without(IReadOnlyDictionary<string, ModelUpdate>? updates, string id) =>
        updates?.Where(u => u.Key != id).ToDictionary(u => u.Key, u => u.Value, StringComparer.Ordinal);

    /// <summary>
    /// Finishes what a crash interrupted: an update whose swap was written down but never judged is judged now, by the same test,
    /// and rolled back when it fails.
    /// </summary>
    internal async Task RecoverUpdatesAsync(CancellationToken ct)
    {
        var pending = await _updateState.ReadJournalAsync(ct).ConfigureAwait(false);
        foreach (var entry in pending)
        {
            var model = State.Installed.FirstOrDefault(m => m.Id == entry.ModelId);
            if (model is null)
            {
                await _updateState.ChangeJournalAsync(j => j.RemoveAll(e => e.ModelId == entry.ModelId), ct).ConfigureAwait(false);
                continue;
            }

            var store = StoreFor(model);
            var staged = entry.Files.Select(f =>
            {
                var final = store.PathFor(entry.ModelId, f);
                return (Relative: f, Final: final, Staged: final + ".update", Previous: final + ".previous");
            }).ToList();
            var name = entry.ModelId[(entry.ModelId.IndexOf('/') + 1)..]; // not a path: a model id
            var update = new ModelUpdate(entry.ModelId, UpdateKind.NewUpload, "", entry.ToRevision) { Files = [.. entry.Files.Select(f => new UpdateFile(f, 0, ""))] };
            var runIds = model.Variants.Where(v => v.Files.Any(f => entry.Files.Any(u => SamePath(model, f.Path, u)))).Select(v => v.Id).ToList();
            Log.Warn($"Finishing the interrupted update of {entry.ModelId}");
            var verdict = staged.All(s => !File.Exists(s.Previous)) ? null : await VerifyAsync(entry.ModelId, update, ct).ConfigureAwait(false);
            if (verdict is null) await FinishAsync(entry.ModelId, name, staged, entry.FromRevision, entry.ToRevision ?? "", entry.WasLoaded, runIds).ConfigureAwait(false);
            else await RollBackAsync(entry.ModelId, name, staged, entry.OldManifest, store, entry.FromRevision, entry.ToRevision ?? "", verdict, runIds).ConfigureAwait(false);
        }
    }
}
