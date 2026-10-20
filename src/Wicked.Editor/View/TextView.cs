using SkiaSharp;
using Wicked.Editor.Layout;
using Wicked.Editor.Model;
using Wicked.Editor.Search;
using Wicked.Editor.Syntax;
using Wicked.Editor.Text;

namespace Wicked.Editor.View;

/// <summary>
/// The editor's view of a document, on the UI thread: where it is scrolled, which lines are in sight, where a point
/// falls in the text, and the <see cref="EditorFrame"/> to draw. Building a frame looks up shaped lines in the layout
/// cache and asks the layout thread for the ones missing; it never measures or shapes text itself. A line not shaped
/// yet is placed on the monospaced grid, which is exact for the ASCII most code is made of.
/// </summary>
public sealed class TextView
{
    private const float ScrollbarSize = 12;
    private const float MinThumb = 24;

    private readonly LayoutService _layout;
    private readonly Dictionary<int, string> _lineText = [];
    private readonly Dictionary<int, ShapedLine> _shapedInSight = [];
    private Rope? _textFor;
    private float _widest;
    private int _lastFirstLine = -1;

    public TextView(Document document, LayoutService layout, EditOptions options, EditorTheme? theme = null)
    {
        Document = document;
        _layout = layout;
        Options = options;
        Theme = theme ?? EditorTheme.WickedDark;
    }

    public Document Document { get; }

    public EditOptions Options { get; set; }

    public EditorTheme Theme { get; set; }

    public EditorFonts Fonts => _layout.Fonts;

    /// <summary>The newest highlighting; may lag the text by an edit or two.</summary>
    public SyntaxSnapshot? Syntax { get; set; }

    public float Width { get; private set; }

    public float Height { get; private set; }

    public float ScrollY { get; private set; }

    public float ScrollX { get; private set; }

    public bool Focused { get; set; } = true;

    /// <summary>The editor surface follows the code palette's own colours (a palette chosen apart from the window theme, D28).</summary>
    public bool FollowPalette { get; set; }

    // Worked out once per palette, not per line: a frame looks it up for every line it keys.
    private EditorTheme EffectiveTheme
    {
        get
        {
            var colors = FollowPalette ? Syntax?.Colors : null;
            if (!ReferenceEquals(colors, _paletteFor) || !ReferenceEquals(Theme, _themeFor))
            {
                _paletteFor = colors;
                _themeFor = Theme;
                _effective = colors is null ? Theme : Theme.WithPalette(colors.Gui);
            }

            return _effective ?? Theme;
        }
    }

    private ColorTable? _paletteFor;
    private EditorTheme? _themeFor;
    private EditorTheme? _effective;

    public bool CaretsVisible { get; set; } = true;

    /// <summary>The find widget's matches (for the version they were found in), and which one it is on.</summary>
    public SearchResult Found { get; set; } = SearchResult.None;

    public int CurrentMatch { get; set; } = -1;

    /// <summary>The text an input method is composing at the primary caret, or null.</summary>
    public string? Preedit { get; set; }

    /// <summary>Where the primary caret is drawn in the view, for an input method's candidate window.</summary>
    public SKRect PrimaryCaretRect
    {
        get
        {
            var (line, column) = Document.Text.Position(Document.Primary.Active);
            var x = TextLeft - ScrollX + XOf(line, column);
            var y = line * LineHeight - ScrollY;
            return new SKRect(x, y, x + Theme.CaretWidth, y + LineHeight);
        }
    }

    /// <summary>A scrollbar thumb is being dragged or hovered: it is drawn brighter.</summary>
    public bool ThumbActive { get; set; }

    public float LineHeight => Fonts.LineHeight;

    public int FirstVisibleLine => Math.Clamp((int)(ScrollY / LineHeight), 0, Document.Text.LineCount - 1);

    public int LastVisibleLine => Math.Clamp((int)((ScrollY + Height) / LineHeight), 0, Document.Text.LineCount - 1);

    /// <summary>The gutter's width: room for the widest line number, with space around it.</summary>
    public float GutterWidth
    {
        get
        {
            var digits = Math.Max(3, (int)Math.Floor(Math.Log10(Math.Max(1, Document.Text.LineCount))) + 1);
            return MathF.Round(digits * Fonts.CharAdvance + 34);
        }
    }

