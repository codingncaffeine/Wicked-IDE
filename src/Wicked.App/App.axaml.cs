using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.Core;

namespace Wicked.App;

public partial class App : Application
{
    /// <summary>The profile's folders, resolved by <see cref="Program"/> (or a test) before the UI thread starts.</summary>
    internal static WickedPaths Paths { get; set; } = null!;

    /// <summary>Whether the UI-thread stall watch runs; off in headless tests, which pump the dispatcher themselves.</summary>
    internal static bool WatchUiThread { get; set; }

    /// <summary>What the command line (or the file manager) asked this window to open; set by <see cref="Program"/>.</summary>
    internal static Shell.LaunchRequest Launch { get; set; } = Shell.LaunchRequest.Empty;

    /// <summary>This is the first window: later launches are handed to it (<see cref="Shell.SingleInstance"/>).</summary>
    internal static bool ListenForLaunches { get; set; }

    // Kept alive for the life of the process; a collected registration stops handling the signal.
    private static PosixSignalRegistration? _terminate;
    private static Shell.SingleInstance? _single;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Chat.ChatFonts.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupTimeline.Mark("framework");
            if (WatchUiThread) UiStallWatch.Start(Environment.GetEnvironmentVariable("WICKED_UI_TRACE") == "1");
            Editor.EditorServices.Start();
            StartupTimeline.Mark("editor services");
            var shell = new ShellViewModel(Paths);
            StartupTimeline.Mark("view model");
            desktop.MainWindow = new MainWindow { DataContext = shell };
            StartupTimeline.Mark("window built");
            var launch = Launch;
            var started = shell.StartAsync(launch);
            if (ListenForLaunches) _single = Shell.SingleInstance.Listen(Paths, request => Dispatcher.UIThread.InvokeAsync(() => shell.HandleLaunchAsync(request)));
            // wicked --wait FILE with no Wicked running: this window is the editor the caller waits for, so it closes with the files.
            if (launch.Wait) _ = started.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
                _ = shell.WhenClosedAsync(shell.StartedTabs).ContinueWith(__ => Dispatcher.UIThread.Post(() => desktop.MainWindow?.Close()), TaskScheduler.Default)), TaskScheduler.Default);
            desktop.ShutdownRequested += (_, _) =>
            {
                _single?.Dispose();
                shell.Dispose();
            };

            // SIGTERM (a logout, a kill) closes the window the normal way.
            if (!OperatingSystem.IsWindows())
            {
                _terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
                {
                    context.Cancel = true;
                    Dispatcher.UIThread.Post(() => desktop.MainWindow?.Close());
                });
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
