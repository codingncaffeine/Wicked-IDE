using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Wicked.App.Editor;
using Wicked.Core.Lsp;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;

namespace Wicked.App.Language;

public sealed partial class EditorLanguageUi
{
    private EditorPopup? _peek;

    /// <summary>The peek card is open (tests).</summary>
    public bool PeekOpen => _peek?.IsOpen == true;

    /// <summary>The editor inside the peek card, once shown.</summary>
    public CodeEditor? PeekEditor { get; private set; }

    /// <summary>
    /// Alt+F12 (VS Code's Peek Definition): the definition's file in a read-only editor floating under the caret, scrolled
    /// to it, without leaving this file. Escape closes it; Enter or Open goes there.
    /// </summary>
    public async Task PeekDefinitionAsync()
    {
        if (Doc is not { } doc) return;
        var caret = _editor.Document.Primary.Active;
        IReadOnlyList<Wicked.Core.Lsp.Location>? found;
        try
        {
            found = await doc.RequestAsync((c, text, _) => c.GoToAsync("definition", doc.Uri, LspText.PositionOf(text, caret)));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return;
        }

        if (found is not { Count: > 0 } || LspPaths.ToPath(found[0].Uri) is not { } path)
        {
            _features.Tell("No definition found");
            return;
        }

        var target = found[0];
        var open = _features.OpenDocumentText(path);
        var text = open is not null ? await Task.Run(open.ToString) : await Task.Run(async () =>
        {
            var loaded = await Wicked.Core.Files.TextFile.ReadAsync(path, false, CancellationToken.None);
            return loaded.Kind == Wicked.Core.Files.FileKind.Text ? loaded.Text : null;
        });
        if (text is null) return;
        var services = await EditorServices.Ready;
        var document = new Document(text) { IsReadOnly = true, KeepsHistory = false };
        var peek = new CodeEditor(services, document, Languages.ForFile(path)) { Name = "PeekEditor", Height = 260, FilePath = path };
        peek.ApplySettings(_features.Settings with { Minimap = false });
        var start = LspText.OffsetOf(document.Text, target.Range.Start);
        var end = LspText.OffsetOf(document.Text, target.Range.End);
        PeekEditor = peek;

        var title = new TextBlock { Text = $"{Path.GetFileName(path)}  line {target.Range.Start.Line + 1}", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var go = new Button { Name = "PeekOpen", Content = "Open", Classes = { "secondary", "small" } };
        go.Click += (_, _) => GoFromPeek(path, target.Range);
        var close = new Button { Name = "PeekClose", Content = "Close", Classes = { "quiet", "small" } };
        close.Click += (_, _) => ClosePeek();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { go, close } };
        DockPanel.SetDock(buttons, Dock.Right);
        head.Children.Add(buttons);
        head.Children.Add(title);
        var body = new DockPanel { Width = 700 };
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);
        body.Children.Add(peek);
        peek.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ClosePeek();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                GoFromPeek(path, target.Range);
            }
        }, RoutingStrategies.Tunnel);
        _peek ??= new EditorPopup(_editor, "PeekCard", 740, 320);
        _peek.Content = body;
        var anchor = _editor.RectOf(caret);
        _peek.Show(new Rect(0, anchor.Y, 1, anchor.Height));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => peek.Select(start, end), Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void GoFromPeek(string path, Wicked.Core.Lsp.Range range)
    {
        ClosePeek();
        _ = _features.OpenLocation?.Invoke(path, range);
    }

    public void ClosePeek()
    {
        _peek?.Hide();
        PeekEditor?.Close();
        PeekEditor = null;
        _editor.Focus();
    }
}
