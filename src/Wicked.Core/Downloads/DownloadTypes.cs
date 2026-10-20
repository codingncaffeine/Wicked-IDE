using System.Text.Json.Serialization;

namespace Wicked.Core.Downloads;

public enum DownloadState
{
    Queued,
    Running,
    Paused,
    Verifying,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// What to fetch and where it goes. <c>AuthToken</c> is held in memory only and never written to <c>downloads.json</c>:
/// a job that survives a restart sets <c>Authenticated</c> and gets its token from the manager's token provider again.
/// </summary>
public sealed record DownloadRequest(
    Uri Url,
    string Destination,
    long? ExpectedSize = null,
    string? ExpectedSha256 = null,
    bool Authenticated = false,
    string? Title = null)
{
    [JsonIgnore]
    public string? AuthToken { get; init; }
}

/// <summary>An immutable picture of one job, safe to hand to any thread. Rate is bytes per second over the last few seconds.</summary>
public sealed record DownloadSnapshot(
    string Id,
    DownloadRequest Request,
    DownloadState State,
    long BytesDone,
    long? TotalBytes,
    double BytesPerSecond,
    TimeSpan? Eta,
    string? Error,
    int Attempt,
    string? Sha256)
{
    public bool IsFinished => State is DownloadState.Done or DownloadState.Failed or DownloadState.Cancelled;
}

/// <summary>The knobs of a <see cref="DownloadManager"/>; the defaults are what the app uses, tests shrink the delays.</summary>
public sealed class DownloadOptions
{
    /// <summary>Jobs running at once.</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Total attempts per run of a job; an attempt that moved bytes resets the count.</summary>
    public int MaxAttempts { get; set; } = 6;

    public TimeSpan BackoffBase { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan BackoffCap { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Space that must stay free on the drive after the download (the OS and the engine need some).</summary>
    public long MinFreeBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>Jobs that were running when the app stopped start again by themselves; false restores them paused.</summary>
    public bool AutoResume { get; set; } = true;

    /// <summary>Supplies the token for requests marked <c>Authenticated</c> that carry none of their own.</summary>
    public Func<string?>? TokenProvider { get; set; }

    /// <summary>Free bytes on the drive holding a folder, or null when unknown. Replaced by tests.</summary>
    public Func<string, long?> FreeSpace { get; set; } = DownloadManager.DriveFreeSpace;

    public TimeProvider Time { get; set; } = TimeProvider.System;
}

/// <summary>The download cannot succeed by trying again (404, wrong size, wrong hash, no space).</summary>
internal sealed class PermanentDownloadException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>The download broke in a way worth retrying (dropped connection, 5xx, 429).</summary>
internal sealed class TransientDownloadException(string message, Exception? inner = null, TimeSpan? retryAfter = null) : IOException(message, inner)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
