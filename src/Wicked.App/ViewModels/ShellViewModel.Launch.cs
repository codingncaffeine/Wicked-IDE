using Wicked.App.Shell;
using Wicked.App.Workbench;

namespace Wicked.App.ViewModels;

/// <summary>
/// Folders and files given on the command line, by the file manager (Open With) or by a later launch handed to this window:
/// a folder opens here when this window has none or the same one, files open in tabs (at a line with -g), and --wait is
/// answered when they are all closed again.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The window comes to the front (a launch was handed to it).</summary>
    public event Action? ActivateRequested;

    /// <summary>Opens what a launch names in this window.</summary>
    public async Task<IReadOnlyList<EditorTab>> OpenLaunchAsync(LaunchRequest launch)
    {
        if (launch.Folders.Count > 0 && launch.Folders[0] != Workbench.Root) await OpenFolderAndRememberAsync(launch.Folders[0]);
        var tabs = new List<EditorTab>();
        foreach (var file in launch.Files)
        {
            var tab = File.Exists(file.Path) ? await Workbench.OpenAsync(file.Path) : Workbench.OpenNew(file.Path);
            if (tab is null) continue;
            tabs.Add(tab);
            if (file.Line is { } line) await OpenAtAsync(file.Path, line - 1, (file.Column ?? 1) - 1);
        }

        return tabs;
    }

    /// <summary>
    /// A later launch, handed over by <see cref="SingleInstance"/> (on this window's thread): a folder other than this window's
    /// opens in a window of its own, as VS Code opens one; anything else opens here and the window comes forward.
    /// </summary>
    public async Task<(LaunchAnswer Answer, Task? Closed)> HandleLaunchAsync(LaunchRequest launch)
    {
        if (launch.NewWindow || (launch.Folders.Count > 0 && Workbench.Root is { } root && launch.Folders[0] != root)) return (LaunchAnswer.OpenHere, null);
        var tabs = await OpenLaunchAsync(launch);
        ActivateRequested?.Invoke();
        return (LaunchAnswer.Opened, launch.Wait ? WhenClosedAsync(tabs) : null);
    }

    /// <summary>The tabs the launch this window started with opened (its --wait waits for them).</summary>
    public IReadOnlyList<EditorTab> StartedTabs { get; private set; } = [];

    /// <summary>Ends when none of <paramref name="tabs"/> is open any more (--wait).</summary>
    public Task WhenClosedAsync(IReadOnlyList<EditorTab> tabs)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new HashSet<EditorTab>(tabs);
        void Check(EditorTab closed)
        {
            waiting.Remove(closed);
            if (waiting.Count > 0) return;
            Workbench.TabClosed -= Check;
            done.TrySetResult();
        }

        if (waiting.Count == 0) done.TrySetResult();
        else Workbench.TabClosed += Check;
        return done.Task;
    }
}

/// <summary>New Window (Ctrl+Shift+N) and folders that need a window of their own: another Wicked process, as VS Code opens another window.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>Starts a window process with these arguments (tests replace it); false when it could not start.</summary>
    public Func<IReadOnlyList<string>, bool> StartWindow { get; set; } = StartWindowProcess;

    /// <summary>A new, empty window (or one on <paramref name="folder"/>), sharing this profile.</summary>
    public void OpenNewWindow(string? folder = null)
    {
        if (!StartWindow(folder is null ? ["--new-window"] : ["--new-window", folder]))
            Notices.Post(new Wicked.Core.Notice("new-window", "A new window could not be started", Wicked.Core.NoticeKind.Error));
    }

    private static bool StartWindowProcess(IReadOnlyList<string> args)
    {
        try
        {
            if (Environment.ProcessPath is not { } program) return false;
            var info = new System.Diagnostics.ProcessStartInfo(program) { UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            System.Diagnostics.Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Wicked.Core.Log.Warn($"New window: {ex.Message}");
            return false;
        }
    }

    /// <summary>Files and folders dropped on the window: files open in tabs; a folder opens here, or in a window of its own when one is open.</summary>
    public async Task OpenDroppedAsync(IReadOnlyList<string> paths)
    {
        var folders = paths.Where(Directory.Exists).ToList();
        var files = paths.Where(File.Exists).Select(p => new OpenedFile(p)).ToList();
        if (folders.Count > 0 && Workbench.Root is { } root && folders[0] != root)
        {
            OpenNewWindow(folders[0]);
            folders.Clear();
        }

        await OpenLaunchAsync(new LaunchRequest(folders, files, false, false));
    }
}
