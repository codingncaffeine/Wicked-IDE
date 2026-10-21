using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Wicked.Contracts;
using Wicked.Core.Engines;
using Wicked.Core.Chat;
using Wicked.Core.Hub;
using Wicked.Core.Models;
using Wicked.Daemon.Api;

namespace Wicked.Daemon;

/// <summary>
/// What the window and the command line ask of the daemon over its socket: its state, the installed models and which are loaded,
/// pulls with their progress, updates, the request log, and a way to stop it. Only this user can reach the socket, so none of it
/// is checked against API keys.
/// </summary>
internal static class ControlApi
{
    public static void Map(WebApplication app, DaemonHost host)
    {
        app.MapGet("/v1/wicked/status", async () => Results.Json(await host.StatusAsync().ConfigureAwait(false), WickedJson.Default.DaemonStatus));
        app.MapPost("/v1/wicked/apply", async () =>
        {
            await host.ApplyAsync().ConfigureAwait(false);
            return Results.Json(await host.StatusAsync().ConfigureAwait(false), WickedJson.Default.DaemonStatus);
        });
        app.MapPost("/v1/wicked/shutdown", () =>
        {
            host.RequestShutdown();
            return Results.Accepted();
        });

        app.MapGet("/v1/wicked/models", async Task<IResult> (HttpContext http) => Results.Json(await ModelsAsync(host, onlyLoaded: false, http.RequestAborted).ConfigureAwait(false), WickedJson.Default.ModelInfoArray));
        app.MapGet("/v1/wicked/ps", async Task<IResult> (HttpContext http) => Results.Json(await ModelsAsync(host, onlyLoaded: true, http.RequestAborted).ConfigureAwait(false), WickedJson.Default.ModelInfoArray));
        app.MapPost("/v1/wicked/load", async (HttpContext http, LoadRequest request) => await LoadAsync(host, request.Id, load: true, http.RequestAborted).ConfigureAwait(false));
        app.MapPost("/v1/wicked/unload", async (HttpContext http, UnloadRequest request) => await UnloadAsync(host, request.Id, http.RequestAborted).ConfigureAwait(false));
        app.MapPost("/v1/wicked/pull", (HttpContext http, PullRequest request) => PullAsync(http, host, request));

        app.MapGet("/v1/wicked/updates", async (HttpContext http) =>
        {
            await host.Ready.WaitAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Json(Report(host, host.Services.Updates.Last), WickedJson.Default.UpdateReport);
        });
        app.MapPost("/v1/wicked/updates/check", async (HttpContext http) =>
        {
            await host.Ready.WaitAsync(http.RequestAborted).ConfigureAwait(false);
            var sweep = await host.Services.Updates.CheckNowAsync(http.RequestAborted).ConfigureAwait(false);
            return Results.Json(Report(host, sweep), WickedJson.Default.UpdateReport);
        });
        app.MapPost("/v1/wicked/updates/apply", async (HttpContext http, UpdateRequest request) => await ApplyUpdatesAsync(host, request, http.RequestAborted).ConfigureAwait(false));

        app.MapGet("/v1/wicked/log", (long? after, int? limit) => Results.Json(host.Log.Since(after ?? 0, Math.Clamp(limit ?? 500, 1, 1000)).ToArray(), WickedJson.Default.ApiLogEntryArray));
        app.MapGet("/v1/wicked/log/stream", async (HttpContext http, long? after) =>
        {
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            var seen = after ?? long.MaxValue;
            try
            {
                // Follow first, then replay what the client has not seen, so nothing falls between the two.
                await using var live = host.Log.FollowAsync(http.RequestAborted).GetAsyncEnumerator(http.RequestAborted);
                var next = live.MoveNextAsync().AsTask();
                if (after is not null)
                {
                    foreach (var entry in host.Log.Since(after.Value)) await SendAsync(http, JsonSerializer.Serialize(entry, WickedJson.Default.ApiLogEntry)).ConfigureAwait(false);
                    seen = host.Log.Since(after.Value).LastOrDefault()?.Id ?? after.Value;
                }

                await http.Response.WriteAsync(": following\n\n", http.RequestAborted).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
                while (await next.ConfigureAwait(false))
                {
                    var entry = live.Current;
                    next = live.MoveNextAsync().AsTask();
                    if (entry.Id > seen || after is null) await SendAsync(http, JsonSerializer.Serialize(entry, WickedJson.Default.ApiLogEntry)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // The viewer went away.
            }
        });
    }

    private static async Task SendAsync(HttpContext http, string data)
    {
        await http.Response.WriteAsync($"data: {data}\n\n", http.RequestAborted).ConfigureAwait(false);
        await http.Response.Body.FlushAsync(http.RequestAborted).ConfigureAwait(false);
    }

    private static async Task<ModelInfo[]> ModelsAsync(DaemonHost host, bool onlyLoaded, CancellationToken ct)
    {
        await host.Ready.WaitAsync(ct).ConfigureAwait(false);
        var services = host.Services;
        await services.Runtime.AttachExistingAsync(ct).ConfigureAwait(false);
        var running = services.Runtime.Models;
        var list = new List<ModelInfo>();
        foreach (var model in services.State.Installed)
        {
            foreach (var variant in model.Variants)
            {
                var live = running.FirstOrDefault(r => r.Id == variant.Id);
                var state = (live?.State ?? RunState.Unloaded).ToString().ToLowerInvariant();
                if (onlyLoaded && live?.State is not (RunState.Loaded or RunState.Loading or RunState.Sleeping)) continue;
                list.Add(new ModelInfo(variant.Id, live?.DisplayName ?? variant.Id, state, variant.Size, model.Manifest?.CatalogId, live?.ContextSize, live?.Error));
            }
        }

        return [.. list];
    }

    private static async Task<IResult> LoadAsync(DaemonHost host, string id, bool load, CancellationToken ct)
    {
        await host.Ready.WaitAsync(ct).ConfigureAwait(false);
        var runId = ModelResolver.Resolve(id, host.Services.State, host.Services.Runtime.Models);
        if (runId is null) return Results.NotFound(new { error = $"{id} is not installed" });
        try
        {
            await host.Services.Runtime.LoadAsync(runId, ct).ConfigureAwait(false);
            return Results.Ok(new { id = runId });
        }
        catch (ChatException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: 503);
        }
    }

    private static async Task<IResult> UnloadAsync(DaemonHost host, string? id, CancellationToken ct)
    {
        await host.Ready.WaitAsync(ct).ConfigureAwait(false);
        var runtime = host.Services.Runtime;
        await runtime.AttachExistingAsync(ct).ConfigureAwait(false);
        List<string>? targets = id is null
            ? [.. runtime.Models.Where(m => m.State is RunState.Loaded or RunState.Loading or RunState.Sleeping).Select(m => m.Id)]
            : ModelResolver.Resolve(id, host.Services.State, runtime.Models) is { } one ? [one] : null;
        if (targets is null) return Results.NotFound(new { error = $"{id} is not installed" });
        foreach (var target in targets) await runtime.UnloadAsync(target, ct).ConfigureAwait(false);
        return Results.Ok(new { unloaded = targets });
    }

    private static UpdateReport Report(DaemonHost host, SweepResult? sweep)
    {
        var services = host.Services;
        var models = (services.State.Updates?.Values ?? (IEnumerable<ModelUpdate>)[]).OrderBy(u => u.ModelId, StringComparer.Ordinal).Select(u =>
        {
            var model = services.State.Installed.FirstOrDefault(m => m.Id == u.ModelId);
            return new UpdateInfo(u.ModelId, u.Kind.ToString(), u.Detail, u.InstalledRevision, u.LatestRevision, u.NewSize, u.Titles, u.CanApply,
                model is null ? "notify" : UpdatePolicies.Name(services.PolicyOf(model)));
        }).ToArray();
        return new UpdateReport(models, sweep?.Engine?.Release.Tag, sweep?.App?.Version, sweep?.Skipped?.ToString(), sweep?.Problem);
    }

    private static async Task<IResult> ApplyUpdatesAsync(DaemonHost host, UpdateRequest request, CancellationToken ct)
    {
        await host.Ready.WaitAsync(ct).ConfigureAwait(false);
        var services = host.Services;
        var wanted = request.All
            ? (services.State.Updates?.Values ?? (IEnumerable<ModelUpdate>)[]).Where(u => u.CanApply).Select(u => u.ModelId).ToList()
            : request.Model is { } one && services.State.Installed.FirstOrDefault(m => m.Id == one || m.Id.EndsWith("/" + one, StringComparison.OrdinalIgnoreCase)) is { } found ? [found.Id] : null; // not a path: a model id
        if (wanted is null) return Results.NotFound(new { error = $"{request.Model} is not installed" });
        var results = new List<UpdateResultInfo>();
        foreach (var id in wanted)
        {
            try
            {
                var outcome = await services.UpdateModelAsync(id, background: false, ct).ConfigureAwait(false);
                results.Add(new UpdateResultInfo(id, outcome.Result switch { UpdateResult.RolledBack => "rolledBack", UpdateResult.NothingToDo => "nothingToDo", UpdateResult.Deferred => "deferred", _ => "updated" }, outcome.Message));
            }
            catch (Exception ex) when (ex is IOException or HubException or HttpRequestException or FileNotFoundException)
            {
                results.Add(new UpdateResultInfo(id, "failed", ex.Message));
            }
        }

        return Results.Json(results.ToArray(), WickedJson.Default.UpdateResultInfoArray);
    }

    // ── pull ─────────────────────────────────────────────────────────────────────────────────────

    private static async Task PullAsync(HttpContext http, DaemonHost host, PullRequest request)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        var ct = http.RequestAborted;
        async Task Send(PullEvent ev)
        {
            await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(ev, WickedJson.Default.PullEvent)}\n\n", ct).ConfigureAwait(false);
            await http.Response.Body.FlushAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await host.Ready.WaitAsync(ct).ConfigureAwait(false);
            var services = host.Services;
            var id = request.Id.Trim();
            var colon = id.LastIndexOf(':');
            var quant = colon > 0 ? id[(colon + 1)..] : null;
            var name = quant is null ? id : id[..colon];
            var catalog = services.State.Catalog?.Catalog;
            var model = catalog?.Models.FirstOrDefault(m => string.Equals(m.Id, name, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Repo, name, StringComparison.OrdinalIgnoreCase));
            string repo;
            string title;
            Func<bool, Task> install;
            string? licenceId;
            string? licenceUrl;
            if (model is not null)
            {
                var tier = quant is null ? ModelFit.DefaultTier(model, services.State.Hardware) : model.Tiers.FirstOrDefault(t => string.Equals(t.Quant, quant, StringComparison.OrdinalIgnoreCase));
                if (tier is null)
                {
                    await Send(new PullEvent("error", $"{model.Name} has no {quant} build. It comes in {string.Join(", ", model.Tiers.Select(t => t.Quant))}.", null, true, "unknown quant")).ConfigureAwait(false);
                    return;
                }

                repo = model.Repo;
                title = $"{model.Name} {tier.Quant}";
                licenceId = model.LicenseId;
                licenceUrl = model.LicenseUrl;
                install = accepted => services.InstallCatalogAsync(model, tier, accepted, ct);
            }
            else if (name.Count(c => c == '/') == 1)
            {
                var detail = await services.GetHubRepoAsync(name, ct).ConfigureAwait(false);
                var variant = quant is null
                    ? detail.Variants.FirstOrDefault(v => string.Equals(v.Quant, "Q4_K_M", StringComparison.OrdinalIgnoreCase)) ?? detail.Variants.FirstOrDefault()
                    : detail.Variants.FirstOrDefault(v => string.Equals(v.Quant, quant, StringComparison.OrdinalIgnoreCase));
                if (variant is null)
                {
                    await Send(new PullEvent("error", detail.Variants.Count == 0 ? $"{name} has no GGUF files." : $"{name} has no {quant} build. It comes in {string.Join(", ", detail.Variants.Select(v => v.Quant))}.", null, true, "unknown quant")).ConfigureAwait(false);
                    return;
                }

                repo = name;
                title = $"{name[(name.IndexOf('/') + 1)..]} {variant.Quant}"; // not a path: a repo id
                licenceId = detail.Model.License;
                licenceUrl = null;
                install = accepted => services.InstallFromHubAsync(name, variant, detail.Model, accepted, ct);
            }
            else
            {
                await Send(new PullEvent("error", $"'{id}' is not a catalog id or a Hugging Face repo (publisher/name).", null, true, "unknown model")).ConfigureAwait(false);
                return;
            }

            if (!request.AcceptLicence && await services.LicenceNeededAsync(repo, title, licenceId, licenceUrl, repo, ct).ConfigureAwait(false) is { } needed)
            {
                await Send(new PullEvent("licence", $"{needed.ModelName} is released under the {needed.LicenseId} licence, which has terms of use. Read them at {needed.Url}.", null, true, "licence")).ConfigureAwait(false);
                return;
            }

            await Send(new PullEvent("start", $"Pulling {title}", 0, false, null)).ConfigureAwait(false);
            var queue = System.Threading.Channels.Channel.CreateUnbounded<InstallProgress>();
            void OnProgress(InstallProgress p)
            {
                if (p.ModelId == repo || p.ModelId == "engine") queue.Writer.TryWrite(p);
            }

            services.Progress += OnProgress;
            var work = Task.Run(async () =>
            {
                try
                {
                    await install(request.AcceptLicence).ConfigureAwait(false);
                }
                finally
                {
                    queue.Writer.TryComplete();
                }
            }, CancellationToken.None);
            try
            {
                var last = DateTime.MinValue;
                await foreach (var p in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    // About four a second is plenty for a progress bar.
                    if (DateTime.UtcNow - last < TimeSpan.FromMilliseconds(250) && p.Fraction is < 1) continue;
                    last = DateTime.UtcNow;
                    await Send(new PullEvent(p.ModelId == "engine" ? "engine" : "download", p.Message, p.Fraction, false, null)).ConfigureAwait(false);
                }

                await work.ConfigureAwait(false);
                await Send(new PullEvent("done", $"{title} is installed", 1, true, null)).ConfigureAwait(false);
            }
            finally
            {
                services.Progress -= OnProgress;
            }
        }
        catch (OperationCanceledException)
        {
            // The caller hung up: the install it started is cancelled with the request.
        }
        catch (Exception ex) when (ex is IOException or HubException or HttpRequestException or LicenceNotAcceptedException or EngineInstallException)
        {
            if (!http.Response.HasStarted || !ct.IsCancellationRequested) await Send(new PullEvent("error", ex.Message, null, true, ex.GetType().Name)).ConfigureAwait(false);
        }
    }
}
