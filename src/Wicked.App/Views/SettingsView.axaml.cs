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
        if (sender is not Button { Tag: string section }) return;
        _model?.ShowSectionsCommand.Execute(null);
        Show(section);
    }

    /// <summary>A setting's "…": copy its id, or the line settings.json would hold (VS Code's gear menu).</summary>
    private void OnSettingMore(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SettingRow row } button || _model is null) return;
        var copyId = new MenuItem { Header = "Copy Setting ID" };
        copyId.Click += (_, _) => _ = CopyAsync(row.Key);
        var items = new List<MenuItem> { copyId };
        if (!row.Info.Secret)
        {
            var copyJson = new MenuItem { Header = "Copy Setting as JSON" };
            copyJson.Click += (_, _) => _ = CopyAsync(_model.SettingJson(row));
            items.Add(copyJson);
        }

        items.Add(new MenuItem { Header = "Reset Setting", IsEnabled = row.IsModified, Command = row.ResetCommand });
        new ContextMenu { ItemsSource = items }.Open(button);
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
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