    public float TextLeft => GutterWidth + 6;

    private float TextAreaWidth => Math.Max(0, Width - TextLeft - ScrollbarSize);

    /// <summary>The farthest scroll: the last line may come up to the top, as VS Code allows.</summary>
    public float MaxScrollY => Math.Max(0, (Document.Text.LineCount - 1) * LineHeight);

    public float MaxScrollX => Math.Max(0, _widest + Fonts.CharAdvance * 4 - TextAreaWidth);

    public void Resize(float width, float height)
    {
        Width = width;
        Height = height;
        ScrollTo(ScrollX, ScrollY);
    }

    public void ScrollTo(float x, float y)
    {
        ScrollX = Math.Clamp(x, 0, MaxScrollX);
        ScrollY = Math.Clamp(y, 0, MaxScrollY);
    }

    public void ScrollBy(float dx, float dy) => ScrollTo(ScrollX + dx, ScrollY + dy);

    /// <summary>Scrolls just enough to bring <paramref name="offset"/> into sight, with a little margin.</summary>
    public void Reveal(int offset)
    {
        var rope = Document.Text;
        var (line, column) = rope.Position(Math.Clamp(offset, 0, rope.Length));
        var top = line * LineHeight;
        var y = ScrollY;
        if (top < y) y = top;
        else if (top + LineHeight > y + Height) y = top + LineHeight - Height;

        var x = XOf(line, column);
        var sx = ScrollX;
        var margin = Fonts.CharAdvance * 4;
        if (x < sx + margin) sx = Math.Max(0, x - margin);
        else if (x > sx + TextAreaWidth - margin) sx = x - TextAreaWidth + margin;
        _widest = Math.Max(_widest, x);
        ScrollTo(sx, y);
    }

    /// <summary>The text offset under a point in the view (clamped into the text).</summary>
    public int OffsetAt(float x, float y)
    {
        var rope = Document.Text;
        var line = Math.Clamp((int)MathF.Floor((y + ScrollY) / LineHeight), 0, rope.LineCount - 1);
        var lineX = x - TextLeft + ScrollX;
        return rope.LineStart(line) + ColumnAt(line, lineX);
    }

    /// <summary>The visual column a point falls in (for column selection), counting past the end of the line.</summary>
    public int VisualColumnAt(float x) => Math.Max(0, (int)MathF.Round((x - TextLeft + ScrollX) / Fonts.CharAdvance));

    public int LineAt(float y) => Math.Clamp((int)MathF.Floor((y + ScrollY) / LineHeight), 0, Document.Text.LineCount - 1);

    /// <summary>The x of a caret position in its line, from the shaped line when it is in the cache, else from the grid.</summary>
    public float XOf(int line, int column)
    {
        if (TryShaped(line, out var shaped)) return shaped.XOf(column);
        return TextNavigation.VisualColumn(LineText(line), column, Options.TabSize) * Fonts.CharAdvance;
    }

    private int ColumnAt(int line, float x)
    {
        if (TryShaped(line, out var shaped)) return shaped.ColumnAt(x);
        return TextNavigation.ColumnOfVisual(LineText(line), (int)MathF.Round(x / Fonts.CharAdvance), Options.TabSize);
    }

