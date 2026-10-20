using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Wicked.App.Shell;
using Wicked.App.ViewModels;

namespace Wicked.App.Views;

public partial class MainWindow : Window
{
    private const int SideColumn = 1;
    private const int AssistantColumn = 5;

    // The widths the user dragged the panels to, restored when a hidden panel comes back.
    private GridLength _sideWidth = new(260);
    private GridLength _assistantWidth = new(380);
    private ShellViewModel? _shell;

    public MainWindow()
    {
        InitializeComponent();
        WindowFrame.Apply(this);
        WindowFrame.Drags(this, TitleBar);
        Opened += OnFirstOpened;
        DataContextChanged += (_, _) =>
        {
            if (_shell is not null) _shell.PropertyChanged -= OnShellChanged;
            _shell = DataContext as ShellViewModel;
            if (_shell is null) return;
            _shell.PropertyChanged += OnShellChanged;
            ApplyColumns();
        };
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.SidebarVisible) or nameof(ShellViewModel.AssistantVisible)) ApplyColumns();
    }

    /// <summary>A hidden panel gives its column back to the editor; shown again, it returns at the width it had.</summary>
    private void ApplyColumns()
    {
        if (_shell is null) return;
        var columns = Body.ColumnDefinitions;
        if (columns[SideColumn].Width.Value > 0) _sideWidth = columns[SideColumn].Width;
        if (columns[AssistantColumn].Width.Value > 0) _assistantWidth = columns[AssistantColumn].Width;
        columns[SideColumn].Width = _shell.SidebarVisible ? _sideWidth : new GridLength(0);
        columns[SideColumn + 1].Width = new GridLength(_shell.SidebarVisible ? 6 : 0);
        columns[AssistantColumn].Width = _shell.AssistantVisible ? _assistantWidth : new GridLength(0);
        columns[AssistantColumn - 1].Width = new GridLength(_shell.AssistantVisible ? 6 : 0);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximize(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // A maximised window has no edge to resize from and no border to show.
        if (change.Property == WindowStateProperty && WindowDecorations == WindowDecorations.None)
        {
            Frame.BorderThickness = new Thickness(WindowState == WindowState.Normal ? 1 : 0);
        }
    }

    private void OnFirstOpened(object? sender, EventArgs e)
    {
        // Opened fires on every Show; this runs once.
        Opened -= OnFirstOpened;
        WindowingBackend.WindowOpened(this);
    }
}
