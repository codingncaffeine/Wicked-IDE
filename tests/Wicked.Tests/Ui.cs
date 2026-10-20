using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.Core;

// The headless session starts before any test runs (see UiSessionFixture).
[assembly: AssemblyFixture(typeof(Wicked.Tests.UiSessionFixture))]

namespace Wicked.Tests;

/// <summary>
/// Starts the headless app session once, before the first test of the assembly, so its one set-up of Avalonia's
/// dispatcher cannot meet a post from another test's worker.
/// </summary>
public sealed class UiSessionFixture : IAsyncLifetime
{
    // Awaited, never blocked on: xunit runs fixtures on its own synchronization context.
    public ValueTask InitializeAsync() => new(Ui.EnsureStartedAsync());

    public ValueTask DisposeAsync() => default;
}

/// <summary>The headless platform's app: the real styles and resources, no window opened by the app itself.</summary>
public sealed class HeadlessApp : App.App
{
    public override void OnFrameworkInitializationCompleted()
    {
        // The real App opens the main window; the tests open their own on a profile of their own.
    }
}

/// <summary>
/// The app on Avalonia's headless platform: the real window, nothing on anybody's desktop. Avalonia is set up once per
/// process, so every UI test shares one session.
/// </summary>
internal static class Ui
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() =>
        HeadlessUnitTestSession.StartNew(typeof(HeadlessEntry), AvaloniaTestIsolationLevel.PerAssembly));

    public static Task EnsureStartedAsync() => Session.Value.Dispatch(() => 0, CancellationToken.None);

    /// <summary>Runs a UI test's body on the session's thread.</summary>
    public static Task<T> Run<T>(Func<T> body) => Session.Value.Dispatch(body, TestContext.Current.CancellationToken);

    /// <summary>The main window on <paramref name="paths"/>'s profile, shown and laid out.</summary>
    public static (MainWindow Window, ShellViewModel Shell) Start(WickedPaths paths, NoticeBoard? notices = null)
    {
        App.App.Paths = paths;
        var shell = new ShellViewModel(paths, notices, daemonPoll: TimeSpan.FromMilliseconds(200));
        var window = new MainWindow { DataContext = shell, Width = 1440, Height = 900 };
        window.Show();
        Pump(0.3);
        return (window, shell);
    }

    /// <summary>Lets the app work (its dispatcher and render timer) for a while.</summary>
    public static void Pump(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(15);
        }
    }

    /// <summary>Pumps until <paramref name="done"/> holds or <paramref name="seconds"/> pass; returns whether it held.</summary>
    public static bool PumpUntil(Func<bool> done, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (done()) return true;
            Pump(0.05);
        }

        return done();
    }
}

/// <summary>The app builder the headless session starts from.</summary>
public static class HeadlessEntry
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Input the way a user gives it: a pointer pressed and released over a control, keys on the window.</summary>
internal static class Input
{
    public static void Click(Avalonia.Controls.Window window, Avalonia.Controls.Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException($"{control.Name} is not in the window");
        window.MouseDown(centre, Avalonia.Input.MouseButton.Left);
        window.MouseUp(centre, Avalonia.Input.MouseButton.Left);
        Ui.Pump(0.1);
    }

    public static void Chord(Avalonia.Controls.Window window, Avalonia.Input.Key key, Avalonia.Input.RawInputModifiers modifiers)
    {
        window.KeyPress(key, modifiers, Avalonia.Input.PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, Avalonia.Input.PhysicalKey.None, null);
        Ui.Pump(0.1);
    }
}