    /// <summary>The frame to draw now, and asks the layout thread for the lines in or near sight it has not shaped.</summary>
    public EditorFrame BuildFrame()
    {
        var rope = Document.Text;
        if (!ReferenceEquals(rope, _textFor))
        {
            _lineText.Clear();
            _textFor = rope;
        }

        _shapedInSight.Clear();
        var first = FirstVisibleLine;
        var last = LastVisibleLine;
        var currentLines = Document.Selections.Where(s => s.IsEmpty).Select(s => rope.LineOf(s.Active)).ToHashSet();
        var lines = new List<FrameLine>(last - first + 1);
        var missing = new List<LineKey>();
        for (var line = first; line <= last; line++)
        {
            var key = Key(line);
            var y = line * LineHeight - ScrollY;
            if (_layout.TryGet(key, out var shaped))
            {
                _shapedInSight[line] = shaped;
                _widest = Math.Max(_widest, shaped.Width);
            }
            else
            {
                missing.Add(key);
                shaped = null!;
            }

            lines.Add(new FrameLine(line, y, shaped, key.Text, key.Tokens, key.Colors, currentLines.Contains(line)));
        }

        // Scrolled to new lines: shape a screen above and below too, so the next scroll finds them ready.
        if (first != _lastFirstLine)
        {
            _lastFirstLine = first;
            var page = last - first + 1;
            for (var line = last + 1; line <= Math.Min(rope.LineCount - 1, last + page); line++) AddIfMissing(missing, line);
            for (var line = Math.Max(0, first - page); line < first; line++) AddIfMissing(missing, line);
        }

        _layout.Request([.. missing]);
        var (selections, carets) = SelectionGeometry(first, last);
        var (matches, current) = MatchGeometry(first, last);
        var (vThumb, hThumb) = Thumbs();
        return new EditorFrame
        {
            Generation = LayoutService.NextFrameGeneration(),
            Width = Width,
            Height = Height,
            Theme = EffectiveTheme,
            Fonts = Fonts,
            TabSize = Options.TabSize,
            GutterWidth = GutterWidth,
            TextLeft = TextLeft,
            ScrollX = ScrollX,
            Lines = lines,
            Selections = selections,
            Carets = carets,
            Matches = matches,
            CurrentMatch = current,
            CaretsVisible = CaretsVisible && Focused,
            Focused = Focused,
            VerticalThumb = vThumb,
            HorizontalThumb = hThumb,
            ThumbActive = ThumbActive,
            Preedit = string.IsNullOrEmpty(Preedit) ? null : Preedit,
            PreeditAt = string.IsNullOrEmpty(Preedit) ? default : new SKPoint(PrimaryCaretRect.Left, PrimaryCaretRect.Top),
        };
    }

    /// <summary>Whether the vertical scrollbar's thumb is under a point, and where the thumb is.</summary>
    public SKRect VerticalThumb => Thumbs().Vertical;

    public SKRect HorizontalThumb => Thumbs().Horizontal;

    /// <summary>Scrolls so the vertical thumb's top is at <paramref name="thumbTop"/> (a thumb drag).</summary>
    public void DragVerticalThumb(float thumbTop)
    {
        var thumb = VerticalThumb;
        var track = Height - thumb.Height;
        if (track <= 0) return;
        ScrollTo(ScrollX, thumbTop / track * MaxScrollY);
    }

    public void DragHorizontalThumb(float thumbLeft)
    {
        var thumb = HorizontalThumb;
        var track = Width - GutterWidth - ScrollbarSize - thumb.Width;
        if (track <= 0) return;
        ScrollTo((thumbLeft - GutterWidth) / track * MaxScrollX, ScrollY);
    }

    private (SKRect Vertical, SKRect Horizontal) Thumbs()
    {
        var vertical = SKRect.Empty;
        var content = MaxScrollY + Height;
        if (MaxScrollY > 0 && Height > 0)
        {
            var size = Math.Max(MinThumb, Height * Height / content);
            var top = ScrollY / MaxScrollY * (Height - size);
            vertical = new SKRect(Width - ScrollbarSize + 3, top + 2, Width - 3, top + size - 2);
        }

        var horizontal = SKRect.Empty;
        if (MaxScrollX > 0)
        {
            var track = Width - GutterWidth - ScrollbarSize;
            var size = Math.Max(MinThumb, track * TextAreaWidth / (TextAreaWidth + MaxScrollX));
            var left = GutterWidth + ScrollX / MaxScrollX * (track - size);
            horizontal = new SKRect(left + 2, Height - ScrollbarSize + 3, left + size - 2, Height - 3);
        }

        return (vertical, horizontal);
    }

