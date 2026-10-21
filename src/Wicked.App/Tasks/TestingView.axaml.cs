using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace Wicked.App.Tasks;

/// <summary>Converters the Testing panel's template uses.</summary>
public static class TestingConverters
{
    /// <summary>Project rows are set in a heavier weight than the groups and tests below them.</summary>
    public static readonly IValueConverter ProjectWeight = new FuncValueConverter<bool, FontWeight>(isProject => isProject ? FontWeight.SemiBold : FontWeight.Normal);
}

/// <summary>The Testing panel: a click picks a row (its failure shows below), a double click or Enter opens the test's code.</summary>
public partial class TestingView : UserControl
{
    private bool _restoring;

    public TestingView()
    {
        // The code face is registered once the app exists, so it is taken when the panel is made, not when the XAML loads.
        Resources["Font.TestMono"] = Chat.ChatFonts.Mono;
        InitializeComponent();
        TestTree.SelectionChanged += (_, _) =>
        {
            if (_restoring || DataContext is not TestingViewModel vm) return;
            vm.Select(TestTree.SelectedItem as TestRowItem);
        };
        TestTree.AddHandler(DoubleTappedEvent, (_, _) =>
        {
            if (DataContext is TestingViewModel vm && TestTree.SelectedItem is TestRowItem row) vm.OpenCommand.Execute(row);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        TestTree.KeyDown += (_, e) =>
        {
            if (DataContext is not TestingViewModel vm || TestTree.SelectedItem is not TestRowItem row) return;
            switch (e.Key)
            {
                case Key.Enter:
                    vm.OpenCommand.Execute(row);
                    break;
                case Key.Right:
                    vm.Expand(row, true);
                    break;
                case Key.Left:
                    vm.Expand(row, false);
                    break;
                default:
                    return;
            }

            e.Handled = true;
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is TestingViewModel vm) vm.Rows.CollectionChanged += OnRowsChanged;
        };
    }

    /// <summary>The rows were replaced (a result arrived): the same test is picked again, so its failure stays on screen.</summary>
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset || DataContext is not TestingViewModel vm || vm.SelectedId is not { } id) return;
        Dispatcher.UIThread.Post(() =>
        {
            _restoring = true;
            try
            {
                TestTree.SelectedItem = vm.Rows.FirstOrDefault(r => r.Id == id);
            }
            finally
            {
                _restoring = false;
            }

            if (TestTree.SelectedItem is TestRowItem row) vm.Select(row);
        }, DispatcherPriority.Background);
    }
}
