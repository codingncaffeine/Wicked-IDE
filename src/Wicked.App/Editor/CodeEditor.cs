using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using Wicked.Editor.Features;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;
using Wicked.Editor.View;

namespace Wicked.App.Editor;

/// <summary>
/// The code editor control. It takes input and hands each keystroke to the editing commands (microseconds on the UI
/// thread), asks its <see cref="TextView"/> for the frame to show (lookups only), and draws that frame on the render
/// thread. Highlighting and text shaping happen on their own threads and arrive as posted redraws.
/// </summary>
public sealed class CodeEditor : Control
{
    private static readonly TimeSpan Blink = TimeSpan.FromMilliseconds(530);
    private static (string Text, bool WholeLines) _lastCopy;

    private readonly EditorServices _services;
    private readonly TextView _view;
    private readonly SyntaxSession _syntax;
    private readonly DispatcherTimer _blink;
    private readonly EditorIme _ime;
    private int _redrawPosted;
    private Drag _drag;
    private float _dragGrab;
    private int _dragAnchor;
    private (int Line, int Column) _boxAnchor;

    private enum Drag
    {
        None,
        Text,
        Lines,
        Box,
        VerticalThumb,
        HorizontalThumb,
        Minimap,
    }

    public CodeEditor(EditorServices services, Document document, LanguageInfo? language)
    {
        _services = services;
        Document = document;
        Language = language;
        _view = new TextView(document, services.Layout, language?.Options ?? EditOptions.Default);
        _ime = new EditorIme(this);
        Find = new FindViewModel(this);
        _view.Folds = FoldState.None(document.Text.LineCount);
        _features = new FeatureWorker(snapshot => Dispatcher.UIThread.Post(() =>
        {
            if (snapshot.Version < (_view.Features?.Version ?? -1)) return;
            _view.Features = snapshot;
            // Folded ranges follow the ranges as worked out again (a fold whose range is gone opens).
            if (snapshot.Version == Document.Version && _view.Folds.LineCount == snapshot.LineCount) _view.Folds = _view.Folds.Rebind(snapshot.Folding);
            InvalidateVisual();
        }, DispatcherPriority.Render))
        {
            Markers = FoldingMarkers.For(language),
        };
        document.Changed += change => _view.Folds = _view.Folds.Map(change);
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);

        _syntax = services.Syntax.Open(document.Text, document.Version, language, snapshot => Dispatcher.UIThread.Post(() =>
        {
            if (snapshot.Version > (_view.Syntax?.Version ?? -1) || snapshot.ValidLines != _view.Syntax?.ValidLines) _view.Syntax = snapshot;
            // Strings and comments known: brackets inside them stop counting.
            if (snapshot.ValidLines == document.Text.LineCount && snapshot.Version == document.Version) RequestFeatures();
            InvalidateVisual();
        }, DispatcherPriority.Render));
        document.Changed += _syntax.Edit;
        document.Changed += _ => RequestFeatures();
        RequestFeatures();
        services.Layout.Ready += OnLinesReady;

        _blink = new DispatcherTimer { Interval = Blink };
        _blink.Tick += (_, _) =>
        {
            _view.CaretsVisible = !_view.CaretsVisible;
            InvalidateVisual();
        };

