using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Wicked.App.Chat;
using Wicked.Editor.Model;

namespace Wicked.App.Previews;

/// <summary>
/// Markdown: Open Preview (Ctrl+Shift+V, Ctrl+K V to the side): the file drawn as Markdown, following its edits as they are
/// typed. Parsing happens on a worker a moment after typing stops; the view keeps every block that did not change. It
/// follows the document only while it is on screen.
/// </summary>
public sealed class MarkdownPreviewView : UserControl
{
    private readonly Document _document;
    private readonly MarkdownView _markdown = new() { Name = "PreviewBlocks" };
    private readonly DispatcherTimer _settle;
    private CancellationTokenSource? _parse;

    public MarkdownPreviewView(Document document, ICodeActions actions)
    {
        _document = document;
        ChatContext.SetActions(this, actions);
        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new Border
            {
                MaxWidth = 860,
                Padding = new Thickness(36, 24),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = _markdown,
            },
        };
        // Made stopped: the (interval, priority, handler) constructor starts a timer at once.
        _settle = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _settle.Tick += (_, _) =>
        {
            _settle.Stop();
            Render();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _document.Changed += OnChanged;
            Render();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _document.Changed -= OnChanged;
            _settle.Stop();
            _parse?.Cancel();
        };
    }

    /// <summary>The blocks drawn now (tests read them).</summary>
    public IReadOnlyList<MdBlock>? Blocks => _markdown.Blocks;

    private void OnChanged(DocumentChange change)
    {
        _settle.Stop();
        _settle.Start();
    }

    private void Render()
    {
        _parse?.Cancel();
        var cts = _parse = new CancellationTokenSource();
        var text = _document.Text;
        var version = _document.Version;
        _ = Task.Run(() => MarkdownParser.Parse(text.ToString()), cts.Token).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (!cts.IsCancellationRequested && _document.Version == version) _markdown.Blocks = task.Result;
            });
        }, TaskScheduler.Default);
    }
}
