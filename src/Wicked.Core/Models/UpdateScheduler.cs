using System.Net.NetworkInformation;
using Wicked.Core.Engines;
using Wicked.Core.Hub;

namespace Wicked.Core.Models;

/// <summary>How the scheduler paces itself (D7). The defaults are the plan's: on start, then every 24 hours, jittered.</summary>
public sealed record SchedulerOptions
{
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>The share of the interval added or taken off at random, so many machines do not ask at the same second.</summary>
    public double Jitter { get; init; } = 0.1;

    /// <summary>How long after start the first look waits (the window is busy with its own start).</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The first wait after a refusal (429, 5xx, the network failing); it doubles with each in a row, up to <see cref="BackoffCap"/>.</summary>
    public TimeSpan BackoffBase { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan BackoffCap { get; init; } = TimeSpan.FromHours(6);

    /// <summary>How long to wait before looking again when there was no network.</summary>
    public TimeSpan NoNetworkRetry { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Models with the automatic policy are updated by the sweep that finds them.</summary>
    public bool ApplyAutomatic { get; init; } = true;
}

public enum SweepSkip
{
    /// <summary>Offline mode is on: no request is made.</summary>
    Offline,

    /// <summary>The machine has no network: no request is made.</summary>
    NoNetwork,

    /// <summary>The user turned update checks off.</summary>
    Disabled,

    /// <summary>Another Wicked process is looking right now.</summary>
    Busy,
}

/// <summary>What one look found: the models, the catalog, the engine and the app.</summary>
public sealed record SweepResult(
    DateTimeOffset At,
    SweepSkip? Skipped,
    UpdateCheck? Models,
    CatalogRefresh? Catalog,
    EngineUpdate? Engine,
    AppRelease? App,
    IReadOnlyList<UpdateOutcome> Applied,
    string? Problem,
    TimeSpan? RetryAfter)
{
    public static SweepResult Skip(SweepSkip why, DateTimeOffset at) => new(at, why, null, null, null, null, [], null, null);
}

/// <summary>
/// Keeps everything current (D7, D15, D25, D26): on start and then every day, jittered, it checks the models' repos, the catalog,
/// the engine and the app's releases, applies the updates of models set to automatic, and posts what is left for the user to the
/// notice board. Nothing is asked in offline mode or without a network; a refusal (429, 5xx) backs off, doubling each time and
/// never sooner than the server asked. "Check now" runs the same look at once. Every request goes through the services' own HTTP
/// client, so tests point the whole thing at loopback stand-ins.
/// </summary>
public sealed class UpdateScheduler : IAsyncDisposable
{
    private readonly ModelServices _services;
    private readonly Func<bool> _offline;
    private readonly Func<bool> _network;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _sweeping = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _applying = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private Task? _loop;
    private SweepResult? _last;
    private DateTimeOffset? _next;

    internal UpdateScheduler(ModelServices services, Func<bool> offline, bool loopback, TimeProvider? time = null)
    {
        _services = services;
        _offline = offline;
        _network = () => loopback || NetworkInterface.GetIsNetworkAvailable();
        _time = time ?? TimeProvider.System;
        services.Progress += OnProgress;
    }

    /// <summary>Where findings are posted; set by the window (the daemon keeps its own).</summary>
    public NoticeBoard? Notices { get; set; }

    /// <summary>Pacing; set before <see cref="Start"/>.</summary>
    public SchedulerOptions Options { get; set; } = new();

    /// <summary>Replaces the "is there a network" check (tests; the default asks the system).</summary>
    internal Func<bool>? NetworkAvailable { get; set; }

    public SweepResult? Last
    {
        get
        {
            lock (_gate) return _last;
        }
    }

    /// <summary>When the next scheduled look is due, or null before <see cref="Start"/>.</summary>
    public DateTimeOffset? NextRun
    {
        get
        {
            lock (_gate) return _next;
        }
    }

    /// <summary>Raised after each look, on a worker.</summary>
    public event Action<SweepResult>? Swept;

    /// <summary>Starts the daily rhythm (once; later calls do nothing).</summary>
    public void Start()
    {
        lock (_gate) _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    /// <summary>Looks now, whatever the schedule says (but never in offline mode, and never at all without a network).</summary>
    public Task<SweepResult> CheckNowAsync(CancellationToken ct = default) => SweepAsync(manual: true, ct);

    private async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        var wait = Jittered(Options.FirstDelay);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                lock (_gate) _next = _time.GetUtcNow() + wait;
                await Task.Delay(wait, _time, ct).ConfigureAwait(false);
                SweepResult result;
                try
                {
                    result = await SweepAsync(manual: false, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Warn($"The update look failed: {ex.Message}");
                    result = new SweepResult(_time.GetUtcNow(), null, null, null, null, null, [], ex.Message, null);
                }

                failures = result.Problem is not null ? failures + 1 : 0;
                wait = result.Skipped switch
                {
                    SweepSkip.NoNetwork => Options.NoNetworkRetry,
                    SweepSkip.Busy => TimeSpan.FromMinutes(1),
                    _ when failures > 0 => Backoff(failures, result.RetryAfter),
                    _ => Jittered(Options.Interval),
                };
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private TimeSpan Jittered(TimeSpan span)
    {
        var spread = span.TotalMilliseconds * Options.Jitter;
        return TimeSpan.FromMilliseconds(Math.Max(1, span.TotalMilliseconds + (Random.Shared.NextDouble() * 2 - 1) * spread));
    }

    private TimeSpan Backoff(int failures, TimeSpan? retryAfter)
    {
        var doubled = TimeSpan.FromTicks(Options.BackoffBase.Ticks * (1L << Math.Min(failures - 1, 20)));
        var wait = doubled > Options.BackoffCap ? Options.BackoffCap : doubled;
        return retryAfter is { } asked && asked > wait ? asked : wait;
    }

    private async Task<SweepResult> SweepAsync(bool manual, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_offline()) return Finish(SweepResult.Skip(SweepSkip.Offline, now));
        if (!(NetworkAvailable ?? _network)()) return Finish(SweepResult.Skip(SweepSkip.NoNetwork, now));
        if (!manual && !_services.UpdateSettings.ChecksEnabled) return Finish(SweepResult.Skip(SweepSkip.Disabled, now));
        // A look already under way in this process is waited for when asked by hand, and skipped when it is the schedule.
        if (manual) await _sweeping.WaitAsync(ct).ConfigureAwait(false);
        else if (!await _sweeping.WaitAsync(0, ct).ConfigureAwait(false)) return Finish(SweepResult.Skip(SweepSkip.Busy, now));
        FileStream? processLock = null;
        try
        {
            try
            {
                Directory.CreateDirectory(_services.RuntimeFolder);
                processLock = new FileStream(Path.Combine(_services.RuntimeFolder, "update-sweep.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                return Finish(SweepResult.Skip(SweepSkip.Busy, now));
            }

            return Finish(await LookAsync(ct).ConfigureAwait(false));
        }
        finally
        {
            if (processLock is not null) await processLock.DisposeAsync().ConfigureAwait(false);
            _sweeping.Release();
        }
    }

    private SweepResult Finish(SweepResult result)
    {
        if (result.Skipped is null) lock (_gate) _last = result;
        try
        {
            Swept?.Invoke(result);
        }
        catch (Exception ex)
        {
            Log.Warn($"An update listener threw: {ex.Message}");
        }

        return result;
    }

    private async Task<SweepResult> LookAsync(CancellationToken ct)
    {
        var at = _time.GetUtcNow();
        string? problem = null;
        TimeSpan? retryAfter = null;

        var models = await _services.CheckForUpdatesAsync(ct).ConfigureAwait(false);
        if (models.Problem is not null)
        {
            problem = models.Problem;
            retryAfter = models.RetryAfter;
        }
        else if (models.Checked > 0 && models.Failed == models.Checked)
        {
            problem = "None of the model repositories could be reached.";
        }

        CatalogRefresh? catalog = null;
        try
        {
            catalog = await _services.RefreshCatalogAsync(ct).ConfigureAwait(false);
            if (catalog.Note is not null) problem ??= catalog.Note;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            Log.Warn($"Catalog refresh: {ex.Message}");
            problem ??= ex.Message;
        }

        EngineUpdate? engine = null;
        if (_services.Engines.Current is not null)
        {
            try
            {
                engine = await _services.CheckEngineUpdateAsync(ct).ConfigureAwait(false);
            }
            catch (GitHubRateLimitException ex)
            {
                problem ??= ex.Message;
                retryAfter ??= ex.ResetsAt - _time.GetUtcNow();
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
            {
                Log.Warn($"Engine check: {ex.Message}");
                problem ??= ex.Message;
            }
        }

        AppRelease? app = null;
        try
        {
            if (await _services.AppReleases.LatestAsync(ct).ConfigureAwait(false) is { } release && AppReleases.IsNewer(release, BuildInfo.Version)) app = release;
        }
        catch (RateLimitedException ex)
        {
            problem ??= ex.Message;
            retryAfter ??= ex.RetryAfter;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException)
        {
            Log.Warn($"App release check: {ex.Message}");
            problem ??= ex.Message;
        }

        var applied = Options.ApplyAutomatic ? await ApplyAutomaticAsync(models, ct).ConfigureAwait(false) : [];
        var saved = await _services.UpdateStateStore.SnapshotAsync(ct).ConfigureAwait(false);
        Post(models, catalog, engine, app, applied, saved);
        await _services.UpdateStateStore.ChangeAsync(s =>
        {
            s.LastSweep = at;
            if (catalog is not null) s.CatalogGenerated = _services.State.Catalog?.Catalog.Generated.ToString("O");
        }, CancellationToken.None).ConfigureAwait(false);
        return new SweepResult(at, null, models, catalog, engine, app, applied, problem, retryAfter);
    }

    private async Task<IReadOnlyList<UpdateOutcome>> ApplyAutomaticAsync(UpdateCheck models, CancellationToken ct)
    {
        var saved = await _services.UpdateStateStore.SnapshotAsync(ct).ConfigureAwait(false);
        var outcomes = new List<UpdateOutcome>();
        foreach (var update in models.Found.Where(u => u.CanApply))
        {
            ct.ThrowIfCancellationRequested();
            var model = _services.State.Installed.FirstOrDefault(m => m.Id == update.ModelId);
            if (model is null || _services.PolicyOf(model) != UpdatePolicy.Auto) continue;
            if (saved.AutoFailed.TryGetValue(update.ModelId, out var failed) && failed == update.LatestRevision) continue; // tried and rolled back: the user decides now
            lock (_gate) _applying.Add(update.ModelId);
            try
            {
                outcomes.Add(await _services.UpdateModelAsync(update.ModelId, background: true, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is IOException or HubException or HttpRequestException or FileNotFoundException)
            {
                Log.Warn($"Automatic update of {update.ModelId}: {ex.Message}");
                outcomes.Add(new UpdateOutcome(update.ModelId, UpdateResult.NothingToDo, $"The automatic update could not run: {ex.Message}"));
            }
            finally
            {
                lock (_gate) _applying.Remove(update.ModelId);
                Notices?.Clear(ProgressKey(update.ModelId));
            }
        }

        return outcomes;
    }

    private static string ProgressKey(string modelId) => "update-progress:" + modelId;

    private void OnProgress(InstallProgress progress)
    {
        bool mine;
        lock (_gate) mine = _applying.Contains(progress.ModelId);
        if (mine) Notices?.Post(new Notice(ProgressKey(progress.ModelId), progress.Message, NoticeKind.Progress, ShellRoutePanelModels, progress.Fraction));
    }

    /// <summary>The route of the Models panel (the app's <c>ShellRoutes.Models</c>, which Core cannot see).</summary>
    public const string ShellRoutePanelModels = "panel/models";

    public const string ShellRouteRuntime = "settings/runtime";

    public const string UrlRoutePrefix = "url/";

    private static readonly NoticeAction[] ModelActions =
    [
        new("update", "Update"),
        new("dismiss", "Dismiss"),
        new("mute", "Don't tell me about this model"),
    ];

    private static readonly NoticeAction[] SupersededActions = [new("dismiss", "Dismiss"), new("mute", "Don't tell me about this model")];

    private void Post(UpdateCheck models, CatalogRefresh? catalog, EngineUpdate? engine, AppRelease? app, IReadOnlyList<UpdateOutcome> applied, UpdateStateFile saved)
    {
        if (Notices is not { } board) return;
        var found = models.Found.ToDictionary(u => u.ModelId, StringComparer.Ordinal);
        foreach (var existing in board.All.Where(n => n.Key.StartsWith("model-update:", StringComparison.Ordinal) && n.Kind == NoticeKind.Action))
        {
            if (!found.ContainsKey(existing.Key["model-update:".Length..])) board.Clear(existing.Key);
        }

        foreach (var outcome in applied)
        {
            var key = "model-update:" + outcome.ModelId;
            switch (outcome.Result)
            {
                case UpdateResult.Updated:
                    board.Post(new Notice(key, outcome.Message, NoticeKind.Success, ShellRoutePanelModels));
                    break;
                case UpdateResult.RolledBack:
                    board.Post(new Notice(key, outcome.Message, NoticeKind.Error, ShellRoutePanelModels, Actions: [new("dismiss", "Dismiss")]));
                    break;
            }
        }

        var handled = applied.Where(a => a.Result is UpdateResult.Updated or UpdateResult.RolledBack).Select(a => a.ModelId).ToHashSet(StringComparer.Ordinal);
        foreach (var update in models.Found)
        {
            if (handled.Contains(update.ModelId)) continue;
            var model = _services.State.Installed.FirstOrDefault(m => m.Id == update.ModelId);
            var name = update.ModelId[(update.ModelId.IndexOf('/') + 1)..]; // not a path: a model id
            if (saved.Dismissed.TryGetValue(update.ModelId, out var dismissed) && dismissed == (update.LatestRevision ?? update.Detail))
            {
                board.Clear("model-update:" + update.ModelId);
                continue;
            }

            if (model is not null && _services.PolicyOf(model) == UpdatePolicy.Auto && update.CanApply && !saved.AutoFailed.ContainsKey(update.ModelId))
            {
                continue; // set to automatic: the next quiet moment updates it, nothing to ask
            }

            var text = update.Kind == UpdateKind.Superseded ? $"{name}: {update.Detail.ToLowerInvariant()}" : $"{name}: {update.Detail.ToLowerInvariant()}";
            board.Post(new Notice("model-update:" + update.ModelId, text, NoticeKind.Action, ShellRoutePanelModels, Actions: update.Kind == UpdateKind.Superseded || !update.CanApply ? SupersededActions : ModelActions));
        }

        if (engine is not null)
        {
            board.Post(new Notice("engine-update", $"A newer llama.cpp engine is available ({engine.Release.Tag})", NoticeKind.Action, ShellRouteRuntime, Actions: [new("update", "Update the engine"), new("dismiss", "Dismiss")]));
        }
        else
        {
            board.Clear("engine-update");
        }

        if (app is not null && saved.DismissedApp != app.Version)
        {
            board.Post(new Notice("app-update", $"Wicked {app.Version} is available", NoticeKind.Action, UrlRoutePrefix + app.Url, Actions: [new("notes", "Open release notes"), new("dismiss", "Dismiss")]));
        }
        else
        {
            board.Clear("app-update");
        }

        if (catalog is not null)
        {
            foreach (var pick in catalog.Better.Where(p => !saved.DismissedPicks.Contains(p.Model.Id)))
            {
                board.Post(new Notice("better-pick:" + pick.Model.Id, $"A better pick for this PC: {pick.Model.Name} ({pick.Kind.ToString().ToLowerInvariant()})", NoticeKind.Action, ShellRoutePanelModels, Actions: [new("dismiss", "Dismiss")]));
            }
        }
    }

    /// <summary>
    /// Does what a notice's button says: <c>update</c> (a model, or the engine), <c>dismiss</c> (this version only), <c>mute</c>
    /// ("don't tell me about this model": pins it). Returns the outcome of an update, else null. The app's own actions (open the
    /// release notes) belong to the window.
    /// </summary>
    public async Task<UpdateOutcome?> PerformAsync(string noticeKey, string actionId, CancellationToken ct = default)
    {
        var board = Notices;
        if (noticeKey.StartsWith("model-update:", StringComparison.Ordinal))
        {
            var id = noticeKey["model-update:".Length..];
            switch (actionId)
            {
                case "update":
                    lock (_gate) _applying.Add(id);
                    try
                    {
                        var outcome = await _services.UpdateModelAsync(id, background: false, ct).ConfigureAwait(false);
                        var kind = outcome.Result switch { UpdateResult.Updated => NoticeKind.Success, UpdateResult.RolledBack => NoticeKind.Error, _ => NoticeKind.Info };
                        board?.Post(new Notice(noticeKey, outcome.Message, kind, ShellRoutePanelModels, Actions: kind == NoticeKind.Error ? [new("dismiss", "Dismiss")] : null));
                        return outcome;
                    }
                    finally
                    {
                        lock (_gate) _applying.Remove(id);
                        board?.Clear(ProgressKey(id));
                    }

                case "dismiss":
                    if (_services.State.UpdateFor(id) is { } shown) await _services.DismissUpdateAsync(id, shown.LatestRevision ?? shown.Detail, ct).ConfigureAwait(false);
                    board?.Clear(noticeKey);
                    return null;
                case "mute":
                    await _services.PinAsync(id, ct).ConfigureAwait(false);
                    board?.Clear(noticeKey);
                    return null;
            }
        }
        else if (noticeKey == "engine-update")
        {
            if (actionId == "update" && await _services.CheckEngineUpdateAsync(ct).ConfigureAwait(false) is { } update)
            {
                await _services.UpdateEngineAsync(update, ct).ConfigureAwait(false);
                board?.Post(new Notice(noticeKey, $"The engine is now {update.Release.Tag}", NoticeKind.Success, ShellRouteRuntime));
                return null;
            }

            board?.Clear(noticeKey);
        }
        else if (noticeKey == "app-update" && actionId == "dismiss")
        {
            if (Last?.App is { } release) await _services.UpdateStateStore.ChangeAsync(s => s.DismissedApp = release.Version, ct).ConfigureAwait(false);
            board?.Clear(noticeKey);
        }
        else if (noticeKey.StartsWith("better-pick:", StringComparison.Ordinal) && actionId == "dismiss")
        {
            var id = noticeKey["better-pick:".Length..];
            await _services.UpdateStateStore.ChangeAsync(s =>
            {
                if (!s.DismissedPicks.Contains(id)) s.DismissedPicks.Add(id);
            }, ct).ConfigureAwait(false);
            board?.Clear(noticeKey);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _services.Progress -= OnProgress;
        await _stop.CancelAsync().ConfigureAwait(false);
        Task? loop;
        lock (_gate) loop = _loop;
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }

        _stop.Dispose();
        _sweeping.Dispose();
    }
}
