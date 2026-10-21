using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Wicked.App.Terminal;

/// <summary>The find widget's keys, the editor's: Enter next, Shift+Enter previous, Escape close, Alt+C/W/R the toggles.</summary>
public partial class TerminalFindWidget : UserControl
{
    private TerminalFindViewModel? _find;

    public TerminalFindWidget()
    {
        InitializeComponent();
        QueryBox.AddHandler(KeyDownEvent, OnQueryKey, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_find is not null) _find.FocusRequested -= FocusQuery;
            _find = DataContext as TerminalFindViewModel;
            if (_find is not null) _find.FocusRequested += FocusQuery;
        };
    }

    private void FocusQuery() => Dispatcher.UIThread.Post(() =>
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }, DispatcherPriority.Loaded);

    private void OnQueryKey(object? sender, KeyEventArgs e)
    {
        if (_find is null) return;
        var alt = e.KeyModifiers == KeyModifiers.Alt;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                _find.Previous();
                break;
            case Key.Enter:
                _find.Next();
                break;
            case Key.Escape:
                _find.Close();
                break;
            case Key.C when alt:
                _find.MatchCase = !_find.MatchCase;
                break;
            case Key.W when alt:
                _find.WholeWord = !_find.WholeWord;
                break;
            case Key.R when alt:
                _find.Regex = !_find.Regex;
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