    private (List<SKRect> Selections, List<SKRect> Carets) SelectionGeometry(int first, int last)
    {
        var rope = Document.Text;
        var selections = new List<SKRect>();
        var carets = new List<SKRect>();
        var firstOffset = rope.LineStart(first);
        var lastOffset = rope.LineStart(last) + rope.LineLength(last);
        var origin = TextLeft - ScrollX;
        foreach (var s in Document.Selections)
        {
            if (s.End < firstOffset || s.Start > lastOffset) continue;
            if (!s.IsEmpty)
            {
                var startLine = rope.LineOf(Math.Max(s.Start, firstOffset));
                var endLine = rope.LineOf(Math.Min(s.End, lastOffset));
                var selStartLine = rope.LineOf(s.Start);
                var selEndLine = rope.LineOf(s.End);
                for (var line = startLine; line <= endLine; line++)
                {
                    var lineStart = rope.LineStart(line);
                    var from = line == selStartLine ? s.Start - lineStart : 0;
                    var to = line == selEndLine ? s.End - lineStart : rope.LineLength(line);
                    var x0 = XOf(line, from);
                    // A selected line break shows as a sliver past the line's end, as in every editor.
                    var x1 = XOf(line, to) + (line != selEndLine ? Fonts.SpaceAdvance * 0.6f : 0);
                    var y = line * LineHeight - ScrollY;
                    if (x1 > x0) selections.Add(new SKRect(origin + x0, y, origin + x1, y + LineHeight));
                }
            }

            var (caretLine, caretColumn) = rope.Position(s.Active);
            if (caretLine < first || caretLine > last) continue;
            var cx = origin + XOf(caretLine, caretColumn);
            var cy = caretLine * LineHeight - ScrollY;
            carets.Add(new SKRect(cx - Theme.CaretWidth / 2, cy + 1, cx + Theme.CaretWidth / 2, cy + LineHeight - 1));
        }

        return (selections, carets);
    }

    /// <summary>The rectangles of the find matches in sight: a binary search to the first, then only those in view.</summary>
    private (List<SKRect> Matches, List<SKRect> Current) MatchGeometry(int first, int last)
    {
        var all = new List<SKRect>();
        var current = new List<SKRect>();
        var found = Found;
        if (found.Matches.IsEmpty || found.Version != Document.Version) return (all, current);
        var rope = Document.Text;
        var firstOffset = rope.LineStart(first);
        var lastOffset = rope.LineStart(last) + rope.LineLength(last);
        var origin = TextLeft - ScrollX;

        // The first match that ends inside or after the first line in sight (matches are sorted and do not overlap).
        int lo = 0, hi = found.Matches.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (found.Matches[mid].Start + found.Matches[mid].Length <= firstOffset) lo = mid + 1;
            else hi = mid;
        }

        for (var i = lo; i < found.Matches.Length; i++)
        {
            var (start, length) = found.Matches[i];
            if (start > lastOffset) break;
            var target = i == CurrentMatch ? current : all;
            var startLine = rope.LineOf(Math.Max(start, firstOffset));
            var endLine = rope.LineOf(Math.Min(start + length, lastOffset));
            for (var line = startLine; line <= endLine; line++)
            {
                var lineStart = rope.LineStart(line);
                var from = Math.Max(start, lineStart) - lineStart;
                var to = Math.Min(start + length, lineStart + rope.LineLength(line)) - lineStart;
                var y = line * LineHeight - ScrollY;
                var x0 = XOf(line, from);
                var x1 = Math.Max(XOf(line, to), x0 + 2);
                target.Add(new SKRect(origin + x0, y + 1, origin + x1, y + LineHeight - 1));
            }
        }

        return (all, current);
    }

    private void AddIfMissing(List<LineKey> missing, int line)
    {
        var key = Key(line);
        if (!_layout.TryGet(key, out _)) missing.Add(key);
    }

    private LineKey Key(int line) =>
        new(LineText(line), Syntax?[line], Syntax?.Colors, EffectiveTheme.Foreground, Options.TabSize, Fonts.Version);

    private bool TryShaped(int line, out ShapedLine shaped)
    {
        if (_shapedInSight.TryGetValue(line, out shaped!)) return true;
        return _layout.TryGet(Key(line), out shaped);
    }

    private string LineText(int line)
    {
        if (!ReferenceEquals(Document.Text, _textFor))
        {
            _lineText.Clear();
            _textFor = Document.Text;
        }

        if (!_lineText.TryGetValue(line, out var text))
        {
            text = Document.Text.Line(line);
            if (_lineText.Count > 4096) _lineText.Clear();
            _lineText[line] = text;
        }

        return text;
    }
}
