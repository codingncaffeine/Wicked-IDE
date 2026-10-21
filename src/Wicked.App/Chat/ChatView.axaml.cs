using Avalonia.Media.Imaging;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Wicked.App.Chat;

/// <summary>
/// The Assistant panel's view: keys (Enter sends, Shift+Enter starts a line, Esc stops, Ctrl+N is a new chat), pictures
/// (attach, paste, drop), and keeping the newest words in view while a reply streams, unless the user has scrolled up.
/// </summary>
public partial class ChatView : UserControl
{
    private ScrollViewer? _scroll;
    private bool _stick = true;
    private ChatViewModel? _chat;

    public ChatView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        Composer.AddHandler(KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
        ComposerBox.AddHandler(DragDrop.DropEvent, OnDrop);
        ComposerBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        MessageList.TemplateApplied += OnListTemplate;
        DataContextChanged += (_, _) =>
        {
            if (_chat is not null) _chat.Messages.CollectionChanged -= OnMessagesChanged;
            _chat = DataContext as ChatViewModel;
            ChatContext.SetActions(this, _chat);
            if (_chat is not null) _chat.Messages.CollectionChanged += OnMessagesChanged;
        };
    }

    /// <summary>Puts the cursor in the composer.</summary>
    public void FocusComposer() => Dispatcher.UIThread.Post(() => Composer.Focus(), DispatcherPriority.Input);

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_chat is null) return;
        if (e.Key == Key.Escape && _chat.IsStreaming)
        {
            _chat.StopCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control)
        {
            _ = _chat.NewChatCommand.ExecuteAsync(null).ContinueWith(_ => FocusComposer(), TaskScheduler.FromCurrentSynchronizationContext());
            e.Handled = true;
        }
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (_chat is null) return;
        if (e.Key == Key.Enter && !e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            e.Handled = true;
            if (_chat.CanSend) _chat.SendCommand.Execute(null);
        }
        else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control && _chat.AcceptsImages)
        {
            _ = PasteImageAsync();
        }
    }

    /// <summary>A choice in a chip's menu closes the menu.</summary>
    private void OnMenuRowClick(object? sender, RoutedEventArgs e) =>
        // After the button's command has run: hiding takes the button out of the tree, and its command binding with it.
        Dispatcher.UIThread.Post(() =>
        {
            ModelChip.Flyout?.Hide();
            PromptChip.Flyout?.Hide();
        }, DispatcherPriority.Input);

    // ── Pictures ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>A picture on the clipboard goes with the message; text on it pastes into the box as usual.</summary>
    private async Task PasteImageAsync()
    {
        if (_chat is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            if (await clipboard.TryGetBitmapAsync() is not { } bitmap) return;
            var data = await Task.Run(() =>
            {
                using var stream = new MemoryStream();
                bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                return stream.ToArray();
            });
            _chat.AddImage("Pasted picture.png", data, "image/png");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            Wicked.Core.Log.Warn($"The clipboard picture could not be read: {ex.Message}");
        }
    }

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (_chat is null || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage) return;
        var picked = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach pictures",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Pictures") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] }],
        });
        await _chat.AddImageFilesAsync(picked.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = _chat?.AcceptsImages == true && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_chat is null) return;
        var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
        await _chat.AddImageFilesAsync(paths);
    }

    // ── Keeping the newest words in view ─────────────────────────────────────────────────────────

    private void OnListTemplate(object? sender, TemplateAppliedEventArgs e)
    {
        _scroll = e.NameScope.Find<ScrollViewer>("MessageScroll");
        if (_scroll is null) return;
        _scroll.ScrollChanged += (_, args) =>
        {
            // The content grew: follow it, if the user is at the end. Otherwise the user moved: remember whether they left the end.
            if (args.ExtentDelta.Y != 0 || args.ViewportDelta.Y != 0)
            {
                if (_stick) _scroll.ScrollToEnd();
            }
            else if (args.OffsetDelta.Y != 0)
            {
                _stick = _scroll.Offset.Y >= _scroll.Extent.Height - _scroll.Viewport.Height - 24;
            }
        };
    }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // A message the user just sent brings them back to the end.
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
        _stick = true;
        Dispatcher.UIThread.Post(() => _scroll?.ScrollToEnd(), DispatcherPriority.Loaded);
    }
}
