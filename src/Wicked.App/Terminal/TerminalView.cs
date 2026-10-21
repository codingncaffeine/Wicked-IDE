using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Media;
using Avalonia.Threading;
using Wicked.Core.Terminal;
using Wicked.Editor.Layout;

namespace Wicked.App.Terminal;

/// <summary>What the user followed: an address, or a file with perhaps a position in it.</summary>
public sealed record TerminalLinkActivation(LinkKind Kind, string Target, int? Line, int? Column);

/// <summary>
/// The terminal control. The UI thread takes keys and the mouse and turns them into bytes for the program (microseconds),
/// takes each finished <see cref="TerminalFrame"/> the session hands over, and describes it to the render thread, which
/// draws it with Skia. Parsing, reflow, search, link detection and text extraction all happen elsewhere.
/// </summary>
public sealed class TerminalView : Control
{
    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(530);
    private static readonly bool Windows = OperatingSystem.IsWindows();

    private readonly DispatcherTimer _blink;
    private readonly DispatcherTimer _autoScroll;
    private readonly DispatcherTimer _bellTimer;
    private readonly TerminalIme _ime;
    private TerminalSession? _session;
    private EditorFonts _fonts;
    private TerminalMetrics _metrics;
    private TerminalTheme _theme;
    private TerminalFrame? _frame;
    private TerminalFrame? _pending;
    private int _framePosted;
    private bool _cursorOn = true;
    private bool _focused;
    private TerminalSelection? _selection;
    private bool _selecting;
    private TerminalPoint _selectionAnchor;
    private IReadOnlyList<TerminalMatch> _matches = [];
    private int _currentMatch = -1;
    private DateTime _bellAt = DateTime.MinValue;
    private string? _preedit;
    private double _wheelRemainder;
    private int _lastColumns = -1;
    private int _lastRows = -1;
    private (int Row, int Column)? _hoverCell;
    private int _hoverVersion;
    private (TerminalPoint Start, TerminalPoint End, TerminalLinkActivation Link)? _hover;
    private bool _thumbDrag;
    private double _thumbGrab;
    private bool _scrollbarHover;
    private int _reportedButton = -1;
    private (int Row, int Column) _lastReported = (-1, -1);
    private double _pointerY;
    private (int Row, int Column)? _cursorSeen;
    private long _chordAt;

    public TerminalView(EditorFonts fonts, TerminalTheme? theme = null)
    {
        _fonts = fonts;
        _metrics = TerminalMetrics.For(fonts);
        _theme = theme ?? TerminalTheme.Default;
        _ime = new TerminalIme(this);
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        // The Tab key belongs to the program in the terminal, not to focus navigation.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = _ime);

        _blink = new DispatcherTimer { Interval = BlinkInterval };
        _blink.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            InvalidateVisual();
        };
        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _autoScroll.Tick += (_, _) => AutoScroll();
        _bellTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _bellTimer.Tick += (_, _) =>
        {
            if ((DateTime.UtcNow - _bellAt).TotalMilliseconds > 180) _bellTimer.Stop();
            InvalidateVisual();
        };

        GotFocus += (_, _) =>
        {
            _focused = true;
            _session?.ReportFocus(true);
            RestartBlink();
            InvalidateVisual();
        };
        LostFocus += (_, _) =>
        {
            _focused = false;
            _session?.ReportFocus(false);
            _blink.Stop();
            InvalidateVisual();
        };

