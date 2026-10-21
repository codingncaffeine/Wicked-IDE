using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Wicked.App.Chat;
using Wicked.App.Editor;
using Wicked.Core.Lsp;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.App.Language;

/// <summary>
/// The language features inside one editor: the hover card, Ctrl+click links to definitions, the other uses of the
/// symbol at the caret, the lightbulb, and (in the other parts of this class) the suggestion list, signature help,
/// rename and quick fixes. Every request goes through the document's queue and is answered on a worker; what reaches
/// the UI thread is a finished list or text to show.
/// </summary>
public sealed partial class EditorLanguageUi
{
    public const string HighlightsLayer = "highlights";
    public const string LinkLayer = "link";
    public const string LightbulbLayer = "lightbulb";

    private readonly CodeEditor _editor;
    private readonly LanguageFeatures _features;
    private readonly Func<Task>? _goToDefinition;
    private readonly EditorPopup _hover;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _caretTimer;
    private readonly DispatcherTimer _semanticTimer;
    private CancellationTokenSource? _semanticCts;
    private CancellationTokenSource? _hoverCts;
    private CancellationTokenSource? _caretCts;
    private CancellationTokenSource? _linkCts;
    private int _hoverOffset = -1;
    private (int Start, int End) _hoverWord = (-1, -1);
    private (int Start, int End) _link = (-1, -1);

