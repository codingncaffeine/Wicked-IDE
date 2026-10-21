using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wicked.App.Language;

namespace Wicked.App.Views;

/// <summary>The References panel: a click opens a place; on a file it folds the file, on a function it opens its calls.</summary>
public partial class ReferencesView : UserControl
{
    public ReferencesView()
    {
        InitializeComponent();
        ReferencesList.AddHandler(PointerReleasedEvent, (_, _) => Choose(false), RoutingStrategies.Bubble, handledEventsToo: true);
        ReferencesList.DoubleTapped += (_, _) => Choose(true);
        ReferencesList.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Choose(true);
            e.Handled = true;
        };
    }

    private void Choose(bool open)
    {
        if (DataContext is not ReferencesViewModel vm) return;
        switch (ReferencesList.SelectedItem)
        {
            case ReferenceRow row:
                vm.OpenCommand.Execute(row);
                break;
            case ReferenceFile file:
                vm.ToggleFileCommand.Execute(file);
                break;
            case CallRow call when open:
                vm.OpenCommand.Execute(call);
                break;
            case CallRow call:
                vm.ToggleCallCommand.Execute(call);
                break;
        }
    }
}
