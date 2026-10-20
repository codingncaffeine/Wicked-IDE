using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Wicked.App.ViewModels;

namespace Wicked.App.Views;

public partial class PromptView : UserControl
{
    public PromptView()
    {
        InitializeComponent();
        // Escape says no; the keyboard starts on the safe answer when the question is a delete.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not ModelPrompt prompt) return;
            prompt.CancelCommand.Execute(null);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ModelPrompt prompt) return;
            Dispatcher.UIThread.Post(() => (prompt.Danger ? this.FindControl<Button>("PromptCancel") : this.FindControl<Button>("PromptConfirm"))?.Focus(), DispatcherPriority.Loaded);
        };
    }
}
