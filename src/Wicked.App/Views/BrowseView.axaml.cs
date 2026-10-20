using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Wicked.App.ViewModels;

namespace Wicked.App.Views;

public partial class BrowseView : UserControl
{
    private BrowseViewModel? _browse;

    public BrowseView()
    {
        InitializeComponent();
        // Escape closes the page (the prompt above it, when there is one, takes the key first).
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not BrowseViewModel browse) return;
            browse.Close();
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_browse is not null) _browse.PropertyChanged -= OnBrowseChanged;
            _browse = DataContext as BrowseViewModel;
            if (_browse is not null) _browse.PropertyChanged += OnBrowseChanged;
        };
    }

    // The keyboard starts in the search box when the page opens or the tab changes, so typing filters and Escape closes.
    private void OnBrowseChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(BrowseViewModel.IsOpen) or nameof(BrowseViewModel.ShowHub)) || _browse is not { IsOpen: true } browse) return;
        Dispatcher.UIThread.Post(() => this.FindControl<TextBox>(browse.ShowHub ? "HubSearch" : "CatalogFilter")?.Focus(), DispatcherPriority.Loaded);
    }
}
