using Avalonia;
using Wicked.Core;

namespace Wicked.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a is "--version" or "-V"))
        {
            Console.WriteLine($"Wicked {BuildInfo.Stamp}");
            Console.WriteLine(BuildInfo.Licence);
            return 0;
        }

        if (args.Any(a => a is "--help" or "-h"))
        {
            Console.WriteLine($"""
                Usage: wicked [options]

                  --version   Print the version and licence, then exit.

                {WickedPaths.HomeVariable}=DIR keeps every setting, model and log under DIR instead of the usual folders.
                WICKED_UI_TRACE=1 logs every moment the window's thread was busy for more than 4 ms.
                The command-line tools are in wicked-cli; the daemon is wickedd.
                """);
            return 0;
        }

        // Read before the UI thread starts: nothing that touches the disk runs on it.
        var paths = WickedPaths.ForCurrentUser();
        paths.EnsureCreated();
        Log.Initialize("wicked", paths.Logs);
        if (paths.Isolated) Log.Info($"Isolated profile under {WickedPaths.HomeVariable}");
        App.Paths = paths;
        App.WatchUiThread = true;

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
