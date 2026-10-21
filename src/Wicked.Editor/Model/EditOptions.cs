using System.Collections.Immutable;

namespace Wicked.Editor.Model;

/// <summary>A pair the editor closes as it is typed: <c>(</c> brings <c>)</c>.</summary>
public readonly record struct BracketPair(string Open, string Close);

/// <summary>
/// How editing behaves in one document: the indentation unit, the pairs that close themselves, and the language's
/// comment marks. The defaults are VS Code's.
/// </summary>
public sealed record EditOptions
{
    public int TabSize { get; init; } = 4;

    public bool InsertSpaces { get; init; } = true;

    public bool AutoClosePairs { get; init; } = true;

    public bool AutoSurround { get; init; } = true;

    public ImmutableArray<BracketPair> Pairs { get; init; } =
        [new("(", ")"), new("[", "]"), new("{", "}"), new("\"", "\""), new("'", "'"), new("`", "`")];

    /// <summary>The line comment mark ("//", "#"), or null for a language without one.</summary>
    public string? LineComment { get; init; } = "//";

    /// <summary>The block comment marks, used when a language has no line comment.</summary>
    public (string Open, string Close)? BlockComment { get; init; } = ("/*", "*/");

    /// <summary>Typing ">" to end an opening tag puts its closing tag after the caret (HTML and XML-like languages).</summary>
    public TagClosing CloseTags { get; init; }

    public string IndentUnit => InsertSpaces ? new string(' ', TabSize) : "\t";

    public static readonly EditOptions Default = new();
}

/// <summary>Which opening tags close themselves as "&gt;" is typed.</summary>
public enum TagClosing
{
    None,

    /// <summary>Every tag (XML, SVG).</summary>
    Xml,

    /// <summary>Every tag but HTML's void elements (br, img, input...).</summary>
    Html,
}
