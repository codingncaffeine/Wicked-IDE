using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Wicked.App.Shell;

namespace Wicked.App.Chat;

/// <summary>
/// Draws parsed Markdown blocks. Given a new list it keeps every child whose block is unchanged, updates the one that
/// grew (a paragraph's text, a code block's lines) and only builds what is new, so a reply that streams at 60 tokens a
/// second costs the work of its last block per frame. The parsing happened on a worker; this touches no text but
/// what it shows.
/// </summary>
public sealed class MarkdownView : StackPanel
{
    public static readonly StyledProperty<IReadOnlyList<MdBlock>?> BlocksProperty = AvaloniaProperty.Register<MarkdownView, IReadOnlyList<MdBlock>?>(nameof(Blocks));

    private static readonly AttachedProperty<MdBlock?> BlockProperty = AvaloniaProperty.RegisterAttached<MarkdownView, Control, MdBlock?>("Block");

    public MarkdownView()
    {
        Spacing = 8;
    }


    public IReadOnlyList<MdBlock>? Blocks
    {
        get => GetValue(BlocksProperty);
        set => SetValue(BlocksProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlocksProperty) Reconcile(Blocks ?? []);
    }

    private void Reconcile(IReadOnlyList<MdBlock> blocks)
    {
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (i < Children.Count)
            {
                var child = Children[i];
                var shown = child.GetValue(BlockProperty);
                if (ReferenceEquals(shown, block) || (shown is not null && shown.Equals(block)))
                {
                    child.SetValue(BlockProperty, block);
                    continue;
                }

                if (TryUpdate(child, block))
                {
                    child.SetValue(BlockProperty, block);
                    continue;
                }

                var built = Build(block);
                built.SetValue(BlockProperty, block);
                Children[i] = built;
            }
            else
            {
                var built = Build(block);
                built.SetValue(BlockProperty, block);
                Children.Add(built);
            }
        }

        while (Children.Count > blocks.Count) Children.RemoveAt(Children.Count - 1);
    }

    private bool TryUpdate(Control child, MdBlock block)
    {
        switch (block)
        {
            case MdParagraph paragraph when child is MarkdownText text && text.Tag is null:
                text.Show(paragraph.Inlines);
                return true;
            case MdHeading heading when child is MarkdownText text && text.Tag is int level && level == heading.Level:
                text.Show(heading.Inlines);
                return true;
            case MdCodeBlock code when child is CodeBlockView view:
                view.Update(code);
                return true;
            case MdList list when child is ListHost host && host.Ordered == list.Ordered:
                host.Update(list);
                return true;
            default:
                return false;
        }
    }

    private Control Build(MdBlock block)
    {
        switch (block)
        {
            case MdParagraph paragraph:
            {
                var text = new MarkdownText();
                text.Show(paragraph.Inlines);
                return text;
            }

            case MdHeading heading:
            {
                var text = new MarkdownText { Tag = heading.Level, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, heading.Level <= 2 ? 6 : 2, 0, 0) };
                text.FontSize = heading.Level switch
                {
                    1 => 19,
                    2 => 16.5,
                    3 => 14.5,
                    _ => 13.5,
                };
                text.Show(heading.Inlines);
                return text;
            }

            case MdCodeBlock code:
            {
                var view = new CodeBlockView();
                view.Update(code);
                return view;
            }

            case MdQuote quote:
            {
                var inner = new MarkdownView { Blocks = quote.Blocks };
                var bar = new Border { Classes = { "quotebar" }, Child = inner };
                return bar;
            }

            case MdList list:
            {
                var host = new ListHost(list.Ordered);
                host.Update(list);
                return host;
            }

            case MdTable table:
                return BuildTable(table);
            case MdRule:
                return new Border { Classes = { "rule" } };
            default:
                return new Control();
        }
    }

    private static Control BuildTable(MdTable table)
    {
        var columns = Math.Max(table.Header.Count, table.Rows.Count == 0 ? 0 : table.Rows.Max(r => r.Count));
        var grid = new Grid { Classes = { "mdtable" } };
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        var row = 0;
        void AddRow(IReadOnlyList<IReadOnlyList<MdInline>> cells, bool header)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var c = 0; c < columns; c++)
            {
                var cell = new Border { Classes = { header ? "mdhead" : "mdcell" } };
                var text = new MarkdownText { MaxWidth = 280, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal };
                if (c < cells.Count) text.Show(cells[c]);
                if (c < table.Alignments.Count) text.TextAlignment = table.Alignments[c] switch
                {
                    MdAlign.Center => TextAlignment.Center,
                    MdAlign.Right => TextAlignment.Right,
                    _ => TextAlignment.Left,
                };

                cell.Child = text;
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }

            row++;
        }

        AddRow(table.Header, header: true);
        foreach (var r in table.Rows) AddRow(r, header: false);
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = grid };
    }

    /// <summary>A list: one row per item, a marker beside the item's own blocks.</summary>
    private sealed class ListHost : StackPanel
    {
        public ListHost(bool ordered)
        {
            Ordered = ordered;
            Spacing = 4;
        }

        public bool Ordered { get; }

        public void Update(MdList list)
        {
            for (var i = 0; i < list.Items.Count; i++)
            {
                var item = list.Items[i];
                Grid row;
                if (i < Children.Count) row = (Grid)Children[i];
                else
                {
                    row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*") };
                    row.Children.Add(new Border { HorizontalAlignment = HorizontalAlignment.Left });
                    var content = new MarkdownView { Spacing = 4 };
                    Grid.SetColumn(content, 1);
                    row.Children.Add(content);
                    Children.Add(row);
                }

                var marker = (Border)row.Children[0];
                marker.Child = Marker(list, item, i);
                ((MarkdownView)row.Children[1]).Blocks = item.Blocks;
            }

            while (Children.Count > list.Items.Count) Children.RemoveAt(Children.Count - 1);
        }

        private static Control Marker(MdList list, MdListItem item, int index)
        {
            if (item.Checked is { } done)
            {
                return done
                    ? new Icon { Data = Res.Geometry("Icon.Check"), Size = 14, Foreground = Res.Brush("Brush.Accent"), Margin = new Thickness(0, 3, 0, 0) }
                    : new Border { Classes = { "taskbox" } };
            }

            var text = list.Ordered ? $"{list.Start + index}." : "•";
            return new TextBlock { Text = text, Foreground = Res.Brush("Brush.Text.Secondary"), HorizontalAlignment = HorizontalAlignment.Left };
        }
    }
}

