using System.Diagnostics;
using Wicked.Core;

namespace Wicked.App.Models;

/// <summary>
/// Hands a folder or a link to the desktop (the file manager, the browser): <c>xdg-open</c> on Linux, <c>explorer</c> on
/// Windows, <c>open</c> on macOS. Started and let go, never waited for; call it from a worker, not the UI thread.
/// </summary>
public static class Opener
{
    public static void Open(string target)
    {
        try
        {
            var launcher = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            var info = new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(target);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn($"Could not open {target}: {ex.Message}");
        }
    }
}
