using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Wicked.App.Git;

/// <summary>The Git output as a list that follows the newest line.</summary>
public partial class GitOutputView : UserControl
{
    public GitOutputView()
    {
        InitializeComponent();
        OutputLines.FontFamily = Chat.ChatFonts.Mono;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is GitOutputLog log) log.Lines.CollectionChanged += OnLines;
        };
    }

    private void OnLines(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || e.NewItems is not { Count: > 0 }) return;
        var last = e.NewItems[^1];
        Dispatcher.UIThread.Post(() => OutputLines.ScrollIntoView(last!), DispatcherPriority.Background);
    }
}