/// <summary>
/// Selectable text built from inlines. A link in it opens on a click that is not the end of a selection; the pointer
/// turns to a hand over one.
/// </summary>
public sealed class MarkdownText : SelectableTextBlock
{
    private readonly List<(int Start, int End, string Url)> _links = [];

    public MarkdownText()
    {
        TextWrapping = TextWrapping.Wrap;
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);



    /// <summary>Replaces the text with <paramref name="inlines"/>.</summary>
    public void Show(IReadOnlyList<MdInline> inlines)
    {
        _links.Clear();
        var position = 0;
        var collection = new InlineCollection();
        Fill(collection, inlines, ref position);
        Inlines = collection;
    }

    private void Fill(InlineCollection into, IReadOnlyList<MdInline> inlines, ref int position)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case MdText text:
                    into.Add(new Run(text.Text));
                    position += text.Text.Length;
                    break;
                case MdCode code:
                    into.Add(new Run(code.Text)
                    {
                        FontFamily = ChatFonts.Mono,
                        FontSize = FontSize * 0.92,
                        Background = Res.Brush("Brush.Raised"),
                        Foreground = Res.Brush("Brush.Accent.Hover"),
                    });
                    position += code.Text.Length;
                    break;
                case MdBreak:
                    into.Add(new LineBreak());
                    position += 1;
                    break;
                case MdStyled styled:
                {
                    Span span = styled.Style switch
                    {
                        MdStyle.Strong => new Bold(),
                        MdStyle.Emphasis => new Italic(),
                        _ => new Span { TextDecorations = Avalonia.Media.TextDecorations.Strikethrough },
                    };
                    Fill(span.Inlines, styled.Children, ref position);
                    into.Add(span);
                    break;
                }

                case MdLink link:
                {
                    var span = new Span
                    {
                        Foreground = Res.Brush("Brush.Accent.Hover"),
                        TextDecorations = Avalonia.Media.TextDecorations.Underline,
                    };
                    var start = position;
                    Fill(span.Inlines, link.Children, ref position);
                    if (position > start) _links.Add((start, position, link.Url));
                    into.Add(span);
                    break;
                }
            }
        }
    }

    private string? LinkAt(Point point)
    {
        if (_links.Count == 0) return null;
        var hit = TextLayout.HitTestPoint(point);
        if (!hit.IsInside) return null;
        var index = hit.TextPosition;
        foreach (var (start, end, url) in _links)
        {
            if (index >= start && index < end) return url;
        }

        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) is null ? new Cursor(StandardCursorType.Ibeam) : new Cursor(StandardCursorType.Hand);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || SelectionStart != SelectionEnd) return;
        if (LinkAt(e.GetPosition(this)) is { } url) ChatContext.GetActions(this)?.OpenLink(url);
    }
}

/// <summary>Theme resources by key, for the controls the chat builds in code.</summary>
internal static class Res
{
    public static IBrush? Brush(string key) => Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as IBrush : null;

    public static Geometry? Geometry(string key) => Application.Current?.TryFindResource(key, Application.Current.ActualThemeVariant, out var value) == true ? value as Geometry : null;
}
