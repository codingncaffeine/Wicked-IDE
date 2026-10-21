using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Wicked.Tests.Models;

/// <summary>A repo on the stand-in Hub.</summary>
internal sealed class HubRepo(string id, string sha)
{
    public string Id { get; } = id;

    public string Sha { get; set; } = sha;

    public string? License { get; init; } = "apache-2.0";

    /// <summary>null: public. "auto"/"manual": downloads need the stand-in's token.</summary>
    public string? Gated { get; init; }

    public long Downloads { get; init; } = 1000;

    public Dictionary<string, byte[]> Files { get; } = [];

    /// <summary>Make the info call itself answer this status (404 for a repo that vanished).</summary>
    public int InfoStatus { get; set; }

    /// <summary>Sent as Retry-After with a 429.</summary>
    public int RetryAfterSeconds { get; set; }

    /// <summary>Answer the info call with an ETag and a 304 to a request that carries it.</summary>
    public bool ETags { get; set; }

    /// <summary>The repo's commits, newest first: id and the first line of the message.</summary>
    public List<(string Id, string Title)> Commits { get; } = [];

    /// <summary>A new upload: the commit moves and one file has other contents.</summary>
    public void Upload(string sha, string file, byte[] bytes, string title)
    {
        Files[file] = bytes;
        Commits.Insert(0, (sha, title));
        Sha = sha;
    }
}

/// <summary>
/// The corner of Hugging Face the model half uses: search, repo info, the file tree, and file downloads with Range and ETag
/// (a slow mode for pause and cancel tests). Gated repos answer 401 without a token and 403 with the wrong one.
/// </summary>
internal sealed class HubStandIn : IAsyncDisposable
{
    private readonly LocalServer _server;
    private readonly List<HubRepo> _repos;

    private HubStandIn(LocalServer server, List<HubRepo> repos)
    {
        _server = server;
        _repos = repos;
    }

    /// <summary>A repo of this stand-in, to change its commit or files while a test runs.</summary>
    public HubRepo Repo(string id) => Find(id) ?? throw new KeyNotFoundException(id);

    public Uri BaseAddress => _server.BaseAddress;

    public LocalServer Server => _server;

    /// <summary>The only token the stand-in accepts for gated files.</summary>
    public string AcceptedToken { get; set; } = "hf_good";

    /// <summary>Pause this long after every 64 KB of a file, so a download is slow enough to pause, resume and cancel.</summary>
    public TimeSpan ChunkDelay { get; set; }

    public List<string> Requests { get; } = [];

    public List<string?> Authorizations { get; } = [];

    /// <summary>How many info calls were answered 304 because the caller sent the current ETag.</summary>
    public int NotModified;

    /// <summary>How many of the recorded requests were for 'path' (an exact path, query ignored).</summary>
    public int CountOf(string path)
    {
        lock (Requests) return Requests.Count(r => r.Split('?')[0] == path);
    }

    public static async Task<HubStandIn> StartAsync(Action<WebApplication>? more = null, params HubRepo[] repos)
    {
        HubStandIn? self = null;
        var list = repos.ToList();
        var server = await LocalServer.StartAsync(app =>
        {
            app.MapGet("/api/models", (HttpContext c) => self!.Search(c));
            app.MapGet("/api/models/{owner}/{name}", (HttpContext c, string owner, string name) => self!.Info(c, $"{owner}/{name}"));
            app.MapGet("/api/models/{owner}/{name}/tree/{rev}", (HttpContext c, string owner, string name) => self!.Tree(c, $"{owner}/{name}"));
            app.MapGet("/api/models/{owner}/{name}/commits/{rev}", (HttpContext c, string owner, string name) => self!.CommitList(c, $"{owner}/{name}"));
            app.MapGet("/{owner}/{name}/resolve/{rev}/{**file}", (HttpContext c, string owner, string name, string file) => self!.ServeAsync(c, $"{owner}/{name}", file));
            more?.Invoke(app);
        });
        self = new HubStandIn(server, list);
        return self;
    }

    public HubRepo Add(HubRepo repo)
    {
        lock (_repos) _repos.Add(repo);
        return repo;
    }

