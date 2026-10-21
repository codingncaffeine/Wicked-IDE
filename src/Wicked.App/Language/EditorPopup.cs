using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Wicked.App.Editor;

namespace Wicked.App.Language;

/// <summary>
/// A card floating over the editor next to some text (a hover, the suggestion list, signature help): drawn in the
/// window's overlay layer, so it opens no window of its own and is not clipped by the editor, and flipped to the other
/// side of its text when it would leave the window. It never takes the keyboard; the editor forwards the keys it wants.
/// </summary>
public sealed class EditorPopup
{
    private readonly CodeEditor _editor;
    private readonly Popup _popup;
    private readonly Border _card;

    public EditorPopup(CodeEditor editor, string name, double maxWidth = 560, double maxHeight = 360)
    {
        _editor = editor;
        _card = new Border { Name = name, Classes = { "editorpopup" }, MaxWidth = maxWidth, MaxHeight = maxHeight };
        _popup = new Popup
        {
            Child = _card,
            PlacementTarget = editor,
            Placement = PlacementMode.AnchorAndGravity,
            ShouldUseOverlayLayer = true,
            IsLightDismissEnabled = false,
            Focusable = false,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.ResizeY,
        };
        editor.AdoptPopup(_popup);
    }

    public bool IsOpen => _popup.IsOpen;


    public Border Card => _card;

    public Control? Content
    {
        get => _card.Child as Control;
        set => _card.Child = value;
    }

    /// <summary>Whether the pointer is over the card (a hover stays while the user reads or scrolls it).</summary>
    public bool IsPointerOver => _popup.IsOpen && _card.IsPointerOver;

    /// <summary>Opens below <paramref name="anchor"/> (or above it, with <paramref name="above"/>), flipping when there is no room.</summary>
    public void Show(Rect anchor, bool above = false)
    {
        _popup.PlacementRect = anchor;
        _popup.PlacementAnchor = above ? PopupAnchor.TopLeft : PopupAnchor.BottomLeft;
        _popup.PlacementGravity = above ? PopupGravity.TopRight : PopupGravity.BottomRight;
        if (!_popup.IsOpen) _popup.IsOpen = true;
        else _popup.InvalidateMeasure();
    }

    public void Hide()
    {
        if (_popup.IsOpen) _popup.IsOpen = false;
    }

    /// <summary>The text's box at an offset, widened to [start, end) on its row, for anchoring.</summary>
    public Rect AnchorFor(int start, int end)
    {
        var a = _editor.RectOf(start);
        var b = _editor.RectOf(end);
        return Math.Abs(a.Top - b.Top) < 0.5 ? new Rect(a.Left, a.Top, Math.Max(a.Width, b.Left - a.Left), a.Height) : a;
    }
}
