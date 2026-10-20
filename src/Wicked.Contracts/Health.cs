namespace Wicked.Contracts;

/// <summary>What the daemon says about itself at <c>GET /v1/health</c>.</summary>
/// <param name="Status">"ok" once the daemon is serving.</param>
/// <param name="Version">The daemon's build, as <c>--version</c> prints it.</param>
/// <param name="ProcessId">The daemon's process, so a client can tell one run from the next.</param>
/// <param name="StartedAt">When this daemon started.</param>
public sealed record HealthReport(string Status, string Version, int ProcessId, DateTimeOffset StartedAt);
