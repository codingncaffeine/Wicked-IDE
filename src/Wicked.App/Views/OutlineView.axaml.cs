using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wicked.App.Language;

namespace Wicked.App.Views;

/// <summary>The Outline panel: a click (or Enter) on a symbol goes to it.</summary>
public partial class OutlineView : UserControl
{
    public OutlineView()
    {
        InitializeComponent();
        OutlineList.AddHandler(PointerReleasedEvent, (_, _) => Choose(), RoutingStrategies.Bubble, handledEventsToo: true);
        OutlineList.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Choose();
            e.Handled = true;
        };
    }

    private void Choose()
    {
        if (DataContext is OutlineViewModel vm && OutlineList.SelectedItem is OutlineRow row) vm.OpenCommand.Execute(row);
    }
}
