using Wicked.Editor.Features;

namespace Wicked.Editor.View;

/// <summary>VS Code's <c>editor.wordWrap</c> values.</summary>
public enum WordWrapMode
{
    Off,

    /// <summary>At the view's width.</summary>
    On,

    /// <summary>At <see cref="WordWrap.Column"/>, however wide the view.</summary>
    WordWrapColumn,

    /// <summary>At the view's width or the column, whichever comes first.</summary>
    Bounded,
}

/// <summary>How an editor wraps its lines: the mode, the column for the column modes, and the continuation indent.</summary>
public sealed record WordWrap(WordWrapMode Mode, int Column = 80, WrapIndent Indent = WrapIndent.Same)
{
    public static readonly WordWrap Off = new(WordWrapMode.Off);
}
