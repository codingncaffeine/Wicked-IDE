using System.Collections.Immutable;
using SkiaSharp;
using Wicked.Editor.Features;
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

    /// <summary>The minimap's width and the height of one line in it (VS Code's "blocks" scale).</summary>
    public const float MinimapWidth = 100;

    public const float MinimapLineHeight = 2;
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
        document.Changed += OnDocumentChanged;
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

    /// <summary>Which ranges are folded away. Kept in step with every edit by the editor (<see cref="FoldState.Map"/>).</summary>
    public FoldState Folds { get; set; } = FoldState.None(1);

    /// <summary>Show the fold arrows of open ranges (the pointer is over the gutter); folded ones always show.</summary>
    public bool GutterHover { get; set; }

    private bool FoldsValid => Folds.LineCount == Document.Text.LineCount;

    /// <summary>A document line's first row among the rows shown (folded lines take none, wrapped lines several).</summary>
    public int VisualOf(int line) => Wrap is { } w ? w.FirstRow(line) : FoldsValid ? Folds.VisualLine(line) : line;

    /// <summary>The document line shown on a row.</summary>
    public int DocumentOf(int visual) => Wrap is { } w ? w.LineAtRow(visual).Line : FoldsValid ? Folds.DocumentLine(Math.Clamp(visual, 0, Folds.VisualLineCount - 1)) : visual;

    /// <summary>The rows the document takes on screen.</summary>
    public int VisualLineCount => Wrap?.RowCount ?? (FoldsValid ? Folds.VisualLineCount : Document.Text.LineCount);

    public bool IsHidden(int line) => FoldsValid && Folds.IsHidden(line);

    /// <summary>Where a document line's top is drawn.</summary>
    public float LineTop(int line) => VisualOf(line) * LineHeight - ScrollY;

    // ── Word wrap ────────────────────────────────────────────────────────────────────────────────

    /// <summary>How lines wrap: off unless the settings or Alt+Z say otherwise.</summary>
    public WordWrap WordWrap { get; set; } = WordWrap.Off;

    /// <summary>
    /// Runs a whole document's wrap on a worker and hands the result back on the UI thread. Without one (tests) the wrap
    /// is worked out here; so is a document short enough to wrap in a fraction of a millisecond, so that a resize never
    /// shows a frame of rows wrapped for the old width.
    /// </summary>
    public Action<Func<WrapMap>, Action<WrapMap>>? WrapWorker { get; set; }

    private const int WrapHereLines = 2000;
    private WrapLayout? _wrap;
    private bool _wrapRequested;
    private readonly List<DocumentChange> _sinceWrapRequest = [];

    /// <summary>Whether lines are wrapped now: wrap is on and its rows are in step with the text.</summary>
    public bool Wrapping => Wrap is not null;

    private WrapLayout? Wrap
    {
        get
        {
            if (_wrap is not { } w || w.LineCount != Document.Text.LineCount) return null;
            w.Sync(FoldsValid ? Folds : null);
            return w;
        }
    }

    /// <summary>What the lines should be wrapped for now, or null when wrap is off.</summary>
    private WrapOptions? WantedWrap()
    {
        if (WordWrap.Mode == WordWrapMode.Off || Width <= 0) return null;
        var view = TextAreaWidth - Fonts.CharAdvance; // room for a caret at a row's end
        var column = Math.Max(1, WordWrap.Column) * Fonts.CharAdvance;
        var width = WordWrap.Mode switch
        {
            WordWrapMode.WordWrapColumn => column,
            WordWrapMode.Bounded => Math.Min(view, column),
            _ => view,
        };
        return new WrapOptions { Width = Math.Max(width, Fonts.CharAdvance * 8), CharWidth = Fonts.CharAdvance, TabSize = Options.TabSize, Indent = WordWrap.Indent };
    }

    /// <summary>Brings the wrap in line with the settings, the width and the font: here for a short document, on the worker for a long one.</summary>
    private void EnsureWrap()
    {
        var wanted = WantedWrap();
        if (wanted is null)
        {
            _wrap = null;
            return;
        }

        if (_wrap is { } w && w.Options == wanted && w.LineCount == Document.Text.LineCount) return;
        if (_wrapRequested) return; // one at a time: a frame after it lands asks again if the width moved on meanwhile
        var rope = Document.Text;
        var version = Document.Version;
        if (WrapWorker is null || rope.LineCount <= WrapHereLines)
        {
            _wrap = WrapLayout.From(WrapMap.Compute(rope, version, wanted));
            return;
        }

        _wrapRequested = true;
        _sinceWrapRequest.Clear();
        WrapWorker(() => WrapMap.Compute(rope, version, wanted), AdoptWrap);
    }

    private void AdoptWrap(WrapMap map)
    {
        _wrapRequested = false;
        var layout = WrapLayout.From(map);
        // The edits made while the worker wrapped go on top, so the rows are for the text as it is now.
        foreach (var change in _sinceWrapRequest) layout.Apply(change);
        _sinceWrapRequest.Clear();
        if (WordWrap.Mode != WordWrapMode.Off) _wrap = layout;
    }

    private void OnDocumentChanged(DocumentChange change)
    {
        Decorations = Decorations.Map(change);
        if (_semantic is not null)
        {
            if (_sinceSemantic.Count >= RecentChangeLimit) _semantic = null;
            else _sinceSemantic.Add(ChangeSet.Invert(change.Before, change.Changes));
        }

        _recentChanges.Enqueue(change);
        while (_recentChanges.Count > RecentChangeLimit) _recentChanges.Dequeue();
        if (_wrap is { } w)
        {
            if (w.LineCount == change.Before.LineCount) w.Apply(change);
            else _wrap = null;
        }

        if (_wrapRequested) _sinceWrapRequest.Add(change);
    }

    /// <summary>Wraps the lines in sight again along their shaped widths once those are in (wide characters, fallback fonts).</summary>
    private void ReshapeInSight()
    {
        if (Wrap is not { } w || VisualLineCount == 0) return;
        var line = w.LineAtRow((int)(ScrollY / LineHeight)).Line;
        var last = w.LineAtRow(Math.Min(VisualLineCount - 1, (int)((ScrollY + Height) / LineHeight))).Line;
        for (; line <= last; line++)
        {
            if (w.FollowsShape(line) || IsHidden(line) || !_layout.TryGet(Key(line), out var shaped)) continue;
            w.Reshape(line, shaped.Text, shaped.CaretX);
        }
    }

    /// <summary>
    /// The row a text position is drawn on, and its x from the text's left edge (before horizontal scrolling). With
    /// <paramref name="upstream"/> a position on a wrap break is drawn at the end of the row above (<see cref="Selection.Upstream"/>).
    /// </summary>
    public (int Row, float X) Place(int line, int column, bool upstream = false)
    {
        if (Wrap is not { } w) return (VisualOf(line), XOf(line, column));
        var sub = SubAt(w, line, column, upstream);
        var (start, _) = w.Segment(line, sub, LineText(line).Length);
        return (w.FirstRow(line) + sub, XOf(line, column) - XOf(line, start) + w.IndentOf(line, sub));
    }

    // The row a column is on: a break column starts the next row unless the caret holds to the row above.
    private int SubAt(WrapLayout w, int line, int column, bool upstream)
    {
        var sub = w.SubOf(line, column);
        return upstream && sub > 0 && column == w.Segment(line, sub, LineText(line).Length).Start ? sub - 1 : sub;
    }

    /// <summary>
    /// The line on a row and its column nearest an x (from the text's left edge). Past the end of a row that wraps, the column
    /// is the break, held to this row (<c>Upstream</c>), as a click there puts the caret in VS Code.
    /// </summary>
    public (int Line, int Column, bool Upstream) PositionOnRow(int row, float x)
    {
        if (Wrap is not { } w)
        {
            var plain = DocumentOf(row);
            return (plain, ColumnAt(plain, x), false);
        }

        var (line, sub) = w.LineAtRow(row);
        var (start, end) = w.Segment(line, sub, LineText(line).Length);
        var column = Math.Clamp(ColumnAt(line, x - w.IndentOf(line, sub) + XOf(line, start)), start, end);
        return (line, column, column == end && sub < w.RowsOf(line) - 1);
    }

    /// <summary>
    /// Where a caret lands <paramref name="rows"/> rows up or down when lines wrap, keeping the x it aims for
    /// (<paramref name="goal"/> in whole pixels, -1 for where it is): the offset reached, the goal to keep and its affinity.
    /// </summary>
    public (int To, int Goal, bool Upstream) RowStep(int offset, int rows, int goal, bool upstream = false)
    {
        var rope = Document.Text;
        var (line, column) = rope.Position(offset);
        var (row, x) = Place(line, column, upstream);
        var want = goal >= 0 ? goal : (int)MathF.Round(x);
        var target = row + rows;
        if (target < 0) return (0, want, false);
        if (target >= VisualLineCount) return (rope.Length, want, false);
        var (toLine, toColumn, toUpstream) = PositionOnRow(target, want);
        return (rope.LineStart(toLine) + toColumn, want, toUpstream);
    }

    /// <summary>End on a wrapped row: the row's end (the break, held to the row), then the line's (VS Code's order).</summary>
    public (int To, bool Upstream) RowEnd(int offset, bool upstream = false)
    {
        var rope = Document.Text;
        var (line, column) = rope.Position(offset);
        var lineEnd = rope.LineStart(line) + rope.LineLength(line);
        if (Wrap is not { } w) return (lineEnd, false);
        var sub = SubAt(w, line, column, upstream);
        if (sub == w.RowsOf(line) - 1) return (lineEnd, false);
        var (_, end) = w.Segment(line, sub, rope.LineLength(line));
        var rowEnd = rope.LineStart(line) + end;
        return offset == rowEnd && upstream ? (lineEnd, false) : (rowEnd, true);
    }

    /// <summary>Home on a wrapped row after the first: the row's start; null where the line's own Home applies.</summary>
    public int? RowHome(int offset, bool upstream = false)
    {
        if (Wrap is not { } w) return null;
        var rope = Document.Text;
        var (line, column) = rope.Position(offset);
        var sub = SubAt(w, line, column, upstream);
        if (sub == 0) return null;
        var (start, _) = w.Segment(line, sub, rope.LineLength(line));
        return column == start ? null : rope.LineStart(line) + start;
    }

    /// <summary>Brackets and indent guides for a recent version of the text (the feature worker's).</summary>
    public FeatureSnapshot? Features { get; set; }

    public bool BracketColors { get; set; } = true;

    public bool ShowMinimap { get; set; } = true;

    private float MinimapSpace => ShowMinimap ? MinimapWidth : 0;

    /// <summary>The minimap's left edge.</summary>
    public float MinimapLeft => Width - ScrollbarSize - MinimapWidth;

    /// <summary>
    /// Which visual line the minimap's first row shows: the minimap scrolls in proportion to the editor, so its slider
    /// sweeps the whole document while showing a strip of lines around the view, as VS Code's does.
    /// </summary>
    public float MinimapFirstLine
    {
        get
        {
            var rows = Height / MinimapLineHeight;
            var total = VisualLineCount;
            if (total <= rows || MaxScrollY <= 0) return 0;
            return ScrollY / MaxScrollY * (total - rows);
        }
    }

    /// <summary>The slider over the minimap: the lines in the view.</summary>
    public SKRect MinimapSlider
    {
        get
        {
            var top = (ScrollY / LineHeight - MinimapFirstLine) * MinimapLineHeight;
            var height = Math.Max(MinimapLineHeight * 4, Height / LineHeight * MinimapLineHeight);
            return new SKRect(MinimapLeft, top, MinimapLeft + MinimapWidth, top + height);
        }
    }

    /// <summary>A click on the minimap: the line under it comes to the middle of the view.</summary>
    public void MinimapClick(float y)
    {
        var visual = MinimapFirstLine + y / MinimapLineHeight;
        ScrollTo(ScrollX, visual * LineHeight - Height / 2);
    }

    /// <summary>Dragging the slider: its top follows the pointer.</summary>
    public void MinimapDrag(float sliderTop)
    {
        var rows = Height / MinimapLineHeight;
        var total = VisualLineCount;
        if (total <= rows)
        {
            ScrollTo(ScrollX, sliderTop / MinimapLineHeight * LineHeight);
            return;
        }

        // The slider's place on the minimap strip, as a share of the way through the document.
        var sliderHeight = MinimapSlider.Height;
        var share = Math.Clamp(sliderTop / Math.Max(1, Height - sliderHeight), 0, 1);
        ScrollTo(ScrollX, share * MaxScrollY);
    }

    public bool ShowIndentGuides { get; set; } = true;

    public WhitespaceMode RenderWhitespace { get; set; } = WhitespaceMode.Selection;

    // ── Decorations ──────────────────────────────────────────────────────────────────────────────

    private const int RecentChangeLimit = 256;
    private readonly Queue<DocumentChange> _recentChanges = new();

    /// <summary>Every provider's marks (diagnostics, source control, breakpoints, …), carried through each edit.</summary>
    public DecorationLayers Decorations { get; private set; } = DecorationLayers.None;

    /// <summary>A change to the marks the editor should draw (a repaint is due).</summary>
    public event Action? DecorationsChanged;

    /// <summary>
    /// Replaces one provider's layer with marks worked out for the text at <paramref name="version"/>. A set for an older
    /// version is carried through the edits made since, so a slow provider's marks still land on their text; one older
    /// than the edits kept is dropped (the provider sends a fresher one after the next change).
    /// </summary>
    public bool SetDecorations(string layer, DecorationSet set, long version)
    {
        if (version > Document.Version) return false;
        if (version < Document.Version)
        {
            var since = _recentChanges.Where(c => c.Version > version).ToList();
            if (since.Count == 0 || since[0].Version != version + 1) return false;
            foreach (var change in since) set = set.Map(change);
        }

        Decorations = Decorations.With(layer, set);
        DecorationsChanged?.Invoke();
        return true;
    }

    /// <summary>The number to show beside a line (1-based, or null for none); unset, every line shows its own.</summary>
    public Func<int, int?>? LineNumber { get; set; }

    /// <summary>editor.rulers: the columns to draw a vertical line at.</summary>
    public IReadOnlyList<int> Rulers { get; set; } = [];

    /// <summary>editor.stickyScroll: the headers of the scopes the top line is in stay at the top (at most <see cref="StickyMaxLines"/>).</summary>
    public bool StickyScroll { get; set; }

    public int StickyMaxLines { get; set; } = 5;

    /// <summary>The header lines the last frame pinned at the top, top first (a click on the band goes to one).</summary>
    public IReadOnlyList<int> StickyLinesShown { get; private set; } = [];

    /// <summary>
    /// The header lines to pin for a view whose top line is <paramref name="firstLine"/>: each range around it whose header has
    /// scrolled away and whose end is still below the band, outermost first, as VS Code's sticky scroll picks them.
    /// </summary>
    private List<int> StickyHeaders(int firstLine)
    {
        // Folding ranges for a text with as many lines will do while the worker catches up, so typing does not flicker the band.
        if (!StickyScroll || StickyMaxLines <= 0 || Features?.Folding is not { Count: > 0 } ranges || Features.LineCount != Document.Text.LineCount) return [];
        var chain = new List<int>();
        for (var i = ranges.InnermostContaining(firstLine); i >= 0; i = ranges.Parent(i)) chain.Add(i);
        chain.Reverse();
        var shown = new List<int>();
        foreach (var i in chain)
        {
            if (shown.Count >= StickyMaxLines) break;
            var under = firstLine + shown.Count; // the line the band's next row would cover
            if (ranges.StartLine(i) < under && ranges.EndLine(i) > under) shown.Add(ranges.StartLine(i));
        }

        return shown;
    }

    /// <summary>editor.lineNumbers when no <see cref="LineNumber"/> map is set: every line's own, none, or relative to the caret.</summary>
    public LineNumbersMode LineNumbers { get; set; } = LineNumbersMode.On;

    private Func<int, int?>? NumbersForMode()
    {
        if (LineNumbers != LineNumbersMode.Relative) return null;
        var caretLine = Document.Text.LineOf(Document.Primary.Active);
        return line => line == caretLine ? line + 1 : Math.Abs(line - caretLine);
    }

    /// <summary>Room left of the line numbers for breakpoints and other glyphs (VS Code's glyph margin).</summary>
    public bool ShowGlyphMargin { get; set; }

    public const float GlyphMarginWidth = 18;

    private float GlyphMargin => ShowGlyphMargin ? GlyphMarginWidth : 0;

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
            var (row, at) = Place(line, column, Document.Primary.Upstream);
            var x = TextLeft - ScrollX + at;
            var y = row * LineHeight - ScrollY;
            return new SKRect(x, y, x + Theme.CaretWidth, y + LineHeight);
        }
    }

    /// <summary>A scrollbar thumb is being dragged or hovered: it is drawn brighter.</summary>
    public bool ThumbActive { get; set; }

    public float LineHeight => Fonts.LineHeight;

    public int FirstVisibleLine => DocumentOf(Math.Clamp((int)(ScrollY / LineHeight), 0, VisualLineCount - 1));

    public int LastVisibleLine => DocumentOf(Math.Clamp((int)((ScrollY + Height) / LineHeight), 0, VisualLineCount - 1));

    /// <summary>The gutter's width: room for the widest line number, with space around it.</summary>
    public float GutterWidth
    {
        get
        {
            // With numbers off the gutter keeps the fold arrows' room only.
            if (LineNumbers == LineNumbersMode.Off && LineNumber is null) return MathF.Round(22 + GlyphMargin);
            var digits = Math.Max(3, (int)Math.Floor(Math.Log10(Math.Max(1, Document.Text.LineCount))) + 1);
            return MathF.Round(digits * Fonts.CharAdvance + 34 + GlyphMargin);
        }
    }

    public float TextLeft => GutterWidth + 6;

    private float TextAreaWidth => Math.Max(0, Width - TextLeft - ScrollbarSize - MinimapSpace);

    /// <summary>The farthest scroll: the last line may come up to the top, as VS Code allows.</summary>
    public float MaxScrollY => Math.Max(0, (VisualLineCount - 1) * LineHeight);

    public float MaxScrollX => Wrap is { } w
        ? Math.Max(0, w.Options.Width + Fonts.CharAdvance - TextAreaWidth)
        : Math.Max(0, _widest + Fonts.CharAdvance * 4 - TextAreaWidth);

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
    public void Reveal(int offset, bool upstream = false)
    {
        var rope = Document.Text;
        var (line, column) = rope.Position(Math.Clamp(offset, 0, rope.Length));
        // A caret that lands in folded text opens the fold, as in VS Code.
        if (IsHidden(line)) Folds = Folds.Reveal(line);
        var (row, x) = Place(line, column, upstream);
        var top = row * LineHeight;
        var y = ScrollY;
        if (top < y) y = top;
        else if (top + LineHeight > y + Height) y = top + LineHeight - Height;

        var sx = ScrollX;
        var margin = Fonts.CharAdvance * 4;
        if (x < sx + margin) sx = Math.Max(0, x - margin);
        else if (x > sx + TextAreaWidth - margin) sx = x - TextAreaWidth + margin;
        _widest = Math.Max(_widest, x);
        ScrollTo(sx, y);
    }

    /// <summary>The text offset under a point in the view (clamped into the text).</summary>
    public int OffsetAt(float x, float y) => PositionAt(x, y).Offset;

    /// <summary>The text offset under a point and its affinity: past the end of a row that wraps, the break held to that row.</summary>
    public (int Offset, bool Upstream) PositionAt(float x, float y)
    {
        var row = Math.Clamp((int)MathF.Floor((y + ScrollY) / LineHeight), 0, Math.Max(0, VisualLineCount - 1));
        var (line, column, upstream) = PositionOnRow(row, x - TextLeft + ScrollX);
        return (Document.Text.LineStart(line) + column, upstream);
    }

    /// <summary>The visual column a point falls in (for column selection), counting past the end of the line.</summary>
    public int VisualColumnAt(float x) => Math.Max(0, (int)MathF.Round((x - TextLeft + ScrollX) / Fonts.CharAdvance));

    public int LineAt(float y) => DocumentOf(Math.Clamp((int)MathF.Floor((y + ScrollY) / LineHeight), 0, VisualLineCount - 1));

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

        EnsureWrap();
        ReshapeInSight();
        _shapedInSight.Clear();
        var first = FirstVisibleLine;
        var last = LastVisibleLine;
        var currentLines = Document.Selections.Where(s => s.IsEmpty).Select(s => rope.LineOf(s.Active)).ToHashSet();
        var lines = new List<FrameLine>(last - first + 1);
        var missing = new List<LineKey>();
        var wrap = Wrap;
        var rows = VisualLineCount;
        var firstRow = Math.Clamp((int)(ScrollY / LineHeight), 0, Math.Max(0, rows - 1));
        var lastRow = Math.Clamp((int)((ScrollY + Height) / LineHeight), 0, Math.Max(0, rows - 1));
        var keyLine = -1;
        LineKey key = default;
        ShapedLine? shaped = null;
        for (var row = firstRow; row <= lastRow && rows > 0; row++)
        {
            var (line, sub) = wrap is not null ? wrap.LineAtRow(row) : (DocumentOf(row), 0);
            var y = row * LineHeight - ScrollY;
            if (line != keyLine)
            {
                // A wrapped line's rows share its key and shaped line.
                keyLine = line;
                key = Key(line);
                if (_layout.TryGet(key, out var found))
                {
                    shaped = found;
                    _shapedInSight[line] = found;
                    _widest = Math.Max(_widest, found.Width);
                }
                else
                {
                    shaped = null;
                    missing.Add(key);
                }
            }

            if (wrap is null)
            {
                lines.Add(new FrameLine(line, y, shaped, key.Text, key.Tokens, key.Colors, currentLines.Contains(line), FoldMark(line)));
                continue;
            }

            // A row of a wrapped line: the line drawn shifted so the row's first character sits at its indent, clipped to the row.
            var (start, end) = wrap.Segment(line, sub, key.Text.Length);
            var lastSub = sub == wrap.RowsOf(line) - 1;
            var left = XOf(line, start);
            var right = lastSub ? float.PositiveInfinity : XOf(line, end);
            lines.Add(new FrameLine(line, y, shaped, key.Text, key.Tokens, key.Colors, currentLines.Contains(line), FoldMark(line), sub, lastSub, left, right, wrap.IndentOf(line, sub) - left));
        }

        // Scrolled to new lines: shape a screen above and below too, so the next scroll finds them ready.
        if (first != _lastFirstLine)
        {
            _lastFirstLine = first;
            var page = last - first + 1;
            for (var line = last + 1; line <= Math.Min(rope.LineCount - 1, last + page); line++) AddIfMissing(missing, line);
            for (var line = Math.Max(0, first - page); line < first; line++) AddIfMissing(missing, line);
        }

        // Sticky scroll: the headers of the scopes the top line is in, drawn over the first rows.
        var stickyLines = StickyHeaders(first);
        StickyLinesShown = stickyLines;
        var sticky = new List<FrameLine>(stickyLines.Count);
        for (var i = 0; i < stickyLines.Count; i++)
        {
            var stickyKey = Key(stickyLines[i]);
            if (!_layout.TryGet(stickyKey, out var stickyShaped))
            {
                stickyShaped = null;
                missing.Add(stickyKey);
            }

            sticky.Add(new FrameLine(stickyLines[i], i * LineHeight, stickyShaped, stickyKey.Text, stickyKey.Tokens, stickyKey.Colors, false));
        }

        _layout.Request([.. missing]);
        var (selections, carets) = SelectionGeometry(first, last);
        var (matches, current) = MatchGeometry(first, last);
        var bracketBoxes = BracketBoxes(first, last);
        var guides = GuideGeometry(first, last);
        var whitespace = WhitespaceGeometry(lines);
        var (vThumb, hThumb) = Thumbs();
        var (marks, lineFills, glyphs) = DecorationGeometry(first, last, lines);
        return new EditorFrame
        {
            Generation = LayoutService.NextFrameGeneration(),
            Width = Width,
            Height = Height,
            Theme = EffectiveTheme,
            Fonts = Fonts,
            TabSize = Options.TabSize,
            GutterWidth = GutterWidth,
            GutterHover = GutterHover,
            TextLeft = TextLeft,
            ScrollX = ScrollX,
            Lines = lines,
            Selections = selections,
            Carets = carets,
            Matches = matches,
            BracketBoxes = bracketBoxes,
            Guides = guides,
            Whitespace = whitespace,
            Minimap = ShowMinimap ? MinimapFrame() : null,
            CurrentMatch = current,
            RangeMarks = marks,
            LineFills = lineFills,
            GutterMarks = glyphs,
            Overview = OverviewMarks(),
            GlyphMargin = GlyphMargin,
            LineNumber = LineNumber ?? NumbersForMode(),
            HideLineNumbers = LineNumber is null && LineNumbers == LineNumbersMode.Off,
            Rulers = [.. Rulers.Select(column => TextLeft - ScrollX + column * Fonts.CharAdvance)],
            Sticky = sticky,
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
                    if (IsHidden(line)) continue;
                    var lineStart = rope.LineStart(line);
                    var from = line == selStartLine ? s.Start - lineStart : 0;
                    var to = line == selEndLine ? s.End - lineStart : rope.LineLength(line);
                    // A selected line break shows as a sliver past the line's end, as in every editor.
                    AddSpan(selections, line, from, to, line != selEndLine ? Fonts.SpaceAdvance * 0.6f : 0, 0, 0);
                }
            }

            var (caretLine, caretColumn) = rope.Position(s.Active);
            if (caretLine < first || caretLine > last || IsHidden(caretLine)) continue;
            var (caretRow, caretX) = Place(caretLine, caretColumn, s.Upstream);
            var cx = origin + caretX;
            var cy = caretRow * LineHeight - ScrollY;
            carets.Add(new SKRect(cx - Theme.CaretWidth / 2, cy + 1, cx + Theme.CaretWidth / 2, cy + LineHeight - 1));
        }

        return (selections, carets);
    }

    /// <summary>
    /// The rectangles over columns [from, to) of a line: one, or one per row when the line wraps. <paramref name="tail"/>
    /// widens the last (a selected line break), <paramref name="inset"/> trims top and bottom, <paramref name="minWidth"/>
    /// keeps an empty match in sight.
    /// </summary>
    private void AddSpan(List<SKRect> into, int line, int from, int to, float tail, float inset, float minWidth)
    {
        var origin = TextLeft - ScrollX;
        var w = Wrap;
        var length = LineText(line).Length;
        var firstSub = w?.SubOf(line, from) ?? 0;
        var lastSub = w is null ? 0 : to > from ? w.SubOf(line, to - 1) : firstSub;
        var firstRow = VisualOf(line);
        for (var sub = firstSub; sub <= lastSub; sub++)
        {
            var (start, end) = w?.Segment(line, sub, length) ?? (0, length);
            var shift = (w?.IndentOf(line, sub) ?? 0) - XOf(line, start);
            var x0 = XOf(line, Math.Max(from, start)) + shift;
            var x1 = XOf(line, sub < lastSub ? end : Math.Min(to, end)) + shift + (sub == lastSub ? tail : 0);
            x1 = Math.Max(x1, x0 + minWidth);
            var y = (firstRow + sub) * LineHeight - ScrollY;
            if (x1 > x0) into.Add(new SKRect(origin + x0, y + inset, origin + x1, y + LineHeight - inset));
        }
    }

    /// <summary>What the minimap draws: the model, the visual lines on its rows, the slider.</summary>
    private MinimapFrame? MinimapFrame()
    {
        if (Features?.Minimap is not { } model || model.LineCount != Document.Text.LineCount) return null;
        var first = (int)MinimapFirstLine;
        var rows = Math.Min((int)(Height / MinimapLineHeight) + 1, VisualLineCount - first);
        var lines = new int[Math.Max(0, rows)];
        var wrap = Wrap;
        for (var i = 0; i < lines.Length; i++)
        {
            if (wrap is null)
            {
                lines[i] = DocumentOf(first + i);
                continue;
            }

            // A wrapped line is drawn once, on its first row; its other rows stay empty.
            var (line, sub) = wrap.LineAtRow(first + i);
            lines[i] = sub == 0 ? line : -1;
        }
        var offset = (MinimapFirstLine - first) * MinimapLineHeight;
        return new MinimapFrame(model, lines, MinimapLeft, -offset, MinimapSlider);
    }

    /// <summary>A line's mark in the gutter: folded, foldable (shown when the pointer is over the gutter), or none.</summary>
    private FoldMark FoldMark(int line)
    {
        if (FoldsValid && Folds.IsCollapsedHeader(line, out _)) return View.FoldMark.Folded;
        return Features?.Folding is { } ranges && Features.LineCount == Document.Text.LineCount && ranges.IndexStartingAt(line) >= 0 ? View.FoldMark.Open : View.FoldMark.None;
    }

    /// <summary>The features worked out for the text as it is now (brackets move with every edit, so stale ones are not drawn).</summary>
    private FeatureSnapshot? CurrentFeatures => Features is { } f && f.Version == Document.Version ? f : null;

    /// <summary>Boxes round the bracket touching the primary caret and its partner, as VS Code draws them.</summary>
    private List<SKRect> BracketBoxes(int first, int last)
    {
        var boxes = new List<SKRect>(2);
        if (CurrentFeatures?.Brackets.MatchAt(Document.Primary.Active) is not { } match) return boxes;
        var rope = Document.Text;
        foreach (var offset in new[] { match.Open, match.Close })
        {
            var (line, column) = rope.Position(offset);
            if (line < first || line > last || IsHidden(line)) continue;
            var (row, x) = Place(line, column);
            var x0 = TextLeft - ScrollX + x;
            var x1 = x0 + XOf(line, column + 1) - XOf(line, column);
            var y = row * LineHeight - ScrollY;
            boxes.Add(new SKRect(x0 + 0.5f, y + 1.5f, x1 - 0.5f, y + LineHeight - 1.5f));
        }

        return boxes;
    }

    /// <summary>The indent guides in sight, the caret's block's drawn brighter. Guides of a slightly older version still hold while the line count does.</summary>
    private List<(float X, float Top, float Bottom, bool Active)> GuideGeometry(int first, int last)
    {
        var result = new List<(float, float, float, bool)>();
        if (!ShowIndentGuides || Features is not { } features || features.LineCount != Document.Text.LineCount) return result;
        var guides = new List<IndentGuide>();
        var active = features.Guides.ActiveFor(Document.Text.LineOf(Document.Primary.Active));
        features.Guides.Collect(first, last, active, guides);
        var step = Options.TabSize * Fonts.SpaceAdvance;
        var origin = TextLeft - ScrollX;
        foreach (var g in guides)
        {
            var x = MathF.Round(origin + (g.Level - 1) * step);
            if (x < GutterWidth || IsHidden(g.Line)) continue;
            var y = LineTop(g.Line);
            result.Add((x, y, y + LineHeight * (Wrap?.RowsOf(g.Line) ?? 1), g.IsActive));
        }

        return result;
    }

    /// <summary>Spaces and tabs made visible, by the renderWhitespace mode (in selections, by default).</summary>
    private List<(SKPoint, bool)> WhitespaceGeometry(List<FrameLine> lines)
    {
        var result = new List<(SKPoint, bool)>();
        if (RenderWhitespace == WhitespaceMode.None) return result;
        var rope = Document.Text;
        var ranges = new List<(int Start, int End)>();
        var marks = new List<WhitespaceMark>();
        var origin = TextLeft - ScrollX;
        foreach (var line in lines)
        {
            if (line.Sub != 0) continue; // a wrapped line's marks are placed from its first row
            ranges.Clear();
            marks.Clear();
            if (RenderWhitespace == WhitespaceMode.Selection) WhitespaceMarks.SelectedRanges(Document.Selections, rope.LineStart(line.Line), line.Text.Length, ranges);
            if (RenderWhitespace == WhitespaceMode.Selection && ranges.Count == 0) continue;
            WhitespaceMarks.Collect(line.Text, RenderWhitespace, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(ranges), marks);
            foreach (var mark in marks)
            {
                var (row, at) = Place(line.Line, mark.Column);
                var x = origin + at;
                var y = row * LineHeight - ScrollY + LineHeight / 2;
                result.Add((new SKPoint(mark.IsTab ? x + 2 : x + Fonts.SpaceAdvance / 2, y), mark.IsTab));
            }
        }

        return result;
    }

    /// <summary>
    /// A line's brackets coloured by depth (VS Code's bracket-pair colours), from the newest bracket index. One a few
    /// edits old still serves for a line whose brackets sit where they were: each is checked against the text.
    /// </summary>
    // ── Semantic colours ─────────────────────────────────────────────────────────────────────────

    private SemanticColors? _semantic;
    private readonly List<ImmutableArray<TextChange>> _sinceSemantic = [];

    /// <summary>A language server's colours for names (functions, parameters, types), for the text they were worked out on.</summary>
    public SemanticColors? Semantic
    {
        get => _semantic;
        set
        {
            _semantic = value;
            _sinceSemantic.Clear();
            if (value is null || value.Version == Document.Version) return;
            // Worked out for an older text: carried through the edits since, when they are still kept.
            var since = _recentChanges.Where(c => c.Version > value.Version).ToList();
            if (since.Count == 0 || since[0].Version != value.Version + 1)
            {
                _semantic = null;
                return;
            }

            foreach (var c in since) _sinceSemantic.Add(ChangeSet.Invert(c.Before, c.Changes));
        }
    }

    /// <summary>The semantic stretches of a line, when the line's text is as it was when they were worked out.</summary>
    private SemanticLine? SemanticFor(int line, string text)
    {
        if (_semantic is not { } s) return null;
        var offset = Document.Text.LineStart(line);
        // Back through the edits made since, newest first, to the same place in the text the colours are for.
        for (var i = _sinceSemantic.Count - 1; i >= 0; i--) offset = ChangeSet.Map(offset, _sinceSemantic[i], stickAfter: false);
        var old = s.Text.LineOf(Math.Clamp(offset, 0, s.Text.Length));
        return s.Lines.TryGetValue(old, out var found) && string.Equals(found.Text, text, StringComparison.Ordinal) ? found : null;
    }

    /// <summary>The text drawn inside a line that is not the document's (inlay hints, ghost text), by column.</summary>
    private Inlays LineInlays(int line, string text)
    {
        if (Decorations.Layers.IsEmpty) return default;
        var start = Document.Text.LineStart(line);
        List<Inlay>? found = null;
        foreach (var set in Decorations.Sets)
        {
            foreach (var d in set.InlineIn(start, start + text.Length))
            {
                (found ??= []).Add(new Inlay(d.Offset - start, d.Text, d.Style, d.PadLeft, d.PadRight));
            }
        }

        return found is null ? default : new Inlays([.. found.OrderBy(i => i.Column)]);
    }

    /// <summary>A line's colour overrides: its semantic stretches with the brackets cut out of them in their depth colours.</summary>
    private ColorOverrides LineOverrides(int line, string text)
    {
        var brackets = BracketOverrides(line, text);
        if (SemanticFor(line, text) is not { } semantic || semantic.Starts.Length == 0) return brackets;
        var starts = new List<int>();
        var ends = new List<int>();
        var colors = new List<uint>();
        var b = 0;
        void Add(int from, int to, uint color)
        {
            if (to <= from) return;
            starts.Add(from);
            ends.Add(to);
            colors.Add(color);
        }

        for (var i = 0; i < semantic.Starts.Length; i++)
        {
            var at = semantic.Starts[i];
            var end = Math.Min(semantic.Ends[i], text.Length);
            while (b < brackets.Count && brackets.Column(b) < end)
            {
                var column = brackets.Column(b);
                if (column >= at) Add(at, column, semantic.Colors[i]);
                Add(column, column + 1, brackets.Color(b));
                at = Math.Max(at, column + 1);
                b++;
            }

            Add(at, end, semantic.Colors[i]);
        }

        for (; b < brackets.Count; b++) Add(brackets.Column(b), brackets.Column(b) + 1, brackets.Color(b));
        // Sorted by start; a bracket before the first stretch was added after it.
        var order = Enumerable.Range(0, starts.Count).OrderBy(i => starts[i]).ToArray();
        return new ColorOverrides([.. order.Select(i => starts[i])], [.. order.Select(i => colors[i])], [.. order.Select(i => ends[i])]);
    }

    private ColorOverrides BracketOverrides(int line, string text)
    {
        if (!BracketColors || Features is not { } features || features.LineCount != Document.Text.LineCount) return default;
        var brackets = features.Brackets;
        var start = Document.Text.LineStart(line);
        var i = brackets.FirstAtOrAfter(start);
        List<int>? columns = null;
        List<uint>? colors = null;
        for (; i >= 0 && i < brackets.Count; i++)
        {
            var offset = brackets.Offset(i);
            if (offset >= start + text.Length) break;
            var column = offset - start;
            if (text[column] != brackets.Char(i)) return default; // the line changed since: plain colours until the worker catches up
            (columns ??= []).Add(column);
            (colors ??= []).Add(brackets.IsMatched(i) ? Theme.BracketColors[brackets.Depth(i) % Theme.BracketColors.Length] : Theme.BracketUnmatched);
        }

        return columns is null ? default : new ColorOverrides([.. columns], [.. colors!]);
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
                if (IsHidden(line)) continue;
                var lineStart = rope.LineStart(line);
                var from = Math.Max(start, lineStart) - lineStart;
                var to = Math.Min(start + length, lineStart + rope.LineLength(line)) - lineStart;
                AddSpan(target, line, from, to, 0, 1, 2);
            }
        }

        return (all, current);
    }

    /// <summary>
    /// The decorations in sight as the frame draws them: range marks as rectangles over their text (a rectangle per row a
    /// range crosses), line backgrounds and gutter glyphs per row shown.
    /// </summary>
    private (List<(SKRect, RangeStyle)> Marks, List<(float Y, LineStyle Style)> Lines, List<(float Y, GutterGlyph Glyph)> Glyphs) DecorationGeometry(int first, int last, List<FrameLine> rows)
    {
        var marks = new List<(SKRect, RangeStyle)>();
        var lineFills = new List<(float, LineStyle)>();
        var glyphs = new List<(float, GutterGlyph)>();
        if (Decorations.Layers.IsEmpty) return (marks, lineFills, glyphs);
        var rope = Document.Text;
        var firstOffset = rope.LineStart(first);
        var lastOffset = rope.LineStart(last) + rope.LineLength(last);
        var spans = new List<SKRect>();
        var lineStarts = new Dictionary<int, LineStyle>();
        var gutter = new Dictionary<int, List<GutterGlyph>>();
        foreach (var set in Decorations.Sets)
        {
            foreach (var r in set.RangesIn(firstOffset, lastOffset))
            {
                var start = Math.Clamp(r.Start, 0, rope.Length);
                var end = Math.Clamp(r.End, start, rope.Length);
                var startLine = rope.LineOf(Math.Max(start, firstOffset));
                var endLine = rope.LineOf(Math.Min(end, lastOffset));
                for (var line = startLine; line <= endLine; line++)
                {
                    if (IsHidden(line)) continue;
                    var lineStart = rope.LineStart(line);
                    var length = rope.LineLength(line);
                    var from = Math.Max(start, lineStart) - lineStart;
                    var to = Math.Min(end, lineStart + length) - lineStart;
                    // An empty range (a diagnostic at a line's end, say) still marks a character's width.
                    if (to <= from)
                    {
                        if (from < length) to = from + 1;
                        else if (from > 0) from = to - 1;
                    }

                    spans.Clear();
                    AddSpan(spans, line, from, to, 0, 0, Fonts.CharAdvance * (to <= from ? 1 : 0));
                    foreach (var rect in spans) marks.Add((rect, r.Style));
                }
            }

            foreach (var l in set.Lines) lineStarts[l.LineStart] = l.Style;
            foreach (var g in set.Gutter)
            {
                if (!gutter.TryGetValue(g.LineStart, out var list)) gutter[g.LineStart] = list = [];
                list.Add(g.Glyph);
            }
        }

        foreach (var row in rows)
        {
            if (lineStarts.Count == 0 && gutter.Count == 0) break;
            var key = rope.LineStart(row.Line);
            if (lineStarts.TryGetValue(key, out var style)) lineFills.Add((row.Y, style));
            if (row.Sub == 0 && gutter.TryGetValue(key, out var list))
            {
                foreach (var glyph in list) glyphs.Add((row.Y, glyph));
            }
        }

        return (marks, lineFills, glyphs);
    }

    /// <summary>The scrollbar's overview ruler: problems, changes and find matches as ticks down the track.</summary>
    private List<(float Y, uint Color)> OverviewMarks()
    {
        var result = new List<(float, uint)>();
        var rows = VisualLineCount;
        if (rows <= 0 || Height <= 0) return result;
        var rope = Document.Text;
        float RowY(int offset)
        {
            var line = rope.LineOf(Math.Clamp(offset, 0, rope.Length));
            return VisualOf(line) / (float)Math.Max(1, rows) * Height;
        }

        var theme = EffectiveTheme;
        foreach (var set in Decorations.Sets)
        {
            foreach (var r in set.Ranges)
            {
                var color = r.Style switch
                {
                    RangeStyle.Error => theme.ErrorColor,
                    RangeStyle.Warning => theme.WarningColor,
                    RangeStyle.Info => theme.InfoColor,
                    _ => 0u,
                };
                if (color != 0) result.Add((RowY(r.Start), color));
            }

            foreach (var g in set.Gutter)
            {
                var color = g.Glyph switch
                {
                    GutterGlyph.Added => theme.GutterAdded,
                    GutterGlyph.Modified => theme.GutterModified,
                    GutterGlyph.Deleted => theme.GutterDeleted,
                    GutterGlyph.Breakpoint or GutterGlyph.BreakpointConditional or GutterGlyph.Logpoint => theme.Breakpoint,
                    _ => 0u,
                };
                if (color != 0) result.Add((RowY(g.LineStart), color));
            }

            if (result.Count > 4000) break; // a tick per pixel row is all the track can show
        }

        if (Found.Version == Document.Version)
        {
            var step = Math.Max(1, Found.Matches.Length / 2000);
            for (var i = 0; i < Found.Matches.Length; i += step) result.Add((RowY(Found.Matches[i].Start), theme.FindMatchCurrent));
        }

        return result;
    }

    private void AddIfMissing(List<LineKey> missing, int line)
    {
        var key = Key(line);
        if (!_layout.TryGet(key, out _)) missing.Add(key);
    }

    private LineKey Key(int line)
    {
        var text = LineText(line);
        return new(text, Syntax?[line], Syntax?.Colors, EffectiveTheme.Foreground, Options.TabSize, Fonts.Version, LineOverrides(line, text),
            LineInlays(line, text), EffectiveTheme.InlayForeground, EffectiveTheme.GhostText);
    }

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

/// <summary>editor.lineNumbers, as VS Code names its values.</summary>
public enum LineNumbersMode
{
    On,
    Off,
    Relative,
}
