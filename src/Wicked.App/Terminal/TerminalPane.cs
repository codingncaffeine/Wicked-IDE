using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Wicked.Editor.Layout;

namespace Wicked.App.Terminal;

/// <summary>One terminal as the window shows it: the view, and the find widget over its top right corner.</summary>
public sealed class TerminalPane : Grid
{
    public TerminalPane(EditorFonts fonts, TerminalTheme? theme = null)
    {
        View = new TerminalView(fonts, theme);
        Find = new TerminalFindViewModel(View);
        var widget = new TerminalFindWidget
        {
            DataContext = Find,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 18, 0),
        };
        widget.Bind(IsVisibleProperty, new Avalonia.Data.Binding(nameof(TerminalFindViewModel.IsOpen)) { Source = Find });
        Children.Add(View);
        Children.Add(widget);
        View.FindRequested += async () => Find.Open(await View.SelectionTextAsync());
    }

    public TerminalView View { get; }

    public TerminalFindViewModel Find { get; }
}
