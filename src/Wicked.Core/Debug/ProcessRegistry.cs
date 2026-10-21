using System.Collections.Concurrent;
using System.Diagnostics;

namespace Wicked.Core.Debug;

/// <summary>
/// The processes the debugger started (adapters, and programs run on their behalf). A session ends its own; whatever is still
/// running when Wicked exits, however it exits, is ended here, so no adapter or debugged program outlives the IDE.
/// </summary>
internal static class ProcessRegistry
{
    private static readonly ConcurrentDictionary<int, Process> Live = new();

    static ProcessRegistry() => AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();

    public static void Track(Process process) => Live[process.Id] = process;

    public static void Untrack(Process process) => Live.TryRemove(process.Id, out _);

    public static int Count => Live.Count;

    /// <summary>Ends every process still tracked, and what they started.</summary>
    public static void KillAll()
    {
        foreach (var (id, process) in Live.ToArray())
        {
            Live.TryRemove(id, out _);
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Gone already.
            }
        }
    }
}