    private HubRepo? Find(string id)
    {
        lock (_repos) return _repos.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private void Note(HttpContext c)
    {
        lock (Requests)
        {
            Requests.Add(c.Request.Path + c.Request.QueryString);
            Authorizations.Add(c.Request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null);
        }
    }

    private IResult Search(HttpContext c)
    {
        Note(c);
        var query = c.Request.Query["search"].ToString();
        HubRepo[] hits;
        lock (_repos) hits = [.. _repos.Where(r => r.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderByDescending(r => r.Downloads)];
        return Results.Text(JsonSerializer.Serialize(hits.Select(r => new
        {
            id = r.Id,
            downloads = r.Downloads,
            likes = 5,
            createdAt = "2026-01-01T00:00:00.000Z",
            lastModified = "2026-10-01T00:00:00.000Z",
            gated = r.Gated is null ? (object)false : r.Gated,
            tags = new[] { "gguf", "license:" + r.License },
        })), "application/json");
    }

    private IResult Info(HttpContext c, string id)
    {
        Note(c);
        if (Find(id) is not { } repo) return Results.NotFound();
        if (repo.InfoStatus == 429 && repo.RetryAfterSeconds > 0) c.Response.Headers.RetryAfter = repo.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        if (repo.InfoStatus != 0) return Results.StatusCode(repo.InfoStatus);
        if (repo.ETags)
        {
            var tag = $"\"info-{repo.Sha}\"";
            c.Response.Headers.ETag = tag;
            if (c.Request.Headers.IfNoneMatch.ToString() == tag)
            {
                Interlocked.Increment(ref NotModified);
                return Results.StatusCode(304);
            }
        }

        return Results.Text(JsonSerializer.Serialize(new
        {
            id = repo.Id,
            sha = repo.Sha,
            lastModified = "2026-10-01T00:00:00.000Z",
            gated = repo.Gated is null ? (object)false : repo.Gated,
            downloads = repo.Downloads,
            likes = 5,
            tags = new[] { "gguf", "license:" + repo.License },
            cardData = new { license = repo.License },
            siblings = repo.Files.Keys.Select(k => new { rfilename = k }),
        }), "application/json");
    }

    private IResult CommitList(HttpContext c, string id)
    {
        Note(c);
        if (Find(id) is not { } repo) return Results.NotFound();
        return Results.Text(JsonSerializer.Serialize(repo.Commits.Select(m => new { id = m.Id, title = m.Title, message = m.Title, date = "2026-10-01T00:00:00.000Z" })), "application/json");
    }

    private IResult Tree(HttpContext c, string id)
    {
        Note(c);
        if (Find(id) is not { } repo) return Results.NotFound();
        return Results.Text(JsonSerializer.Serialize(repo.Files.Select(f => new
        {
            type = "file",
            oid = "blob-" + f.Key,
            size = f.Value.Length,
            path = f.Key,
            lfs = new { oid = StandIns.Sha(f.Value), size = f.Value.Length },
        })), "application/json");
    }

    private async Task ServeAsync(HttpContext c, string id, string file)
    {
        Note(c);
        if (Find(id) is not { } repo || !repo.Files.TryGetValue(file, out var bytes))
        {
            c.Response.StatusCode = 404;
            return;
        }

        if (repo.Gated is not null)
        {
            var auth = c.Request.Headers.Authorization.ToString();
            if (auth.Length == 0)
            {
                c.Response.StatusCode = 401;
                return;
            }

            if (auth != "Bearer " + AcceptedToken)
            {
                c.Response.StatusCode = 403;
                return;
            }
        }

        long start = 0;
        var partial = false;
        var end = bytes.Length - 1L;
        var range = c.Request.Headers.Range.ToString();
        if (range.Length > 0 && RangeHeaderValue.TryParse(range, out var parsed) && parsed.Ranges.FirstOrDefault() is { From: { } from } first)
        {
            start = from;
            partial = true;
            if (first.To is { } to) end = Math.Min(end, to);
        }

        c.Response.Headers.AcceptRanges = "bytes";
        if (partial)
        {
            c.Response.StatusCode = 206;
            c.Response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {start}-{end}/{bytes.Length}");
        }

        c.Response.ContentLength = end - start + 1;
        try
        {
            for (var at = start; at <= end;)
            {
                var n = (int)Math.Min(64 * 1024, end - at + 1);
                await c.Response.Body.WriteAsync(bytes.AsMemory((int)at, n), c.RequestAborted);
                await c.Response.Body.FlushAsync(c.RequestAborted);
                at += n;
                if (ChunkDelay > TimeSpan.Zero) await Task.Delay(ChunkDelay, c.RequestAborted);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
