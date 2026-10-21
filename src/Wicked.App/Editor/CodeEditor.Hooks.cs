using Avalonia;
using Avalonia.Input;
using Wicked.Editor.Features;

namespace Wicked.App.Editor;

/// <summary>
/// What the editor offers the features layered on it (language servers, source control, the debugger, the assistant):
/// marks to draw, the places of text on screen, and hooks into its input that run before its own handling.
/// </summary>
public sealed partial class CodeEditor
{
    /// <summary>The language features serving this editor's file, when it has a server (set by them).</summary>
    public Wicked.App.Language.LanguageFeatures? LanguageService { get; set; }

    /// <summary>A key, before the editor's own bindings: true when it was taken (an open suggestion list's arrows).</summary>
    public Func<KeyEventArgs, bool>? KeyPreview { get; set; }

    /// <summary>Text about to be typed: true when it was dealt with (a commit character accepting a suggestion).</summary>
    public Func<string, bool>? TypePreview { get; set; }

    /// <summary>Text the user just typed went in (completion and signature help listen).</summary>
    public event Action<string>? Typed;

    /// <summary>A left click in the text before the editor acts on it: true when it was taken (Ctrl+click goes to a definition).</summary>
    public Func<int, KeyModifiers, bool>? ClickPreview { get; set; }

    /// <summary>A click in the glyph margin, by line (a breakpoint toggled).</summary>
    public Action<int>? GlyphMarginClicked { get; set; }

    /// <summary>The pointer moved over the editor: the text offset under it (-1 off the text), where, and the keys held.</summary>
    public event Action<int, Point, KeyModifiers>? PointerOverText;

    /// <summary>The view scrolled (popups anchored to text close or move).</summary>
    public event Action? Scrolled;

    /// <summary>Shows the glyph margin (for breakpoints) left of the line numbers.</summary>
    public bool ShowGlyphMargin
    {
        get => _view.ShowGlyphMargin;
        set
        {
            if (_view.ShowGlyphMargin == value) return;
            _view.ShowGlyphMargin = value;
            InvalidateVisual();
        }
    }

    /// <summary>Replaces one provider's marks, worked out for <paramref name="version"/> of the text (carried forward when older).</summary>
    public bool SetDecorations(string layer, DecorationSet set, long version)
    {
        var placed = _view.SetDecorations(layer, set, version);
        if (placed) InvalidateVisual();
        return placed;
    }

    public DecorationLayers Decorations => _view.Decorations;

    /// <summary>A language server's colours for names, for the text they were worked out on (null clears them).</summary>
    public void SetSemantic(Wicked.Editor.View.SemanticColors? colors)
    {
        _view.Semantic = colors;
        InvalidateVisual();
    }

    public Wicked.Editor.View.SemanticColors? Semantic => _view.Semantic;

    /// <summary>Where a text offset is drawn in the editor (its character's box), for anchoring a popup.</summary>
    public Rect RectOf(int offset)
    {
        var rope = Document.Text;
        var (line, column) = rope.Position(Math.Clamp(offset, 0, rope.Length));
        var (row, x) = _view.Place(line, column);
        var left = _view.TextLeft - _view.ScrollX + x;
        var top = row * _view.LineHeight - _view.ScrollY;
        return new Rect(left, top, Math.Max(1, _view.Fonts.CharAdvance), _view.LineHeight);
    }

    /// <summary>The primary caret's box.</summary>
    public Rect CaretRect => RectOf(Document.Primary.Active);

    /// <summary>The text offset under a point of the editor.</summary>
    public int OffsetAt(Point point) => _view.OffsetAt((float)point.X, (float)point.Y);

    public double LineHeight => _view.LineHeight;

    /// <summary>Takes a popup into the editor's logical tree (a popup outside one never opens), so it opens over the editor.</summary>
    internal void AdoptPopup(Avalonia.Controls.Primitives.Popup popup)
    {
        if (!LogicalChildren.Contains(popup)) LogicalChildren.Add(popup);
    }

    /// <summary>Selects [start, end), brings it into sight and takes the keyboard (a jump to a definition, a problem).</summary>
    public void Select(int start, int end)
    {
        Document.SetSelections([new Wicked.Editor.Model.Selection(start, end)]);
        _view.Reveal(start);
        RevealCaret();
    }
}