        // A view that left the window stops its timers.
        DetachedFromVisualTree += (_, _) =>
        {
            _blink.Stop();
            _autoScroll.Stop();
            _bellTimer.Stop();
            _focused = false;
        };
    }

    // ── Settings ─────────────────────────────────────────────────────────────────────────────────

    public TerminalSession? Session => _session;

    /// <summary>The user's cursor style, used until the program picks its own.</summary>
    public CursorShape CursorStyle { get; set; } = CursorShape.Block;

    public bool CursorBlinking { get; set; }

    public bool CopyOnSelection { get; set; }

    /// <summary>Whether the marks beside commands are drawn (the shell integration is on).</summary>
    public bool Decorations { get; set; } = true;

    /// <summary>Asked before a key is sent to the program: true lets the window's own shortcut have it instead (the workbench's list of keys the terminal does not take).</summary>
    public Func<KeyEventArgs, bool>? SkipShell { get; set; }

    public TerminalMetrics Metrics => _metrics;

    public TerminalFrame? Frame => _frame;

    public TerminalSelection? Selection => _selection;

    /// <summary>The pointer is over a link (its lookup on the worker has come back).</summary>
    internal bool OverLink => _hover is not null;

    public bool HasSelection => _selection is { IsEmpty: false };

    public (int Columns, int Rows) GridSize => (_lastColumns, _lastRows);

    public event Action<TerminalLinkActivation>? LinkActivated;

    /// <summary>Search was requested (Ctrl+Shift+F).</summary>
    public event Action? FindRequested;

    /// <summary>A frame was taken: the pane updates what it shows about the session.</summary>
    public event Action<TerminalFrame>? FrameTaken;

    /// <summary>The buffer was rebuilt (a resize reflowed it): line numbers from before mean nothing now.</summary>
    public event Action? BufferRebuilt;

    /// <summary>Follows a link as if it had been clicked.</summary>
    public void ActivateLink(TerminalLinkActivation link) => LinkActivated?.Invoke(link);

    public void ApplyTheme(TerminalTheme theme)
    {
        _theme = theme;
        InvalidateVisual();
    }

    public void ApplyFonts(EditorFonts fonts)
    {
        _fonts = fonts;
        _metrics = TerminalMetrics.For(fonts);
        ReportSize();
        InvalidateVisual();
    }

    /// <summary>Connects the view to a session and starts showing it. The session is started by whoever made it.</summary>
    public void Attach(TerminalSession session)
    {
        Detach();
        _session = session;
        session.FrameReady += OnFrameReady;
        session.Bell += OnBell;
        ReportSize();
    }

    public void Detach()
    {
        if (_session is null) return;
        _session.FrameReady -= OnFrameReady;
        _session.Bell -= OnBell;
        _session = null;
    }

    // ── Frames ───────────────────────────────────────────────────────────────────────────────────

    private void OnFrameReady(TerminalFrame frame)
    {
        Volatile.Write(ref _pending, frame);
        if (Interlocked.Exchange(ref _framePosted, 1) == 0) Dispatcher.UIThread.Post(TakeFrame, DispatcherPriority.Render);
    }

    private void OnBell() => Dispatcher.UIThread.Post(() =>
    {
        // A visual flash only: the terminal never makes a sound.
        _bellAt = DateTime.UtcNow;
        _bellTimer.Start();
        InvalidateVisual();
    });

    /// <summary>Called with the milliseconds each piece of the view's UI-thread work took (taking a frame, describing it for drawing); tests measure the 4 ms budget with it.</summary>
    internal static Action<double>? UiSlice { get; set; }

    private void TakeFrame()
    {
        var started = UiSlice is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            TakeFrameCore();
        }
        finally
        {
            if (UiSlice is { } slice) slice(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private void TakeFrameCore()
    {
        Volatile.Write(ref _framePosted, 0);
        var frame = Interlocked.Exchange(ref _pending, null);
        if (frame is null) return;
        var rebuilt = _frame is { } old && old.Epoch != frame.Epoch;
        _frame = frame;
        if (rebuilt)
        {
            _selection = null;
            _selecting = false;
            _matches = [];
            _currentMatch = -1;
            _hover = null;
            BufferRebuilt?.Invoke();
        }

        // The cursor shows steadily while it moves, and blinks once it rests.
        var cursor = (frame.CursorRow, frame.CursorColumn);
        if (_cursorSeen != cursor)
        {
            _cursorSeen = cursor;
            RestartBlink();
        }

        FrameTaken?.Invoke(frame);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var started = UiSlice is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        base.Render(context);
        RenderCore(context);
        if (UiSlice is { } slice) slice(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void RenderCore(DrawingContext context)
    {
        if (_frame is not { } frame)
        {
            context.FillRectangle(new SolidColorBrush(Color.FromUInt32(_theme.Background)), new Rect(Bounds.Size));
            return;
        }

        var modes = frame.Modes;
        var shape = modes.CursorShape ?? CursorStyle;
        var flash = (float)Math.Clamp(1 - (DateTime.UtcNow - _bellAt).TotalMilliseconds / 180, 0, 1);
        var state = new TerminalPaintState
        {
            Frame = frame,
            Theme = _theme,
            Fonts = _fonts,
            Metrics = _metrics,
            Width = (float)Bounds.Width,
            Height = (float)Bounds.Height,
            Gutter = Gutter,
            Focused = _focused,
            CursorOn = _cursorOn || !(modes.CursorBlink ?? CursorBlinking),
            Shape = shape,
            Selection = _selection is { IsEmpty: false } ? _selection : null,
            Matches = _matches,
            CurrentMatch = _currentMatch,
            HoverLink = _hover is { } h ? (h.Start, h.End) : null,
            Preedit = _preedit,
            BellFlash = flash,
            ScrollbarVisible = frame.ScrollbackLines > 0 && !modes.AlternateScreen,
            ScrollbarActive = _thumbDrag || _scrollbarHover,
        };
        context.Custom(new TerminalDrawOperation(new Rect(Bounds.Size), state));
    }

    private float Gutter => Decorations ? 14 : 0;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ReportSize();
    }

    private void ReportSize()
    {
        if (_session is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var columns = Math.Max(2, (int)((Bounds.Width - Gutter - TerminalPainter.ScrollbarWidth) / _metrics.Width));
        var rows = Math.Max(1, (int)(Bounds.Height / _metrics.Height));
        if (columns == _lastColumns && rows == _lastRows) return;
        (_lastColumns, _lastRows) = (columns, rows);
        _session.Resize(columns, rows);
    }

    private void RestartBlink()
    {
        _cursorOn = true;
        _blink.Stop();
        var blinks = _frame?.Modes.CursorBlink ?? CursorBlinking;
        if (_focused && blinks) _blink.Start();
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_session is null || e.Handled) return;
        var mods = e.KeyModifiers;
        var ctrl = (mods & KeyModifiers.Control) != 0;
        var shift = (mods & KeyModifiers.Shift) != 0;
        var alt = (mods & KeyModifiers.Alt) != 0;
        var modes = _frame?.Modes ?? new TerminalModes();

        if (HandleTerminalShortcut(e, ctrl, shift, alt, modes))
        {
            e.Handled = true;
            return;
        }

        if (SkipShell?.Invoke(e) == true) return;

        // On Windows AltGr arrives as Ctrl+Alt and types a character; it is text, not a chord.
        if (Windows && ctrl && alt) return;

        byte[]? bytes = null;
        var special = TerminalKeys.Special(e.Key);
        if (special is { } key) bytes = KeyEncoder.Encode(key, '\0', TerminalKeys.Mods(mods), modes);
        else if (TerminalKeys.Keypad(e.Key) is { } pad && modes.ApplicationKeypad) bytes = KeyEncoder.Encode(pad, '\0', TerminalKeys.Mods(mods), modes);
        else if (ctrl)
        {
            if (TerminalKeys.ControlCharacter(e.Key, shift) is { } c) bytes = KeyEncoder.Encode(TermKey.Character, c, TerminalKeys.Mods(mods), modes);
        }
        else if (alt && !string.IsNullOrEmpty(e.KeySymbol) && e.KeySymbol.Length <= 2 && !char.IsControl(e.KeySymbol[0]))
        {
            bytes = Encoding.UTF8.GetBytes("\u001b" + e.KeySymbol);
        }

        if (bytes is null) return;
        if (bytes.Length == 0) return;
        // Some systems also report the text of a chord (Alt+X as "x"): it must not be typed after the escape sequence already sent.
        if (alt || ctrl) _chordAt = System.Diagnostics.Stopwatch.GetTimestamp();
        Send(bytes);
        e.Handled = true;
    }

    private bool HandleTerminalShortcut(KeyEventArgs e, bool ctrl, bool shift, bool alt, TerminalModes modes)
    {
        if (ctrl && shift && !alt)
        {
            switch (e.Key)
            {
                case Key.C: _ = CopyAsync(); return true;
                case Key.V: _ = PasteAsync(); return true;
                case Key.F: FindRequested?.Invoke(); return true;
                case Key.A: SelectAll(); return true;
                case Key.Up: _session?.ScrollViewport(1); return true;
                case Key.Down: _session?.ScrollViewport(-1); return true;
            }
        }

        if (ctrl && !shift && !alt && e.Key == Key.Insert)
        {
            _ = CopyAsync();
            return true;
        }

        if (shift && !ctrl && !alt && e.Key == Key.Insert)
        {
            _ = PasteAsync();
            return true;
        }

        if (modes.AlternateScreen) return false;

        // Scrolling the history, as VS Code binds it.
        if (shift && !ctrl && !alt && e.Key is Key.PageUp or Key.PageDown)
        {
            _session?.ScrollViewport((e.Key == Key.PageUp ? 1 : -1) * Math.Max(1, _lastRows - 1));
            return true;
        }

        if (ctrl && !shift && !alt && e.Key == Key.Home)
        {
            _session?.ScrollViewport(int.MaxValue / 2);
            return true;
        }

        if (ctrl && !shift && !alt && e.Key == Key.End)
        {
            _session?.ScrollViewportToBottom();
            return true;
        }

        if (ctrl && !shift && !alt && e.Key is Key.Up or Key.Down && _frame is { } frame && frame.Lines.Any(l => l.Command is not null || frame.ScrollbackLines > 0))
        {
            _ = GoToCommandAsync(previous: e.Key == Key.Up, fallback: true);
            return true;
        }

        return false;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_session is null || string.IsNullOrEmpty(e.Text)) return;
        if (_chordAt != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(_chordAt).TotalMilliseconds < 40)
        {
            e.Handled = true;
            return;
        }

        _preedit = null;
        Send(Encoding.UTF8.GetBytes(e.Text));
        e.Handled = true;
    }

    private void Send(byte[] bytes)
    {
        _selection = null;
        if (_frame is { ViewportOffset: > 0 }) _session?.ScrollViewportToBottom();
        _session?.Input(bytes);
        RestartBlink();
    }

    /// <summary>Types text as if the user had (the pane uses it for dropped files and "run this").</summary>
    public void SendText(string text) => _session?.Input(text);

    // ── Clipboard ────────────────────────────────────────────────────────────────────────────────

    public async Task CopyAsync()
    {
        if (_session is null || _selection is not { IsEmpty: false } selection) return;
        var text = await _session.GetTextAsync(selection);
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    /// <summary>The selected text if it is one line (what the find widget starts from), else null.</summary>
    public async Task<string?> SelectionTextAsync()
    {
        if (_session is null || _selection is not { IsEmpty: false } selection || selection.Start.Line != selection.End.Line) return null;
        var text = await _session.GetTextAsync(selection);
        return text.Length > 0 ? text : null;
    }

    public async Task PasteAsync()
    {
        if (_session is null || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;
        if (_frame is { ViewportOffset: > 0 }) _session.ScrollViewportToBottom();
        _session.Paste(text);
    }

    public void SelectAll()
    {
        if (_frame is not { } frame) return;
        // From the oldest line held to the bottom of the live screen.
        var first = frame.TopLine - (frame.ScrollbackLines - frame.ViewportOffset);
        var last = frame.TopLine + frame.ViewportOffset + frame.Rows - 1;
        _selection = new TerminalSelection(new TerminalPoint(first, 0), new TerminalPoint(last, frame.Columns - 1), SelectionKind.Lines);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _selection = null;
        InvalidateVisual();
    }

    // ── Pointer ──────────────────────────────────────────────────────────────────────────────────

    private (int Row, int Column) CellAt(Point p)
    {
        var columns = Math.Max(1, _frame?.Columns ?? _lastColumns);
        var rows = Math.Max(1, _frame?.Rows ?? _lastRows);
        var column = (int)Math.Floor((p.X - Gutter) / _metrics.Width);
        var row = (int)Math.Floor(p.Y / _metrics.Height);
        return (Math.Clamp(row, 0, rows - 1), Math.Clamp(column, 0, columns - 1));
    }

    private TerminalPoint PointAt(Point p)
    {
        var (row, column) = CellAt(p);
        return new TerminalPoint((_frame?.TopLine ?? 0) + row, column);
    }

    private bool InScrollbar(Point p) => _frame is { ScrollbackLines: > 0, Modes.AlternateScreen: false } && p.X >= Bounds.Width - TerminalPainter.ScrollbarWidth - 2;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_session is null || _frame is not { } frame) return;
        Focus();
        var point = e.GetCurrentPoint(this);
        var p = point.Position;
        var mods = e.KeyModifiers;

        if (point.Properties.IsLeftButtonPressed && InScrollbar(p))
        {
            BeginThumbDrag(p, e);
            return;
        }

        var reporting = frame.Modes.Mouse != MouseTracking.None && (mods & KeyModifiers.Shift) == 0;
        if (reporting)
        {
            var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 : point.Properties.IsRightButtonPressed ? 2 : -1;
            if (button < 0) return;
            _reportedButton = button;
            var (row, column) = CellAt(p);
            _lastReported = (row, column);
            ReportMouse(MouseAction.Press, button, mods, row, column);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsMiddleButtonPressed)
        {
            _ = PasteAsync();
            e.Handled = true;
            return;
        }

        if (point.Properties.IsRightButtonPressed)
        {
            ShowContextMenu();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        if (_hover is { } hover && (mods & KeyModifiers.Control) != 0)
        {
            LinkActivated?.Invoke(hover.Link);
            e.Handled = true;
            return;
        }

        var cell = PointAt(p);
        var kind = (mods & KeyModifiers.Alt) != 0 ? SelectionKind.Block : SelectionKind.Stream;
        switch (e.ClickCount)
        {
            case >= 3:
                _ = SelectLogicalLineAsync(cell);
                break;
            case 2:
                _ = SelectWordAsync(cell);
                break;
            default:
                if ((mods & KeyModifiers.Shift) != 0 && _selection is { } existing) _selection = existing with { Active = cell };
                else
                {
                    _selectionAnchor = cell;
                    _selection = new TerminalSelection(cell, cell, kind);
                }

                _selecting = true;
                break;
        }

        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_session is null || _frame is not { } frame) return;
        var p = e.GetPosition(this);
        _pointerY = p.Y;
        var mods = e.KeyModifiers;

        if (_thumbDrag)
        {
            DragThumb(p);
            return;
        }

        var overBar = InScrollbar(p);
        if (overBar != _scrollbarHover)
        {
            _scrollbarHover = overBar;
            InvalidateVisual();
        }

        if (_reportedButton >= 0 || (frame.Modes.Mouse == MouseTracking.Any && (mods & KeyModifiers.Shift) == 0))
        {
            var (row, column) = CellAt(p);
            if ((row, column) != _lastReported)
            {
                _lastReported = (row, column);
                ReportMouse(MouseAction.Move, _reportedButton, mods, row, column);
            }

            return;
        }

        if (_selecting && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var cell = PointAt(p);
            if (_selection is { } sel && sel.Active != cell)
            {
                _selection = sel with { Active = cell };
                InvalidateVisual();
            }

            if (p.Y < 0 || p.Y > Bounds.Height) _autoScroll.Start();
            else _autoScroll.Stop();
            return;
        }

        UpdateHover(p, (mods & KeyModifiers.Control) != 0);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_session is null) return;
        _autoScroll.Stop();
        if (_thumbDrag)
        {
            _thumbDrag = false;
            e.Pointer.Capture(null);
            InvalidateVisual();
            return;
        }

        if (_reportedButton >= 0)
        {
            var (row, column) = CellAt(e.GetPosition(this));
            ReportMouse(MouseAction.Release, _reportedButton, e.KeyModifiers, row, column);
            _reportedButton = -1;
            e.Pointer.Capture(null);
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
            if (CopyOnSelection && HasSelection) _ = CopyAsync();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hoverCell = null;
        if (_hover is not null || _scrollbarHover)
        {
            _hover = null;
            _scrollbarHover = false;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null || _frame is not { } frame) return;
        var mods = e.KeyModifiers;
        var (row, column) = CellAt(e.GetPosition(this));

        // A program that asked for the mouse gets the wheel; Shift takes it back for scrolling.
        if (frame.Modes.Mouse != MouseTracking.None && (mods & KeyModifiers.Shift) == 0)
        {
            var action = e.Delta.Y > 0 ? MouseAction.WheelUp : MouseAction.WheelDown;
            var notches = Math.Max(1, (int)Math.Round(Math.Abs(e.Delta.Y)));
            for (var i = 0; i < notches; i++) ReportMouse(action, -1, mods, row, column);
            e.Handled = true;
            return;
        }

        _wheelRemainder += e.Delta.Y * 3;
        var lines = (int)_wheelRemainder;
        if (lines == 0)
        {
            e.Handled = true;
            return;
        }

        _wheelRemainder -= lines;

        // Full-screen programs without the mouse (less, man) scroll by arrow keys, as terminals do for the alternate screen.
        if (frame.Modes.AlternateScreen && frame.Modes.AlternateScroll)
        {
            var key = lines > 0 ? TermKey.Up : TermKey.Down;
            var bytes = KeyEncoder.Encode(key, '\0', TermMods.None, frame.Modes)!;
            for (var i = 0; i < Math.Abs(lines); i++) _session.Input(bytes);
        }
        else _session.ScrollViewport(lines);

        e.Handled = true;
    }

    private void ReportMouse(MouseAction action, int button, KeyModifiers mods, int row, int column)
    {
        if (_frame is not { } frame || _session is null) return;
        var bytes = MouseEncoder.Encode(action, button, TerminalKeys.Mods(mods), column, row, frame.Modes);
        if (bytes is not null) _session.Input(bytes);
    }

    private void AutoScroll()
    {
        if (_session is null || !_selecting || _frame is not { } frame) return;
        var above = _pointerY < 0;
        _session.ScrollViewport(above ? 1 : -1);
        if (_selection is { } sel)
        {
            var row = above ? 0 : frame.Rows - 1;
            _selection = sel with { Active = new TerminalPoint(frame.TopLine + row + (above ? -1 : 1), above ? 0 : frame.Columns - 1) };
        }
    }

    // ── Scrollbar ────────────────────────────────────────────────────────────────────────────────

    private void BeginThumbDrag(Point p, PointerPressedEventArgs e)
    {
        if (_frame is not { } frame) return;
        _thumbDrag = true;
        e.Pointer.Capture(this);
        var total = frame.ScrollbackLines + frame.Rows;
        var thumb = Math.Max(24, Bounds.Height * frame.Rows / total);
        var first = frame.ScrollbackLines - frame.ViewportOffset;
        var top = (Bounds.Height - thumb) * first / Math.Max(1, frame.ScrollbackLines);
        // Grab where the thumb was hit, or the middle of it when the click was on the track.
        _thumbGrab = p.Y >= top && p.Y <= top + thumb ? p.Y - top : thumb / 2;
        DragThumb(p);
        e.Handled = true;
    }

    private void DragThumb(Point p)
    {
        if (_frame is not { } frame || _session is null) return;
        var total = frame.ScrollbackLines + frame.Rows;
        var thumb = Math.Max(24, Bounds.Height * frame.Rows / total);
        var fraction = Math.Clamp((p.Y - _thumbGrab) / Math.Max(1, Bounds.Height - thumb), 0, 1);
        var first = (int)Math.Round(fraction * frame.ScrollbackLines);
        _session.ScrollViewport(-(frame.ScrollbackLines - first - frame.ViewportOffset));
    }

    // ── Words, lines, links ──────────────────────────────────────────────────────────────────────

    private async Task SelectWordAsync(TerminalPoint cell)
    {
        if (_session is null) return;
        var bounds = await _session.RunAsync(e => e.WordAt(cell.Line, cell.Column));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _selection = bounds is { } w
                ? new TerminalSelection(new TerminalPoint(cell.Line, w.Start), new TerminalPoint(cell.Line, w.End))
                : new TerminalSelection(cell, cell);
            _selectionAnchor = _selection.Value.Start;
            _selecting = true;
            InvalidateVisual();
        });
    }

    private async Task SelectLogicalLineAsync(TerminalPoint cell)
    {
        if (_session is null) return;
        var (first, last) = await _session.RunAsync(e => e.LogicalLineAt(cell.Line));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _selection = new TerminalSelection(new TerminalPoint(first, 0), new TerminalPoint(last, (_frame?.Columns ?? 1) - 1), SelectionKind.Lines);
            _selecting = false;
            InvalidateVisual();
        });
    }

    private void UpdateHover(Point p, bool control)
    {
        var cell = CellAt(p);
        if (_hoverCell == cell) return;
        _hoverCell = cell;
        var frame = _frame;
        if (frame is null) return;
        var version = ++_hoverVersion;
        // Reading the line for a link is off the UI thread; the frame's rows never change, so no lock is needed.
        _ = Task.Run(() => TerminalLinks.FindAt(frame, cell.Row, cell.Column)).ContinueWith(task =>
        {
            if (!task.IsCompletedSuccessfully) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (version != _hoverVersion) return;
                _hover = task.Result;
                Cursor = new Cursor(_hover is not null ? StandardCursorType.Hand : StandardCursorType.Ibeam);
                InvalidateVisual();
            });
        }, TaskScheduler.Default);
        _ = control;
    }

    // ── Search results and command navigation ────────────────────────────────────────────────────

    public void SetMatches(IReadOnlyList<TerminalMatch> matches, int current)
    {
        _matches = matches;
        _currentMatch = current;
        InvalidateVisual();
    }

    /// <summary>Scrolls so that a match is in view (unless it already is), roughly in the middle.</summary>
    public void RevealMatch(TerminalMatch match)
    {
        if (_frame is not { } frame || _session is null) return;
        if (match.Start.Line >= frame.TopLine && match.End.Line < frame.TopLine + frame.Rows) return;
        _session.ScrollViewportToLine(match.Start.Line - frame.Rows / 2);
    }

    /// <summary>Scrolls to the previous or next command the shell marked (VS Code's Ctrl+Up and Ctrl+Down).</summary>
    public async Task GoToCommandAsync(bool previous, bool fallback)
    {
        if (_session is null || _frame is not { } frame) return;
        var commands = await _session.GetCommandsAsync();
        if (commands.Count == 0)
        {
            // Without the shell's marks, Ctrl+Up and Ctrl+Down are the program's: readline's word keys.
            if (fallback && _frame is { } f) _session.Input(KeyEncoder.Encode(previous ? TermKey.Up : TermKey.Down, '\0', TermMods.Ctrl, f.Modes)!);
            return;
        }

        var top = frame.TopLine;
        TerminalCommand? target = previous
            ? commands.LastOrDefault(c => c.Line < top) is { Record: not null } before && before.Line < top ? before : null
            : commands.FirstOrDefault(c => c.Line > top) is { Record: not null } after && after.Line > top ? after : null;
        if (target is { } t) _session.ScrollViewportToLine(t.Line);
        else if (!previous) _session.ScrollViewportToBottom();
    }

    private void ShowContextMenu()
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Copy", () => _ = CopyAsync(), HasSelection));
        menu.Items.Add(Item("Paste", () => _ = PasteAsync()));
        menu.Items.Add(Item("Select All", SelectAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Find", () => FindRequested?.Invoke()));
        menu.Items.Add(Item("Clear", () => _session?.Clear()));
        menu.Open(this);
    }

    // ── Input method ─────────────────────────────────────────────────────────────────────────────

    internal void SetPreedit(string? text)
    {
        _preedit = string.IsNullOrEmpty(text) ? null : text;
        InvalidateVisual();
    }

    private Rect CursorRect()
    {
        if (_frame is not { } frame || frame.CursorRow < 0) return new Rect(0, 0, 1, _metrics.Height);
        return new Rect(Gutter + frame.CursorColumn * _metrics.Width, frame.CursorRow * _metrics.Height, _metrics.Width, _metrics.Height);
    }

    private sealed class TerminalIme(TerminalView view) : TextInputMethodClient
    {
        public override Visual TextViewVisual => view;

        public override bool SupportsPreedit => true;

        public override bool SupportsSurroundingText => false;

        public override string SurroundingText => "";

        public override Rect CursorRectangle => view.CursorRect();

        public override TextSelection Selection
        {
            get => default;
            set { }
        }

        public override void SetPreeditText(string? preeditText) => view.SetPreedit(preeditText);
    }
}
