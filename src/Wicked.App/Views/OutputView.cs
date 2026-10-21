using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Wicked.App.Editor;
using Wicked.App.Shell;

namespace Wicked.App.Views;

/// <summary>
/// The Output panel: a channel picker and Clear, over a read-only editor showing the channel (so its text can be searched,
/// selected and copied like any file's). One editor per channel, made when the channel is first shown.
/// </summary>
public sealed class OutputView : DockPanel
{
    private readonly OutputLog _log;
    private readonly Func<Wicked.Core.Settings.WickedSettings> _settings;
    private readonly Dictionary<OutputChannel, CodeEditor> _editors = [];
    private readonly ContentControl _host = new();
    private readonly ComboBox _picker;

    public OutputView(OutputLog log, Func<Wicked.Core.Settings.WickedSettings> settings)
    {
        _log = log;
        _settings = settings;
        _picker = new ComboBox
        {
            Name = "OutputChannel",
            MinWidth = 200,
            ItemsSource = log.Channels,
            ItemTemplate = new FuncDataTemplate<OutputChannel>((c, _) => new TextBlock { Text = c?.Name }),
            PlaceholderText = "No output yet",
        };
        _picker.SelectionChanged += (_, _) =>
        {
            if (_picker.SelectedItem is OutputChannel c) log.Shown = c;
        };
        var clear = new Button { Name = "OutputClear", Content = new Icon { Data = (Avalonia.Media.Geometry)Application.Current!.FindResource("Icon.Trash2")! }, Classes = { "quiet" } };
        ToolTip.SetTip(clear, "Clear Output");
        clear.Click += (_, _) => log.Shown?.Clear();
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(10, 0, 10, 4), Children = { _picker, clear } };
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        Children.Add(_host);
        log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OutputLog.Shown)) _ = ShowAsync();
        };
        _ = ShowAsync();
    }

    private async Task ShowAsync()
    {
        if (_log.Shown is not { } channel) return;
        _picker.SelectedItem = channel;
        if (!_editors.TryGetValue(channel, out var editor))
        {
            var services = await EditorServices.Ready;
            if (!_editors.TryGetValue(channel, out editor))
            {
                editor = new CodeEditor(services, channel.Document, null) { Name = "OutputEditor" };
                editor.ApplySettings(_settings() with { Minimap = false, WordWrap = "on" });
                _editors[channel] = editor;
            }
        }

        _host.Content = editor;
    }
}
