using Avalonia;
using Wicked.Core;

namespace Wicked.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupTimeline.Begin();
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"Wicked {BuildInfo.Stamp}");
            Console.WriteLine(BuildInfo.Licence);
            return 0;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine($"""
                Usage: wicked [options] [folder | file ...]

                  folder              Open the folder (in a window of its own when Wicked has another one open).
                  file                Open the file in the running Wicked; one that does not exist yet is made on save.
                  -g, --goto FILE:LINE[:COLUMN]
                                      Open the file at a line and column.
                  -n, --new-window    Open a new window, whatever is running.
                  -w, --wait          Wait until the files are closed (for git commit and other tools).
                  --version           Print the version and licence, then exit.

                {WickedPaths.HomeVariable}=DIR keeps every setting, model and log under DIR instead of the usual folders.
                WICKED_UI_TRACE=1 logs every moment the window's thread was busy for more than 4 ms.
                The command-line tools are in wicked-cli; the daemon is wickedd.
                """);
            return 0;
        }

        var launch = Shell.LaunchRequest.Parse(args, Environment.CurrentDirectory, Directory.Exists, out var error);
        if (launch is null)
        {
            Console.Error.WriteLine($"wicked: {error}");
            return 2;
        }

        // Read before the UI thread starts: nothing that touches the disk runs on it.
        var paths = WickedPaths.ForCurrentUser();
        paths.EnsureCreated();

        // A Wicked already running takes the launch (it opens the files, or says to open a window of our own).
        var openHere = false;
        if (!launch.NewWindow)
        {
            var (handedOff, here) = Shell.SingleInstance.HandOffAsync(paths, launch, TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            if (handedOff is { } exitCode) return exitCode;
            openHere = here;
        }

        Log.Initialize("wicked", paths.Logs);
        if (paths.Isolated) Log.Info($"Isolated profile under {WickedPaths.HomeVariable}");
        App.Paths = paths;
        App.WatchUiThread = true;
        App.Launch = openHere ? launch with { NewWindow = true } : launch;
        App.ListenForLaunches = !launch.NewWindow && !openHere;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Log.Crash("startup", ex));
            return 1;
        }
        finally
        {
            UiStallWatch.Current?.Stop();
            Log.Flush(TimeSpan.FromSeconds(2));
        }
    }

    /// <summary>Used by <see cref="Main"/>, the designer and the headless tests.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        WindowingBackend.Apply(
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace());
}
