using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Wicked.App.ViewModels;

namespace Wicked.App.Views;

public partial class SettingsView : UserControl
{
    private SettingsViewModel? _model;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_model is not null) _model.SectionRequested -= Show;
            _model = DataContext as SettingsViewModel;
            if (_model is null) return;
            _model.SectionRequested += Show;
            _model.FolderPicker = PickFolderAsync;
        };
    }

    private void OnJump(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string section }) Show(section);
    }

    /// <summary>Scrolls a section's heading into view; the token box takes the keyboard when the section is Models (that is what sends people there).</summary>
    private void Show(string section) => Dispatcher.UIThread.Post(() =>
    {
        var name = "Section" + char.ToUpperInvariant(section[0]) + section[1..];
        this.FindControl<Control>(name)?.BringIntoView();
        if (section == "models") this.FindControl<TextBox>("TokenBox")?.Focus();
    }, DispatcherPriority.Loaded);

    private async Task<string?> PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Where should new models go?", AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
