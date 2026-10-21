using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
using Wicked.Editor.Syntax;

namespace Wicked.App.Chat;

/// <summary>
/// The lines of one code block, drawn in the colours the highlighter gave them. Monospaced, so the height is a line count
/// times a line height with no layout, and only the lines in sight inside the message list's scroll viewer are shaped:
/// a 2000-line block costs what the 40 on screen cost. Text can be selected with the mouse and copied with Ctrl+C.
/// </summary>
public sealed class CodeText : Control
{
    public const double FontSize = 12.5;
    public const double LineHeight = 19;
    public static readonly Thickness Padding = new(12, 8, 12, 8);

    private static readonly Typeface Regular = new(ChatFonts.Mono);
    private static readonly Dictionary<uint, IBrush> Brushes = [];
    private static double _advance;

    private readonly Dictionary<int, (HighlightedLine Line, FormattedText Text)> _shaped = [];
    private IReadOnlyList<HighlightedLine> _lines = [];
    private HighlightedLine[] _display = [];
    private int[]?[] _maps = [];
    private int _longest;
    private ScrollViewer? _scroll;
    private (int Line, int Column)? _anchor;
    private (int Line, int Column) _caret;
    private bool _selecting;

    public CodeText()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        ClipToBounds = true;
    }

    public IReadOnlyList<HighlightedLine> Lines => _lines;

    /// <summary>The foreground for text no span colours.</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextBlock.ForegroundProperty.AddOwner<CodeText>();

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<CodeText, IBrush?>(nameof(SelectionBrush));

    public IBrush? SelectionBrush
    {
        get => GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    static CodeText()
    {
        AffectsRender<CodeText>(ForegroundProperty, SelectionBrushProperty);
    }

    public void SetLines(IReadOnlyList<HighlightedLine> lines)
    {
        var heightChanged = lines.Count != _lines.Count;
        var widthChanged = false;
        var longest = 0;
        // Tabs are shown as spaces to the next stop; the map takes a column on screen back to the text, so a copy keeps its tabs.
        var display = new HighlightedLine[lines.Count];
        var maps = new int[]?[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Text.Contains('\t')) (display[i], maps[i]) = Expand(lines[i]);
            else display[i] = lines[i];
            longest = Math.Max(longest, display[i].Text.Length);
        }

        _display = display;
        _maps = maps;
        if (longest != _longest)
        {
            _longest = longest;
            widthChanged = true;
        }

        _lines = lines;
        if (_anchor is { } a && (a.Line >= lines.Count || _caret.Line >= lines.Count)) ClearSelection();
        if (heightChanged || widthChanged) InvalidateMeasure();
        InvalidateVisual();
    }

    /// <summary>The text under the selection, or all of it with none.</summary>
    public string SelectedText
    {
        get
        {
            if (Range() is not { } range) return string.Join('\n', _lines.Select(l => l.Text));
            var (from, to) = range;
            if (from.Line == to.Line) return Slice(from.Line, from.Column, to.Column);
            var parts = new List<string> { Slice(from.Line, from.Column, int.MaxValue) };
            for (var i = from.Line + 1; i < to.Line; i++) parts.Add(_lines[i].Text);
            parts.Add(Slice(to.Line, 0, to.Column));
            return string.Join('\n', parts);
        }
    }

    public bool HasSelection => Range() is not null;

    /// <summary>The text of a line between two columns on screen.</summary>
    private string Slice(int line, int fromColumn, int toColumn)
    {
        var text = _lines[line].Text;
        var map = _maps[line];
        int Index(int column) => map is null ? Math.Clamp(column, 0, text.Length) : map[Math.Clamp(column, 0, map.Length - 1)];
        var from = Index(fromColumn);
        var to = Math.Max(from, Index(toColumn));
        return text[from..to];
    }

    /// <summary>A line with its tabs spread to 4-column stops, spans moved with the text, and the map from screen column back to the text.</summary>
    private static (HighlightedLine Line, int[] Map) Expand(HighlightedLine line)
    {
        var text = new System.Text.StringBuilder();
        var map = new List<int>();
        var columnOf = new int[line.Text.Length + 1];
        for (var i = 0; i < line.Text.Length; i++)
        {
            columnOf[i] = text.Length;
            if (line.Text[i] == '\t')
            {
                var width = 4 - text.Length % 4;
                for (var k = 0; k < width; k++)
                {
                    map.Add(i);
                    text.Append(' ');
                }
            }
            else
            {
                map.Add(i);
                text.Append(line.Text[i]);
            }
        }

        columnOf[line.Text.Length] = text.Length;
        map.Add(line.Text.Length);
        var spans = line.Spans.Select(sp => sp with { Start = columnOf[sp.Start], Length = columnOf[Math.Min(sp.Start + sp.Length, line.Text.Length)] - columnOf[sp.Start] }).ToArray();
        return (new HighlightedLine(text.ToString(), spans), [.. map]);
    }

    private ((int Line, int Column) From, (int Line, int Column) To)? Range()
    {
        if (_anchor is not { } a || a == _caret) return null;
        return a.Line < _caret.Line || (a.Line == _caret.Line && a.Column < _caret.Column) ? (a, _caret) : (_caret, a);
    }

    private void ClearSelection()
    {
        _anchor = null;
        InvalidateVisual();
    }

    private static double Advance
    {
        get
        {
            if (_advance > 0) return _advance;
            var probe = new FormattedText("MMMMMMMMMMMMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Regular, FontSize, Avalonia.Media.Brushes.White);
            return _advance = probe.WidthIncludingTrailingWhitespace / 20;
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Padding.Left + Padding.Right + _longest * Advance, Padding.Top + Padding.Bottom + _lines.Count * LineHeight);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Only the lines inside the scroll viewer's window are shaped, so the code redraws as the list scrolls.
        _scroll = this.GetVisualAncestors().OfType<ScrollViewer>().LastOrDefault();
        if (_scroll is not null) _scroll.ScrollChanged += OnScrolled;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_scroll is not null) _scroll.ScrollChanged -= OnScrolled;
        _scroll = null;
        _shaped.Clear();
    }

    private void OnScrolled(object? sender, ScrollChangedEventArgs e) => InvalidateVisual();

    private (int First, int Last) Visible()
    {
        var count = _lines.Count;
        if (count == 0) return (0, -1);
        var top = 0.0;
        var bottom = Bounds.Height;
        if (_scroll is { } scroll)
        {
            var origin = scroll.TranslatePoint(new Point(0, 0), this);
            if (origin is { } o)
            {
                top = o.Y;
                bottom = o.Y + scroll.Viewport.Height;
            }
        }

        var first = (int)Math.Floor((top - Padding.Top) / LineHeight) - 1;
        var last = (int)Math.Ceiling((bottom - Padding.Top) / LineHeight) + 1;
        return (Math.Clamp(first, 0, count - 1), Math.Clamp(last, 0, count - 1));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var (first, last) = Visible();
        if (last < first) return;
        var range = Range();
        var selection = SelectionBrush;
        var plain = Foreground ?? Avalonia.Media.Brushes.White;
        for (var i = first; i <= last; i++)
        {
            var y = Padding.Top + i * LineHeight;
            if (range is { } r && selection is not null && i >= r.From.Line && i <= r.To.Line)
            {
                var from = i == r.From.Line ? r.From.Column : 0;
                var to = i == r.To.Line ? r.To.Column : _display[i].Text.Length + 1;
                context.FillRectangle(selection, new Rect(Padding.Left + from * Advance, y, Math.Max(2, (to - from) * Advance), LineHeight));
            }

            var line = _display[i];
            if (line.Text.Length == 0) continue;
            if (!_shaped.TryGetValue(i, out var cached) || !ReferenceEquals(cached.Line, line))
            {
                cached = (line, Shape(line, plain));
                _shaped[i] = cached;
            }

            context.DrawText(cached.Text, new Point(Padding.Left, y + (LineHeight - cached.Text.Height) / 2));
        }

        // Shaped lines far from the window are dropped, so a long block does not keep its whole text shaped.
        if (_shaped.Count > 400) _shaped.Clear();
    }

    private static FormattedText Shape(HighlightedLine line, IBrush plain)
    {
        var text = new FormattedText(line.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Regular, FontSize, plain);
        foreach (var span in line.Spans)
        {
            if (span.Start + span.Length > line.Text.Length) continue;
            text.SetForegroundBrush(BrushFor(span.Color), span.Start, span.Length);
            if ((span.Style & TokenStyle.Bold) != 0) text.SetFontWeight(FontWeight.Bold, span.Start, span.Length);
            if ((span.Style & TokenStyle.Italic) != 0) text.SetFontStyle(FontStyle.Italic, span.Start, span.Length);
        }

        return text;
    }

    private static IBrush BrushFor(uint argb)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(argb, out var brush))
            {
                brush = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromUInt32(argb));
                Brushes[argb] = brush;
            }

            return brush;
        }
    }

    // ── Selecting ────────────────────────────────────────────────────────────────────────────────

    private (int Line, int Column) PositionAt(Point point)
    {
        var line = Math.Clamp((int)Math.Floor((point.Y - Padding.Top) / LineHeight), 0, Math.Max(0, _lines.Count - 1));
        var length = _display.Length == 0 ? 0 : _display[line].Text.Length;
        var column = Math.Clamp((int)Math.Round((point.X - Padding.Left) / Advance), 0, length);
        return (line, column);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || _lines.Count == 0) return;
        Focus();
        _caret = PositionAt(e.GetPosition(this));
        _anchor = _caret;
        _selecting = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting) return;
        _caret = PositionAt(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_selecting) return;
        _selecting = false;
        e.Pointer.Capture(null);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        if (!_selecting) ClearSelection();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyModifiers != KeyModifiers.Control) return;
        if (e.Key == Key.C && Range() is not null)
        {
            e.Handled = true;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) _ = clipboard.SetTextAsync(SelectedText);
        }
        else if (e.Key == Key.A && _lines.Count > 0)
        {
            e.Handled = true;
            _anchor = (0, 0);
            _caret = (_lines.Count - 1, _display[^1].Text.Length);
            InvalidateVisual();
        }
    }
}
