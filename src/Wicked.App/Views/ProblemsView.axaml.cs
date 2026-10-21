using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wicked.App.Language;

namespace Wicked.App.Views;

/// <summary>The Problems panel: a click (or Enter) on a problem opens it, on a file's header folds the file.</summary>
public partial class ProblemsView : UserControl
{
    public ProblemsView()
    {
        InitializeComponent();
        ProblemsList.AddHandler(PointerReleasedEvent, (_, _) => Choose(), RoutingStrategies.Bubble, handledEventsToo: true);
        ProblemsList.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Choose();
            e.Handled = true;
        };
    }

    private void Choose()
    {
        if (DataContext is not ProblemsViewModel vm) return;
        switch (ProblemsList.SelectedItem)
        {
            case ProblemItem item:
                vm.OpenCommand.Execute(item);
                break;
            case FileProblems file:
                vm.ToggleCommand.Execute(file);
                break;
        }
    }
}
