using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Wicked.Editor.Syntax;

namespace Wicked.App.Chat;

/// <summary>
/// A fenced code block in a reply: a language label, Copy and Insert, and the lines highlighted on the highlighter's
/// thread. A block that grows while the reply streams keeps the colours of the lines it already had and shows the new
/// ones plain until the highlighter catches up (a frame or two).
/// </summary>
public partial class CodeBlockView : UserControl
{
    private MdCodeBlock? _block;
    private string[] _plain = [];
    private CodeSession? _session;
    private bool _attached;

    public CodeBlockView()
    {
        InitializeComponent();
    }

    private ICodeActions? Actions => ChatContext.GetActions(this);

    public MdCodeBlock? Block => _block;

    /// <summary>What the block shows now, highlighted or not.</summary>
    public string Text => Body.SelectedText;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ChatContext.ActionsProperty) return;
        if (change.OldValue is ICodeActions old) old.CanInsertChanged -= OnCanInsertChanged;
        if (change.NewValue is ICodeActions now) now.CanInsertChanged += OnCanInsertChanged;
        UpdateInsert();
    }

    private void OnCanInsertChanged(object? sender, EventArgs e) => UpdateInsert();

    private void UpdateInsert()
    {
        InsertCode.IsEnabled = Actions?.CanInsert == true;
        ToolTip.SetTip(InsertCode, InsertCode.IsEnabled ? "Insert at the caret, replacing the selection" : "Open a file to insert code into");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateInsert();
        if (_block is not null) Highlight();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _session?.Close();
        _session = null;
    }

    public void Update(MdCodeBlock block)
    {
        if (block.Equals(_block)) return;
        var languageChanged = _block?.Language != block.Language;
        _block = block;
        LanguageLabel.Text = block.Language ?? "text";
        _plain = [.. block.Lines];
        // The lines that were already highlighted stay as they are; the rest show plain until their colours arrive.
        var current = Body.Lines;
        var merged = new HighlightedLine[_plain.Length];
        for (var i = 0; i < merged.Length; i++)
        {
            merged[i] = !languageChanged && i < current.Count && string.Equals(current[i].Text, _plain[i], StringComparison.Ordinal)
                ? current[i]
                : new HighlightedLine(_plain[i], []);
        }

        Body.SetLines(merged);
        if (_attached) Highlight();
    }

    private void Highlight()
    {
        _session ??= ChatHighlighter.Shared.Open(_block?.Language, lines => Dispatcher.UIThread.Post(() => Apply(lines), DispatcherPriority.Background));
        _session.Update(_plain);
    }

    private void Apply(IReadOnlyList<HighlightedLine> lines)
    {
        if (_block is null) return;
        var merged = new HighlightedLine[_plain.Length];
        for (var i = 0; i < merged.Length; i++)
        {
            merged[i] = i < lines.Count && string.Equals(lines[i].Text, _plain[i], StringComparison.Ordinal)
                ? lines[i]
                : i < Body.Lines.Count && string.Equals(Body.Lines[i].Text, _plain[i], StringComparison.Ordinal) ? Body.Lines[i] : new HighlightedLine(_plain[i], []);
        }

        Body.SetLines(merged);
    }

    private void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (_block is null) return;
        Actions?.Copy(string.Join('\n', _plain));
        CopyLabel.Text = "Copied";
        DispatcherTimer.RunOnce(() => CopyLabel.Text = "Copy", TimeSpan.FromSeconds(1.5));
    }

    private void OnInsert(object? sender, RoutedEventArgs e)
    {
        if (_block is not null && Actions?.CanInsert == true) Actions.Insert(string.Join('\n', _plain));
    }
}
