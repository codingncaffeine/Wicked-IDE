using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Wicked.Core.Engines;

/// <summary>
/// What the process that owns the router tells the others: where it listens, which processes it is, and how it was started.
/// Written to <c>router.json</c> in the profile's runtime folder each time the router (re)starts.
/// </summary>
/// <param name="Port">The loopback port the router listens on now.</param>
/// <param name="RouterPid">The <c>llama-server</c> router process.</param>
/// <param name="OwnerPid">The Wicked process that supervises it (the window or <c>wickedd</c>).</param>
/// <param name="EngineTag">The engine install the router was started from, e.g. <c>b11765-cpu</c>.</param>
/// <param name="ModelsMax">How many models the router was started to hold at once.</param>
/// <param name="StartedAt">When this router process was started.</param>
public sealed record RouterRuntimeInfo(int Port, int RouterPid, int OwnerPid, string? EngineTag, int ModelsMax, DateTimeOffset StartedAt);

/// <summary>
/// One router per profile (D3, D10). The process that supervises the router holds an exclusive lock on <c>router.lock</c> in the
/// runtime folder for as long as it does, and describes the router in <c>router.json</c>. A second process that finds the lock
/// taken reads that file and attaches to the running router instead of starting another; when the owner dies the system drops
/// its lock, and the next process that needs the router takes the lock and the ownership.
/// </summary>
public sealed class RouterOwnership : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    private readonly FileStream _lock;
    private readonly string _infoPath;
    private volatile bool _published;

    private RouterOwnership(FileStream heldLock, string infoPath)
    {
        _lock = heldLock;
        _infoPath = infoPath;
    }

    public static string LockPath(WickedPaths paths) => Path.Combine(paths.Runtime, "router.lock");

    public static string InfoPath(WickedPaths paths) => Path.Combine(paths.Runtime, "router.json");

    /// <summary>Takes the profile's router lock, or returns null when another process (or another owner in this one) holds it.</summary>
    public static RouterOwnership? TryAcquire(WickedPaths paths)
    {
        paths.EnsureCreated();
        try
        {
            // FileShare.None is an exclusive lock (flock on Unix) that the system drops when the process dies.
            return new RouterOwnership(new FileStream(LockPath(paths), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), InfoPath(paths));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>What the owner last wrote, or null when there is no file or it cannot be read (a half-written file is retried by the caller).</summary>
    public static RouterRuntimeInfo? Read(WickedPaths paths)
    {
        try
        {
            var path = InfoPath(paths);
            return File.Exists(path) ? JsonSerializer.Deserialize<RouterRuntimeInfo>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Records the router the owner just started. Atomic: a reader sees the old file or the new one.</summary>
    public void Publish(RouterRuntimeInfo info)
    {
        var temp = _infoPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(info, Json));
        File.Move(temp, _infoPath, overwrite: true);
        _published = true;
    }

    /// <summary>Whether a process is alive; false for a pid that is gone or that this user may not look at.</summary>
    public static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="pid"/> is a router this build starts (its command line carries <c>--models-preset</c>), so a pid
    /// left in a stale file that the system has since given to another program is never signalled. Only answerable on Linux;
    /// elsewhere it says no, and an orphaned router is left alone.
    /// </summary>
    public static bool LooksLikeARouter(int pid)
    {
        try
        {
            var path = $"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/cmdline";
            return OperatingSystem.IsLinux() && File.Exists(path) && File.ReadAllText(path).Contains("--models-preset", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Whether some process holds the lock right now (a probe: it takes and drops the lock when it is free).</summary>
    public static bool IsHeld(WickedPaths paths)
    {
        using var taken = TryAcquire(paths);
        return taken is null;
    }

    public void Dispose()
    {
        try
        {
            // Only a file this owner wrote is its to remove; a probe that took and dropped the lock leaves the real owner's alone.
            if (_published) File.Delete(_infoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"The router file could not be removed: {ex.Message}");
        }

        _lock.Dispose();
    }
}