        GotFocus += (_, _) =>
        {
            _view.Focused = true;
            RestartBlink();
            InvalidateVisual();
        };
        LostFocus += (_, _) =>
        {
            _view.Focused = false;
            _blink.Stop();
            InvalidateVisual();
        };
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = _ime);

        // Highlighting serves the editors in sight first; a tab in the background waits its turn.
        AttachedToVisualTree += (_, _) => _syntax.Visible = true;
        DetachedFromVisualTree += (_, _) => _syntax.Visible = false;
    }

    public Document Document { get; }

    public LanguageInfo? Language { get; }

    public EditOptions Options => _view.Options;

    public FindViewModel Find { get; }

    private readonly FeatureWorker _features;

    /// <summary>Up/Down by the lines shown: a folded range counts as its first line.</summary>
    internal int StepLines(int line, int count) => _view.DocumentOf(_view.VisualOf(line) + count);

    /// <summary>Ctrl+Shift+[: folds the innermost range at the caret.</summary>
    public void Fold()
    {
        if (_view.Features?.Folding is not { } ranges) return;
        _view.Folds = _view.Folds.FoldAt(ranges, Document.Text.LineOf(Document.Primary.Active));
        MoveCaretOutOfFolds();
    }

    /// <summary>Ctrl+Shift+]: opens the folds at the caret.</summary>
    public void Unfold()
    {
        _view.Folds = _view.Folds.UnfoldAt(Document.Text.LineOf(Document.Primary.Active));
        InvalidateVisual();
    }

    public void FoldAll()
    {
        if (_view.Features?.Folding is not { } ranges) return;
        _view.Folds = _view.Folds.CollapseAll(ranges);
        MoveCaretOutOfFolds();
    }

    public void UnfoldAll()
    {
        _view.Folds = _view.Folds.ExpandAll();
        InvalidateVisual();
    }

    /// <summary>
    /// What a click on a line's fold arrow does: a folded line opens (the fold state follows every edit at once, so this
    /// never waits); an open range folds, using ranges worked out for the text as it is now.
    /// </summary>
    private Action? FoldArrowAt(int line)
    {
        if (_view.Folds.LineCount == Document.Text.LineCount && _view.Folds.IsCollapsedHeader(line, out _))
        {
            return () =>
            {
                _view.Folds = _view.Folds.Expand(line);
                InvalidateVisual();
            };
        }

        if (_view.Features is { } features && features.Version == Document.Version && features.Folding.IndexStartingAt(line) >= 0)
        {
            return () =>
            {
                _view.Folds = _view.Folds.Toggle(features.Folding, line);
                MoveCaretOutOfFolds();
            };
        }

        return null;
    }

    /// <summary>A caret that a fold just hid goes to the fold's first line, so it stays in sight.</summary>
    private void MoveCaretOutOfFolds()
    {
        var rope = Document.Text;
        Document.SetSelections(Document.Selections.Select(s =>
        {
            var line = rope.LineOf(s.Active);
            if (!_view.IsHidden(line)) return s;
            var shown = _view.DocumentOf(_view.VisualOf(line));
            return Wicked.Editor.Model.Selection.Caret(rope.LineStart(shown) + rope.LineLength(shown));
        }).ToList(), Document.PrimaryIndex);
        AfterCommand();
    }

    private void RequestFeatures() => _features.Request(Document.Text, Document.Version, _view.Syntax, _view.Options);

    internal TextView View => _view;

    /// <summary>Stops highlighting this document (the tab closed).</summary>
    public void Close()
    {
        Document.Changed -= _syntax.Edit;
        _services.Layout.Ready -= OnLinesReady;
        _syntax.Close();
        _blink.Stop();
    }

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var frame = _view.BuildFrame();
        _syntax.SetViewport(_view.FirstVisibleLine, _view.LastVisibleLine);
        context.Custom(new EditorDrawOperation(new Rect(Bounds.Size), frame));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _view.Resize((float)e.NewSize.Width, (float)e.NewSize.Height);
    }

    /// <summary>The layout thread has lines ready: one redraw, however many batches arrive before it runs.</summary>
    private void OnLinesReady()
    {
        if (Interlocked.Exchange(ref _redrawPosted, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _redrawPosted, 0);
            InvalidateVisual();
        }, DispatcherPriority.Render);
    }

    /// <summary>The settings that shape this editor: indentation and bracket closing over the language's rules, the palette surface.</summary>
    public void ApplySettings(Wicked.Core.Settings.WickedSettings settings)
    {
        _view.Options = (Language?.Options ?? EditOptions.Default) with
        {
            TabSize = Math.Clamp(settings.TabSize, 1, 16),
            InsertSpaces = settings.InsertSpaces,
            AutoClosePairs = settings.AutoClosingBrackets,
            AutoSurround = settings.AutoClosingBrackets,
        };
        _view.FollowPalette = settings.CodeColorTheme.Length > 0;
        _view.BracketColors = settings.BracketPairColorization;
        _view.ShowMinimap = settings.Minimap;
        _view.ShowIndentGuides = settings.IndentGuides;
        _view.RenderWhitespace = Enum.TryParse<Wicked.Editor.Features.WhitespaceMode>(settings.RenderWhitespace, ignoreCase: true, out var mode) ? mode : Wicked.Editor.Features.WhitespaceMode.Selection;
        RequestFeatures();
        InvalidateVisual();
    }

    /// <summary>Scrolls <paramref name="offset"/> into sight without taking the keyboard (the find widget keeps it).</summary>
    public void Reveal(int offset)
    {
        _view.Reveal(offset);
        InvalidateVisual();
    }

    /// <summary>Brings the primary caret into sight and the keyboard to the editor (after go-to-line, a search hit).</summary>
    public void RevealCaret()
    {
        Focus();
        AfterCommand();
    }

    /// <summary>After a command: the caret back in sight and steady, the input method told, a redraw.</summary>
    private void AfterCommand(bool reveal = true)
    {
        if (reveal) _view.Reveal(Document.Primary.Active);
        RestartBlink();
        _ime.Changed();
        InvalidateVisual();
    }

    private void RestartBlink()
    {
        _view.CaretsVisible = true;
        _blink.Stop();
        if (_view.Focused) _blink.Start();
    }

    // ── Keys and text ────────────────────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        foreach (var (gesture, command) in EditorKeymap.Effective)
        {
            if (!gesture.Matches(e)) continue;
            command.Run(this);
            e.Handled = true;
            AfterCommand();
            return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        _view.Preedit = null;
        Edits.Type(Document, e.Text, Options);
        e.Handled = true;
        AfterCommand();
    }

    internal void SetPreedit(string? text)
    {
        _view.Preedit = text;
        InvalidateVisual();
    }

    /// <summary>PageUp/PageDown: the carets and the view move together by a screen.</summary>
    public void Page(int direction, bool select)
    {
        var lines = Math.Max(1, _view.LastVisibleLine - _view.FirstVisibleLine - 1);
        if (direction < 0) Edits.Up(Document, select, Options, lines, StepLines);
        else Edits.Down(Document, select, Options, lines, StepLines);
        _view.ScrollBy(0, direction * lines * _view.LineHeight);
    }

    public async Task CopyAsync()
    {
        _lastCopy = Edits.CopyText(Document);
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_lastCopy.Text);
    }

    public async Task CutAsync()
    {
        _lastCopy = Edits.Cut(Document, Options);
        AfterCommand();
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_lastCopy.Text);
    }

    public async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        // Whole lines copied here paste above the caret's line; the same text copied elsewhere pastes where the caret is.
        Edits.Paste(Document, text, _lastCopy.WholeLines && text == _lastCopy.Text);
        AfterCommand();
    }

    // ── Pointer ──────────────────────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed) return;
        Focus();
        var (x, y) = ((float)point.Position.X, (float)point.Position.Y);

        if (_view.VerticalThumb.Contains(x, y))
        {
            _drag = Drag.VerticalThumb;
            _dragGrab = y - _view.VerticalThumb.Top;
        }
        else if (_view.HorizontalThumb.Contains(x, y))
        {
            _drag = Drag.HorizontalThumb;
            _dragGrab = x - _view.HorizontalThumb.Left;
        }
        else if (_view.ShowMinimap && x >= _view.MinimapLeft && x < _view.MinimapLeft + TextView.MinimapWidth)
        {
            // On the slider: drag it; elsewhere: jump there, then drag.
            var slider = _view.MinimapSlider;
            if (!slider.Contains(x, y)) _view.MinimapClick(y);
            _drag = Drag.Minimap;
            _dragGrab = y - _view.MinimapSlider.Top;
        }
        else if (x >= Bounds.Width - 12)
        {
            // A click on the scrollbar's track jumps a page toward it.
            _view.ScrollBy(0, (y < _view.VerticalThumb.Top ? -1 : 1) * (float)Bounds.Height);
            InvalidateVisual();
            return;
        }
        else if (x < _view.GutterWidth && x >= _view.GutterWidth - 20 && FoldArrowAt(_view.LineAt(y)) is { } toggle)
        {
            toggle();
            e.Handled = true;
            return;
        }
        else if (x < _view.GutterWidth)
        {
            var line = _view.LineAt(y);
            _dragAnchor = line;
            _drag = Drag.Lines;
            SelectLines(line, line, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        }
        else
        {
            var offset = _view.OffsetAt(x, y);
            var mods = e.KeyModifiers;
            if (mods.HasFlag(KeyModifiers.Alt) && mods.HasFlag(KeyModifiers.Shift))
            {
                _drag = Drag.Box;
                _boxAnchor = (_view.LineAt(y), _view.VisualColumnAt(x));
                BoxSelect(_boxAnchor.Line, _boxAnchor.Column);
            }
            else if (point.Properties.IsLeftButtonPressed && e.ClickCount == 2)
            {
                var (start, end) = TextNavigation.WordAt(Document.Text, offset);
                Document.SetSelections([new Selection(start, end)]);
                _drag = Drag.None;
            }
            else if (e.ClickCount >= 3)
            {
                var line = Document.Text.LineOf(offset);
                SelectLines(line, line, false);
                _dragAnchor = line;
                _drag = Drag.Lines;
            }
            else if (mods.HasFlag(KeyModifiers.Alt))
            {
                // Alt+click adds a caret, as in VS Code.
                Document.SetSelections([.. Document.Selections, Selection.Caret(offset)]);
                _drag = Drag.None;
            }
            else if (mods.HasFlag(KeyModifiers.Shift))
            {
                var primary = Document.Primary;
                Document.SetSelections([new Selection(primary.Anchor, offset)]);
                _dragAnchor = primary.Anchor;
                _drag = Drag.Text;
            }
            else
            {
                Document.BreakUndoGroup();
                Document.SetSelections([Selection.Caret(offset)]);
                _dragAnchor = offset;
                _drag = Drag.Text;
            }
        }

        e.Pointer.Capture(this);
        e.Handled = true;
        AfterCommand(reveal: false);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetPosition(this);
        var (x, y) = ((float)p.X, (float)p.Y);
        var overThumb = _view.VerticalThumb.Contains(x, y) || _view.HorizontalThumb.Contains(x, y);
        if (_drag == Drag.None)
        {
            Cursor = new Cursor(x < _view.GutterWidth || x >= (_view.ShowMinimap ? _view.MinimapLeft : Bounds.Width - 12) ? StandardCursorType.Arrow : StandardCursorType.Ibeam);
            var overGutter = x < _view.GutterWidth;
            if (overGutter != _view.GutterHover)
            {
                _view.GutterHover = overGutter;
                InvalidateVisual();
            }

            if (overThumb != _view.ThumbActive)
            {
                _view.ThumbActive = overThumb;
                InvalidateVisual();
            }

            return;
        }

        switch (_drag)
        {
            case Drag.VerticalThumb:
                _view.DragVerticalThumb(y - _dragGrab);
                break;
            case Drag.HorizontalThumb:
                _view.DragHorizontalThumb(x - _dragGrab);
                break;
            case Drag.Minimap:
                _view.MinimapDrag(y - _dragGrab);
                break;
            case Drag.Text:
                var offset = _view.OffsetAt(x, y);
                Document.SetSelections([new Selection(_dragAnchor, offset)]);
                _view.Reveal(offset);
                break;
            case Drag.Lines:
                SelectLines(_dragAnchor, _view.LineAt(y), false);
                break;
            case Drag.Box:
                BoxSelect(_view.LineAt(y), _view.VisualColumnAt(x));
                break;
        }

        AfterCommand(reveal: false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (!_view.GutterHover) return;
        _view.GutterHover = false;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = Drag.None;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var step = _view.LineHeight * 3;
        var dx = (float)-e.Delta.X * step;
        var dy = (float)-e.Delta.Y * step;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) (dx, dy) = (dy, dx);
        _view.ScrollBy(dx, dy);
        e.Handled = true;
        InvalidateVisual();
    }

    private void SelectLines(int from, int to, bool extend)
    {
        var rope = Document.Text;
        var (first, last) = (Math.Min(from, to), Math.Max(from, to));
        var start = rope.LineStart(first);
        var end = last + 1 < rope.LineCount ? rope.LineStart(last + 1) : rope.Length;
        var selection = to >= from ? new Selection(start, end) : new Selection(end, start);
        if (extend) selection = new Selection(Document.Primary.Anchor, selection.Active);
        Document.SetSelections([selection]);
    }

    /// <summary>Alt+Shift+drag: one selection per line between the anchor and the pointer, at the same visual columns.</summary>
    private void BoxSelect(int line, int column)
    {
        var rope = Document.Text;
        var (fromLine, toLine) = (Math.Min(_boxAnchor.Line, line), Math.Max(_boxAnchor.Line, line));
        var list = new List<Selection>();
        for (var l = fromLine; l <= toLine; l++)
        {
            var text = rope.Line(l);
            var start = rope.LineStart(l);
            var anchor = start + TextNavigation.ColumnOfVisual(text, _boxAnchor.Column, Options.TabSize);
            var active = start + TextNavigation.ColumnOfVisual(text, column, Options.TabSize);
            list.Add(new Selection(anchor, active));
        }

        Document.SetSelections(list, line < _boxAnchor.Line ? 0 : list.Count - 1);
    }

    /// <summary>The input method's view of the editor: the caret's line, its selection, and where to put the candidate window.</summary>
    private sealed class EditorIme(CodeEditor editor) : TextInputMethodClient
    {
        public override Visual TextViewVisual => editor;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => true;

        public override string SurroundingText => editor.Document.Text.Line(editor.Document.Text.LineOf(editor.Document.Primary.Active));

        public override Rect CursorRectangle
        {
            get
            {
                var r = editor._view.PrimaryCaretRect;
                return new Rect(r.Left, r.Top, Math.Max(1, r.Width), r.Height);
            }
        }

        public override TextSelection Selection
        {
            get
            {
                var doc = editor.Document;
                var start = doc.Text.LineStart(doc.Text.LineOf(doc.Primary.Active));
                return new TextSelection(Math.Max(0, doc.Primary.Anchor - start), doc.Primary.Active - start);
            }

            set
            {
                var doc = editor.Document;
                var start = doc.Text.LineStart(doc.Text.LineOf(doc.Primary.Active));
                doc.SetSelections([new Wicked.Editor.Model.Selection(start + value.Start, start + value.End)]);
            }
        }

        public override void SetPreeditText(string? preeditText) => editor.SetPreedit(preeditText);

        public void Changed()
        {
            RaiseCursorRectangleChanged();
            RaiseSurroundingTextChanged();
            RaiseSelectionChanged();
        }
    }
}
