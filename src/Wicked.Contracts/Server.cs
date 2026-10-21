namespace Wicked.Contracts;

/// <summary>Where the API gateway stands. <c>Address</c> is the base URL clients use, e.g. <c>http://127.0.0.1:1337</c>.</summary>
public sealed record GatewayStatus(bool Enabled, bool Running, string? Address, int Port, bool ListensOnLan, string? Error);

/// <summary>
/// What <c>GET /v1/wicked/status</c> says: the daemon, its gateway and the model half. <c>Ready</c> is false while the model
/// services are still starting (hardware, catalog, store). <c>RouterOwner</c> is <c>this</c> when the daemon supervises the router,
/// <c>other</c> when it is attached to one another process supervises, and null when no router is running.
/// </summary>
public sealed record DaemonStatus(
    bool Ready,
    string Version,
    int ProcessId,
    DateTimeOffset StartedAt,
    GatewayStatus Gateway,
    int KeyCount,
    int Installed,
    int Loaded,
    string? RouterOwner);

/// <summary>
/// One request through the gateway, for the request log: method, route, model, status, token counts and time. <c>Id</c> rises with
/// every request (a client asks for the entries after the last it has). <c>Client</c> is the name of the API key that made the
/// request, or null when none was needed.
/// </summary>
public sealed record ApiLogEntry(
    long Id,
    DateTimeOffset Time,
    string Method,
    string Route,
    string? Model,
    int Status,
    int? PromptTokens,
    int? CompletionTokens,
    long Milliseconds,
    bool Streamed,
    string? Client,
    string? Error);

/// <summary>An installed model and whether it is in memory. <c>State</c> is unloaded, loading, loaded, sleeping or failed.</summary>
public sealed record ModelInfo(string Id, string Name, string State, long SizeBytes, string? CatalogId, int? Context, string? Error);

/// <summary>One step of a pull, streamed to the client as it happens. <c>Phase</c> is start, engine, download, licence, done or error.</summary>
public sealed record PullEvent(string Phase, string Message, double? Fraction, bool Done, string? Error);

/// <summary>A model with something newer.</summary>
public sealed record UpdateInfo(string ModelId, string Kind, string Detail, string? From, string? To, long NewSize, IReadOnlyList<string> Titles, bool CanApply, string Policy);

/// <summary>What a look for updates found.</summary>
public sealed record UpdateReport(IReadOnlyList<UpdateInfo> Models, string? Engine, string? App, string? Skipped, string? Problem);

/// <summary>How one update ended: updated, rolledBack, deferred, nothingToDo or failed.</summary>
public sealed record UpdateResultInfo(string ModelId, string Result, string Message);

public sealed record LoadRequest(string Id);

public sealed record UnloadRequest(string? Id);

/// <summary>Install a catalog id or a Hugging Face repo, optionally as <c>name:QUANT</c>. A licence with terms is accepted only when <c>AcceptLicence</c> is set.</summary>
public sealed record PullRequest(string Id, bool AcceptLicence = false);

/// <summary>Update <c>Model</c>, or every model with an update when <c>All</c> is set.</summary>
public sealed record UpdateRequest(string? Model, bool All);
