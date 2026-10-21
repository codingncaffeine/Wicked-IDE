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

    // Kept alive for the life of the process; a collected registration stops handling the signal.
    private static PosixSignalRegistration? _terminate;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Chat.ChatFonts.Register();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (WatchUiThread) UiStallWatch.Start(Environment.GetEnvironmentVariable("WICKED_UI_TRACE") == "1");
            Editor.EditorServices.Start();
            var shell = new ShellViewModel(Paths);
            desktop.MainWindow = new MainWindow { DataContext = shell };
            _ = shell.StartAsync();
            desktop.ShutdownRequested += (_, _) => shell.Dispose();

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
