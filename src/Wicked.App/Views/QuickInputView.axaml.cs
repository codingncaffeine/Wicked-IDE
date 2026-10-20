using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wicked.App.ViewModels;

namespace Wicked.App.Views;

/// <summary>The palette's keys: arrows move, Enter chooses, Escape (or a click elsewhere) closes.</summary>
public partial class QuickInputView : UserControl
{
    public QuickInputView()
    {
        InitializeComponent();
        QueryBox.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        List.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            if (DataContext is QuickInputViewModel vm) _ = vm.ChooseAsync(List.SelectedIndex);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        QueryBox.LostFocus += (_, _) =>
        {
            // Focus moving into the list (a click on a row) keeps the palette open.
            if (DataContext is QuickInputViewModel vm && !List.IsKeyboardFocusWithin && !List.IsPointerOver) vm.Close();
        };
    }

    public void FocusQuery()
    {
        QueryBox.Focus();
        QueryBox.CaretIndex = QueryBox.Text?.Length ?? 0;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (DataContext is not QuickInputViewModel vm) return;
        switch (e.Key)
        {
            case Key.Down:
                vm.Move(1);
                break;
            case Key.Up:
                vm.Move(-1);
                break;
            case Key.Enter:
                _ = vm.ChooseAsync();
                break;
            case Key.Escape:
                vm.Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
