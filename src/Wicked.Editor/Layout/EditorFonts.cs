using SkiaSharp;
using Wicked.Editor.Syntax;

namespace Wicked.Editor.Layout;

/// <summary>The editor's type settings.</summary>
/// <param name="Size">The font size in device-independent pixels.</param>
/// <param name="LineHeight">Line height as a multiple of the size (VS Code's default is about 1.5 at 14 px).</param>
/// <param name="Ligatures">Programming ligatures (=>, !=, ...) on or off.</param>
public sealed record EditorFontSettings(float Size = 14, float LineHeight = 1.5f, bool Ligatures = true);

/// <summary>
/// The editor's typefaces and their measurements. Typefaces are immutable and safe to share between threads; each
/// thread that draws or shapes makes its own <see cref="SKFont"/> from them. Loaded on a worker before an editor opens,
/// so the UI thread never reads a font file.
/// </summary>
public sealed class EditorFonts
{
    private static int _nextVersion;

    private EditorFonts(EditorFontSettings settings, SKTypeface regular, SKTypeface bold, SKTypeface italic, SKTypeface boldItalic)
    {
        Settings = settings;
        Regular = regular;
        Bold = bold;
        Italic = italic;
        BoldItalic = boldItalic;
        Version = Interlocked.Increment(ref _nextVersion);

        using var font = CreateFont(regular);
        font.GetFontMetrics(out var metrics);
        Ascent = -metrics.Ascent;
        Descent = metrics.Descent;
        LineHeight = MathF.Round(settings.Size * settings.LineHeight);
        // The baseline sits so the text is centred in its line, on a whole pixel for crisp glyphs.
        Baseline = MathF.Round((LineHeight - (Ascent + Descent)) / 2 + Ascent);
        SpaceAdvance = font.MeasureText(" ");
        CharAdvance = font.MeasureText("M");
    }

    public EditorFontSettings Settings { get; }

    public SKTypeface Regular { get; }

    public SKTypeface Bold { get; }

    public SKTypeface Italic { get; }

    public SKTypeface BoldItalic { get; }

    /// <summary>Changes whenever fonts or settings change; part of every cached line's key.</summary>
    public int Version { get; }

    public float Size => Settings.Size;

    public float Ascent { get; }

    public float Descent { get; }

    /// <summary>The height of one line, in whole pixels.</summary>
    public float LineHeight { get; }

    /// <summary>From a line's top to its baseline.</summary>
    public float Baseline { get; }

    public float SpaceAdvance { get; }

    /// <summary>The width of one cell of the monospaced grid.</summary>
    public float CharAdvance { get; }

    public SKTypeface For(TokenStyle style) =>
        (style & (TokenStyle.Bold | TokenStyle.Italic)) switch
        {
            TokenStyle.Bold => Bold,
            TokenStyle.Italic => Italic,
            TokenStyle.Bold | TokenStyle.Italic => BoldItalic,
            _ => Regular,
        };

    /// <summary>A Skia font for <paramref name="typeface"/> at the editor's size, set up the same way everywhere it is used.</summary>
    public SKFont CreateFont(SKTypeface typeface) => new(typeface, Settings.Size)
    {
        Subpixel = true,
        Edging = SKFontEdging.Antialias,
        Hinting = SKFontHinting.Slight,
        LinearMetrics = true,
    };

    /// <summary>The bundled JetBrains Mono at <paramref name="settings"/>. Reads four font files: call it off the UI thread.</summary>
    public static EditorFonts LoadDefault(EditorFontSettings? settings = null) =>
        new(settings ?? new EditorFontSettings(), Load("Regular"), Load("Bold"), Load("Italic"), Load("BoldItalic"));

    /// <summary>The same typefaces with other settings (a zoom, ligatures off).</summary>
    public EditorFonts With(EditorFontSettings settings) => new(settings, Regular, Bold, Italic, BoldItalic);

    private static SKTypeface Load(string style)
    {
        using var stream = typeof(EditorFonts).Assembly.GetManifestResourceStream($"Wicked.Editor.Fonts.JetBrainsMono-{style}.ttf")
            ?? throw new InvalidOperationException($"the bundled font JetBrainsMono-{style}.ttf is missing");
        return SKTypeface.FromStream(stream) ?? throw new InvalidOperationException($"JetBrainsMono-{style}.ttf could not be read");
    }
}
