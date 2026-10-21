using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace Wicked.App.Debug;

/// <summary>The Debug Console tab: the program's output, and the box that evaluates expressions in the frame being looked at.</summary>
public partial class DebugConsoleView : UserControl
{
    public DebugConsoleView()
    {
        InitializeComponent();
        ConsoleLines.AttachedToVisualTree += (_, _) => FollowEnd();
    }

    private DebugConsoleViewModel? Console => DataContext as DebugConsoleViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Console is { } console)
        {
            console.Lines.CollectionChanged += (_, _) => FollowEnd();
        }
    }

    /// <summary>New lines scroll into view when the list was at its end (reading older lines is not interrupted).</summary>
    private void FollowEnd()
    {
        if (ConsoleLines.ItemCount == 0) return;
        var scroll = ConsoleLines.Scroll as ScrollViewer;
        var atEnd = scroll is null || scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 40;
        if (atEnd) Avalonia.Threading.Dispatcher.UIThread.Post(() => ConsoleLines.ScrollIntoView(ConsoleLines.ItemCount - 1), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnInputKey(object? sender, KeyEventArgs e)
    {
        if (Console is not { } console) return;
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                _ = console.SubmitAsync();
                break;
            case Key.Up:
                e.Handled = true;
                console.Recall(-1);
                ConsoleInput.CaretIndex = ConsoleInput.Text?.Length ?? 0;
                break;
            case Key.Down:
                e.Handled = true;
                console.Recall(1);
                ConsoleInput.CaretIndex = ConsoleInput.Text?.Length ?? 0;
                break;
        }
    }

    private void OnClear(object? sender, RoutedEventArgs e) => Console?.Clear();

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (Console is { } console) _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(console.AllText);
    }

    private void OnLineDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is ConsoleLine { Path: { } path } line && Console?.OpenSource is { } open) _ = open(path, line.Line);
    }
}
