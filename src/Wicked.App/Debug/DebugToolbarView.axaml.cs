using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Wicked.App.Debug;

/// <summary>The bar over the editor while a session runs: continue or pause, the three steps, restart, stop.</summary>
public partial class DebugToolbarView : UserControl
{
    public DebugToolbarView() => InitializeComponent();

    private DebugController? Controller => DataContext as DebugController;

    private void OnContinue(object? sender, RoutedEventArgs e)
    {
        if (Controller is not { } c) return;
        _ = c.IsRunning ? c.PauseAsync() : c.ContinueAsync();
    }

    private void OnStepOver(object? sender, RoutedEventArgs e) => _ = Controller?.StepOverAsync();

    private void OnStepInto(object? sender, RoutedEventArgs e) => _ = Controller?.StepIntoAsync();

    private void OnStepOut(object? sender, RoutedEventArgs e) => _ = Controller?.StepOutAsync();

    private void OnRestart(object? sender, RoutedEventArgs e) => _ = Controller?.RestartAsync();

    private void OnStop(object? sender, RoutedEventArgs e) => _ = Controller?.StopAsync();
}
