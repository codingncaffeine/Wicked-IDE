using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Wicked.App.Search;

/// <summary>The Search panel: Enter searches at once, a click on a match opens it, on a file folds it.</summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        SearchQuery.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter || DataContext is not SearchViewModel vm) return;
            e.Handled = true;
            _ = vm.RunAsync();
        };
        SearchResults.AddHandler(PointerReleasedEvent, (_, _) => Choose(), RoutingStrategies.Bubble, handledEventsToo: true);
        SearchResults.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Choose();
            e.Handled = true;
        };
    }

    /// <summary>The query box takes the keyboard with its text selected (Ctrl+Shift+F), as in VS Code.</summary>
    public void FocusQuery()
    {
        SearchQuery.Focus();
        SearchQuery.SelectAll();
    }

    private void Choose()
    {
        if (DataContext is not SearchViewModel vm) return;
        switch (SearchResults.SelectedItem)
        {
            case SearchMatchRow match:
                vm.OpenCommand.Execute(match);
                break;
            case SearchFileRow file:
                vm.ToggleCommand.Execute(file);
                break;
        }
    }
}