    public EditorLanguageUi(CodeEditor editor, LanguageFeatures features, Func<Task>? goToDefinition)
    {
        _editor = editor;
        _features = features;
        _goToDefinition = goToDefinition;
        _hover = new EditorPopup(editor, "HoverCard", 620, 420);
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(50, features.Settings.HoverDelay)) };
        _hoverTimer.Tick += (_, _) =>
        {
            _hoverTimer.Stop();
            _ = ShowHoverAsync(_hoverOffset, explicitly: false);
        };
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _caretTimer.Tick += (_, _) =>
        {
            _caretTimer.Stop();
            _ = CaretSettledAsync();
        };

        _semanticTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _semanticTimer.Tick += (_, _) =>
        {
            _semanticTimer.Stop();
            _ = RefreshSemanticAsync();
            _ = RefreshInlayHintsAsync();
        };

        editor.PointerOverText += OnPointer;
        editor.Scrolled += CloseAll;
        editor.LostFocus += (_, _) =>
        {
            if (!_hover.IsPointerOver) _hover.Hide();
            CloseSuggestions();
            CloseSignature();
        };
        editor.Document.SelectionsChanged += () =>
        {
            _caretTimer.Stop();
            _caretTimer.Start();
            OnCaretMovedForSignature();
        };
        editor.Document.Changed += _ =>
        {
            _hover.Hide();
            ClearLink();
            _semanticTimer.Stop();
            _semanticTimer.Start();
        };
        editor.KeyPreview = OnKey;
        editor.TypePreview = OnTypePreview;
        editor.Typed += OnTyped;
        editor.ClickPreview = OnClick;
        InitSuggestions();
        InitSignature();
    }

    private LanguageDocument? Doc => _features.DocumentFor(_editor.Document);

    /// <summary>Everything floating closed (a scroll, the editor left).</summary>
    public void CloseAll()
    {
        _hover.Hide();
        CloseSuggestions();
        CloseSignature();
        CloseRename();
        CloseActions();
        if (PeekOpen) ClosePeek();
    }

    // ── Keys ─────────────────────────────────────────────────────────────────────────────────────

    private bool OnKey(KeyEventArgs e)
    {
        if (RenameKey(e) || ActionsKey(e) || SuggestionKey(e) || SignatureKey(e)) return true;
        if (e.Key == Key.Escape && _hover.IsOpen)
        {
            _hover.Hide();
            return true;
        }

        return false;
    }

    // ── Hover ────────────────────────────────────────────────────────────────────────────────────

    private void OnPointer(int offset, Point at, KeyModifiers mods)
    {
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        if (offset < 0)
        {
            _hoverTimer.Stop();
            ClearLink();
            if (!_hover.IsPointerOver) DelayedHide();
            return;
        }

        var word = TextNavigation.WordAt(_editor.Document.Text, offset);
        if (ctrl) _ = ShowLinkAsync(offset, word);
        else ClearLink();
        if (!_features.Settings.Hover || Doc is null) return;
        if (word == _hoverWord && _hover.IsOpen) return;
        if (_hover.IsOpen && !_hover.IsPointerOver) _hover.Hide();
        _hoverOffset = offset;
        _hoverTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(50, _features.Settings.HoverDelay));
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void DelayedHide() => DispatcherTimer.RunOnce(() =>
    {
        if (!_hover.IsPointerOver && !_editor.IsPointerOver) _hover.Hide();
    }, TimeSpan.FromMilliseconds(300));

    /// <summary>Ctrl+K Ctrl+I: the hover for the caret's symbol, without the pointer.</summary>
    public Task ShowHoverAtCaretAsync() => ShowHoverAsync(_editor.Document.Primary.Active, explicitly: true);

    /// <summary>The hover card: the problems at the place, then what the server says about the symbol (Markdown).</summary>
    private async Task ShowHoverAsync(int offset, bool explicitly)
    {
        if (Doc is not { } doc || offset < 0) return;
        _hoverCts?.Cancel();
        var cts = _hoverCts = new CancellationTokenSource();
        var problems = _features.DiagnosticsAt(doc, offset);
        Hover? hover = null;
        try
        {
            hover = await doc.RequestAsync((c, text, _) => c.Supports("hoverProvider") ? c.HoverAsync(doc.Uri, LspText.PositionOf(text, offset), cts.Token) : Task.FromResult<Hover?>(null), cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            if (cts.IsCancellationRequested) return;
        }

        if (cts.IsCancellationRequested || (hover is null && problems.Count == 0)) return;
        var blocks = hover is null ? null : await Task.Run(() => MarkdownParser.Parse(hover.Text), cts.Token);
        if (cts.IsCancellationRequested) return;

        var stack = new StackPanel { Spacing = 8 };
        foreach (var p in problems.Take(6))
        {
            stack.Children.Add(ProblemLine(p));
            if (_features.ProblemAction is not { } act) continue;
            var fix = new Button { Name = "FixWithAssistant", Content = "Fix with the assistant", Classes = { "secondary", "small" } };
            var explain = new Button { Name = "ExplainWithAssistant", Content = "Explain", Classes = { "quiet", "small" } };
            var problem = p;
            fix.Click += (_, _) =>
            {
                _hover.Hide();
                act(_editor, problem, "fix");
            };
            explain.Click += (_, _) =>
            {
                _hover.Hide();
                act(_editor, problem, "explain");
            };
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(21, 0, 0, 0), Children = { fix, explain } });
        }
        if (problems.Count > 0 && blocks is not null) stack.Children.Add(new Border { Height = 1, Classes = { "popupdivider" } });
        if (blocks is not null) stack.Children.Add(new MarkdownView { Blocks = blocks });
        _hover.Content = new ScrollViewer { Content = stack, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var word = TextNavigation.WordAt(_editor.Document.Text, offset);
        _hoverWord = word;
        _hover.Show(_hover.AnchorFor(word.Start, Math.Max(word.End, word.Start + 1)), above: true);
        if (explicitly) _editor.Focus();
    }

    internal static Control ProblemLine(Diagnostic p)
    {
        var (icon, brush) = p.Severity switch
        {
            DiagnosticSeverity.Warning => ("Icon.TriangleAlert", "Brush.Warning"),
            DiagnosticSeverity.Information or DiagnosticSeverity.Hint => ("Icon.Info", "Brush.Accent"),
            _ => ("Icon.CircleX", "Brush.Danger"),
        };
        var source = p.Source is null ? "" : $"  {p.Source}" + (p.Code is { } code ? $"({code.ToString().Trim('"')})" : "");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children =
            {
                new Wicked.App.Shell.Icon { Data = Resource<Geometry>(icon), Size = 14, Foreground = Resource<IBrush>(brush), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) },
                new TextBlock { Text = p.Message + source, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 },
            },
        };
    }

    internal static T? Resource<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as T : null;

    // ── Ctrl+hover links and Ctrl+click ──────────────────────────────────────────────────────────

    private async Task ShowLinkAsync(int offset, (int Start, int End) word)
    {
        if (word.End <= word.Start || word == _link || Doc is not { } doc) return;
        _linkCts?.Cancel();
        var cts = _linkCts = new CancellationTokenSource();
        IReadOnlyList<Wicked.Core.Lsp.Location>? found = null;
        try
        {
            found = await doc.RequestAsync((c, text, _) => c.GoToAsync("definition", doc.Uri, LspText.PositionOf(text, offset), cts.Token), cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }

        if (cts.IsCancellationRequested || found is not { Count: > 0 }) return;
        _link = word;
        _editor.SetDecorations(LinkLayer, DecorationSet.OfRanges([new RangeDecoration(word.Start, word.End, RangeStyle.Link)]), _editor.Document.Version);
        _editor.Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void ClearLink()
    {
        _linkCts?.Cancel();
        if (_link.Start < 0) return;
        _link = (-1, -1);
        _editor.SetDecorations(LinkLayer, DecorationSet.Empty, _editor.Document.Version);
        _editor.Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    private bool OnClick(int offset, KeyModifiers mods)
    {
        if (!mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Alt) || Doc is null || _goToDefinition is null) return false;
        _editor.Document.SetSelections([Selection.Caret(offset)]);
        ClearLink();
        _ = _goToDefinition();
        return true;
    }

    // ── Semantic colours ─────────────────────────────────────────────────────────────────────────

    /// <summary>The document's semantic tokens, asked for after edits settle and decoded on a worker into colours by line.</summary>
    public async Task RefreshSemanticAsync()
    {
        if (!_features.Settings.SemanticHighlighting)
        {
            if (_editor.Semantic is not null) _editor.SetSemantic(null);
            return;
        }

        if (Doc is not { } doc) return;
        _semanticCts?.Cancel();
        var cts = _semanticCts = new CancellationTokenSource();
        try
        {
            var palette = await _features.SemanticPaletteAsync();
            var colors = await doc.RequestAsync(async (c, text, version) =>
            {
                if (c.SemanticLegend is not { } legend) return null;
                var data = await c.SemanticTokensAsync(doc.Uri, cts.Token).ConfigureAwait(false);
                return data is null ? null : SemanticDecoder.Decode(data, legend, palette, text, version);
            }, cts.Token);
            if (!cts.IsCancellationRequested && colors is not null) _editor.SetSemantic(colors);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }

    // ── Inlay hints ──────────────────────────────────────────────────────────────────────────────

    public const string InlayLayer = "inlay";
    private CancellationTokenSource? _inlayCts;

    /// <summary>Parameter names and inferred types drawn in the text, for the whole file (servers answer a range; this one is all of it).</summary>
    public async Task RefreshInlayHintsAsync()
    {
        if (_features.Settings.InlayHints is "off" or "offUnlessPressed")
        {
            if (!_editor.Decorations[InlayLayer].IsEmpty) _editor.SetDecorations(InlayLayer, DecorationSet.Empty, _editor.Document.Version);
            return;
        }

        if (Doc is not { } doc) return;
        _inlayCts?.Cancel();
        var cts = _inlayCts = new CancellationTokenSource();
        try
        {
            var answer = await doc.RequestAsync(async (c, text, version) =>
            {
                if (!c.Supports("inlayHintProvider")) return (Set: DecorationSet.Empty, Version: version);
                var hints = await c.InlayHintsAsync(doc.Uri, LspText.RangeOf(text, 0, text.Length), cts.Token).ConfigureAwait(false);
                return (Set: new DecorationSet([], [], [], hints.Select(h => new InlineDecoration(LspText.OffsetOf(text, h.Position), h.Label.Trim(), InlineStyle.Hint, h.PaddingLeft, h.PaddingRight))), Version: version);
            }, cts.Token);
            if (!cts.IsCancellationRequested) _editor.SetDecorations(InlayLayer, answer.Set, answer.Version);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }

    // ── The caret settled: highlights and the lightbulb ──────────────────────────────────────────

    private async Task CaretSettledAsync()
    {
        if (Doc is not { } doc) return;
        _caretCts?.Cancel();
        var cts = _caretCts = new CancellationTokenSource();
        var caret = _editor.Document.Primary.Active;
        var settings = _features.Settings;
        try
        {
            if (settings.OccurrencesHighlight)
            {
                var found = await doc.RequestAsync(async (c, text, version) =>
                {
                    if (!c.Supports("documentHighlightProvider")) return (Set: DecorationSet.Empty, Version: version);
                    var highlights = await c.DocumentHighlightsAsync(doc.Uri, LspText.PositionOf(text, caret), cts.Token).ConfigureAwait(false);
                    return (Set: DecorationSet.OfRanges(highlights.Select(h =>
                    {
                        var (s, e) = LspText.OffsetsOf(text, h.Range);
                        return new RangeDecoration(s, e, h.Kind == 3 ? RangeStyle.HighlightWrite : RangeStyle.Highlight);
                    })), Version: version);
                }, cts.Token);
                // One use on its own is the symbol itself: nothing to point out.
                if (!cts.IsCancellationRequested) _editor.SetDecorations(HighlightsLayer, found.Set.Ranges.Length > 1 ? found.Set : DecorationSet.Empty, found.Version);
            }

            if (settings.Lightbulb && _editor.ShowGlyphMargin)
            {
                var line = _editor.Document.Text.LineOf(caret);
                var problems = _features.DiagnosticsOf(doc).Where(d => d.Range.Start.Line <= line && d.Range.End.Line >= line).ToList();
                var actions = await doc.RequestAsync((c, text, _) => c.Supports("codeActionProvider")
                    ? c.CodeActionsAsync(doc.Uri, LspText.RangeOf(text, caret, caret), problems, cts.Token)
                    : Task.FromResult<IReadOnlyList<CodeAction>>([]), cts.Token);
                if (cts.IsCancellationRequested) return;
                var show = actions is { Count: > 0 } && actions.Any(a => a.Kind is null || a.Kind.StartsWith("quickfix", StringComparison.Ordinal) || a.Kind.StartsWith("refactor", StringComparison.Ordinal));
                _editor.SetDecorations(LightbulbLayer, show ? DecorationSet.OfGutter([new GutterDecoration(_editor.Document.Text.LineStart(line), GutterGlyph.Lightbulb)]) : DecorationSet.Empty, _editor.Document.Version);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
        }
    }
}
