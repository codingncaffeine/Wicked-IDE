using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Wicked.App.Editor;

namespace Wicked.App.Views;

/// <summary>The find widget's keys, VS Code's: Enter next, Shift+Enter previous, Escape close, Alt+C/W/R the toggles.</summary>
public partial class FindWidget : UserControl
{
    private FindViewModel? _find;

    public FindWidget()
    {
        InitializeComponent();
        QueryBox.AddHandler(KeyDownEvent, OnQueryKey, RoutingStrategies.Tunnel);
        ReplaceBox.AddHandler(KeyDownEvent, OnReplaceKey, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) =>
        {
            if (_find is not null) _find.FocusRequested -= FocusQuery;
            _find = DataContext as FindViewModel;
            if (_find is not null) _find.FocusRequested += FocusQuery;
        };
    }

    private void FocusQuery() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        QueryBox.Focus();
        QueryBox.SelectAll();
    }, Avalonia.Threading.DispatcherPriority.Loaded);

    private void OnQueryKey(object? sender, KeyEventArgs e)
    {
        if (_find is null || !Common(e)) return;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                _find.Previous();
                break;
            case Key.Enter:
                _find.Next();
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.None:
                _find.Recall(-1);
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.None:
                _find.Recall(1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnReplaceKey(object? sender, KeyEventArgs e)
    {
        if (_find is null || !Common(e)) return;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Control | KeyModifiers.Alt):
                _find.ReplaceAll();
                break;
            case Key.Enter:
                _find.ReplaceOne();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Keys both boxes share; false when the key was taken.</summary>
    private bool Common(KeyEventArgs e)
    {
        if (_find is null) return false;
        var alt = e.KeyModifiers == KeyModifiers.Alt;
        if (e.Key == Key.Escape) _find.Close();
        else if (alt && e.Key == Key.C) _find.MatchCase = !_find.MatchCase;
        else if (alt && e.Key == Key.W) _find.WholeWord = !_find.WholeWord;
        else if (alt && e.Key == Key.R) _find.Regex = !_find.Regex;
        else if (alt && e.Key == Key.L) _find.InSelection = !_find.InSelection;
        else if (e.Key == Key.H && e.KeyModifiers == KeyModifiers.Control) _find.ShowReplace = true;
        else return true;
        e.Handled = true;
        return false;
    }
}
