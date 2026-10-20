using System.Globalization;
using System.Runtime.InteropServices;
using HarfBuzzSharp;
using SkiaSharp;
using Wicked.Editor.Syntax;
using HbBuffer = HarfBuzzSharp.Buffer;
using HbFont = HarfBuzzSharp.Font;

namespace Wicked.Editor.Layout;

/// <summary>
/// Turns a line of text and its highlighting into glyphs with HarfBuzz: ligatures, combining marks and any script the
/// fonts cover, with fallback fonts from the system for characters the editor font lacks (emoji, CJK). Belongs to one
/// thread: its HarfBuzz and Skia objects are not shared.
/// </summary>
internal sealed class LineShaper : IDisposable
{
    private readonly EditorFonts _fonts;
    private readonly HbBuffer _buffer = new();
    private readonly Feature[] _features;
    private readonly Dictionary<SKTypeface, Face> _faces = [];
    private readonly Dictionary<SKTypeface, SKFont> _skFonts = [];
    private readonly Dictionary<(SKTypeface, int), bool> _covers = [];
    private readonly Dictionary<int, SKTypeface> _fallbacks = [];

    public LineShaper(EditorFonts fonts)
    {
        _fonts = fonts;
        _features = fonts.Settings.Ligatures ? [] : [Feature.Parse("liga=0"), Feature.Parse("calt=0"), Feature.Parse("dlig=0")];
    }

    public ShapedLine Shape(string text, LineTokens? tokens, ColorTable? colors, uint defaultColor, int tabSize, ColorOverrides overrides = default)
    {
        var caretX = new float[text.Length + 1];
        var builders = new Dictionary<uint, SKTextBlobBuilder>();
        var decorations = new List<Decoration>();
        var tabWidth = tabSize * _fonts.SpaceAdvance;
        var x = 0f;

        foreach (var (start, end, color, style) in Segments(text, tokens, colors, defaultColor, overrides))
        {
            var primary = _fonts.For(style);
            var segmentStart = x;

            var pos = start;
            while (pos < end)
            {
                if (text[pos] == '\t')
                {
                    caretX[pos] = x;
                    x = (MathF.Floor(x / tabWidth + 0.001f) + 1) * tabWidth;
                    pos++;
                    continue;
                }

                // The longest stretch without a tab that one typeface covers.
                var face = FaceFor(text, pos, primary);
                var q = pos + Step(text, pos);
                while (q < end && text[q] != '\t')
                {
                    var next = FaceFor(text, q, primary);
                    if (next != face && !Joins(text, q)) break;
                    q += Step(text, q);
                }

                x = ShapeRun(text, pos, q - pos, face, color, x, caretX, builders);
                pos = q;
            }

            if ((style & TokenStyle.Underline) != 0) decorations.Add(new Decoration(new SKRect(segmentStart, 2, x, 3), color));
            if ((style & TokenStyle.Strikethrough) != 0) decorations.Add(new Decoration(new SKRect(segmentStart, -_fonts.Ascent * 0.32f, x, -_fonts.Ascent * 0.32f + 1), color));
        }

        caretX[text.Length] = x;
        var runs = new List<ColoredRun>(builders.Count);
        foreach (var (color, builder) in builders)
        {
            if (builder.Build() is { } blob) runs.Add(new ColoredRun(blob, color));
            builder.Dispose();
        }

        return new ShapedLine(text, runs, caretX, decorations);
    }

    /// <summary>
    /// The line's coloured stretches: one per token, with each overridden character (a bracket coloured by its depth)
    /// split out as a stretch of its own.
    /// </summary>
    internal static List<(int Start, int End, uint Color, TokenStyle Style)> Segments(string text, LineTokens? tokens, ColorTable? colors, uint defaultColor, ColorOverrides overrides)
    {
        var list = new List<(int, int, uint, TokenStyle)>();
        var count = tokens is { Count: > 0 } ? tokens.Count : 1;
        var o = 0;
        for (var t = 0; t < count; t++)
        {
            var start = tokens is { Count: > 0 } ? Math.Min(tokens.Start(t), text.Length) : 0;
            var end = tokens is { Count: > 0 } && t + 1 < tokens.Count ? Math.Min(tokens.Start(t + 1), text.Length) : text.Length;
            if (end <= start) continue;
            var color = tokens is { Count: > 0 } && colors is not null ? colors[tokens.Foreground(t)] : defaultColor;
            var style = tokens is { Count: > 0 } ? tokens.Style(t) : TokenStyle.None;
            var at = start;
            while (o < overrides.Count && overrides.Column(o) < start) o++;
            while (o < overrides.Count && overrides.Column(o) < end)
            {
                var column = overrides.Column(o);
                if (column > at) list.Add((at, column, color, style));
                list.Add((column, column + 1, overrides.Color(o), style));
                at = column + 1;
                o++;
            }

            if (at < end) list.Add((at, end, color, style));
        }

        return list;
    }

    private float ShapeRun(string text, int start, int length, SKTypeface typeface, uint color, float x, float[] caretX, Dictionary<uint, SKTextBlobBuilder> builders)
    {
        var face = HarfBuzzFace(typeface);
        // Shaping in the font's own units and scaling once keeps every advance exact (8.4 px, not 8.406 after rounding to
        // 1/64 px), so a shaped line and the monospaced grid agree to the pixel however long the line.
        using var font = new HbFont(face);
        var units = typeface.UnitsPerEm;
        font.SetScale(units, units);
        var scale = _fonts.Size / units;

        _buffer.ClearContents();
        _buffer.AddUtf16(text, start, length);
        _buffer.GuessSegmentProperties();
        _buffer.ClusterLevel = ClusterLevel.MonotoneCharacters;
        font.Shape(_buffer, _features);

        var infos = _buffer.GetGlyphInfoSpan();
        var positions = _buffer.GetGlyphPositionSpan();
        if (!builders.TryGetValue(color, out var builder)) builders[color] = builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedRun(SkiaFont(typeface), infos.Length);
        var glyphs = run.Glyphs;
        var points = run.Positions;

        // Caret positions: a cluster's advance is shared evenly by the characters in it (a ligature's two characters).
        var i = 0;
        while (i < infos.Length)
        {
            var cluster = (int)infos[i].Cluster;
            var clusterX = x;
            var j = i;
            while (j < infos.Length && infos[j].Cluster == cluster)
            {
                glyphs[j] = (ushort)infos[j].Codepoint;
                points[j] = new SKPoint(x + positions[j].XOffset * scale, -positions[j].YOffset * scale);
                x += positions[j].XAdvance * scale;
                j++;
            }

            var next = j < infos.Length ? (int)infos[j].Cluster : start + length;
            var chars = Math.Max(1, next - cluster);
            for (var k = 0; k < chars && cluster + k < caretX.Length; k++) caretX[cluster + k] = clusterX + (x - clusterX) * k / chars;
            i = j;
        }

        return x;
    }

    /// <summary>The typeface that draws the character at <paramref name="index"/>: the editor's own, or the system's best match for it.</summary>
    private SKTypeface FaceFor(string text, int index, SKTypeface primary)
    {
        var c = text[index];
        if (c < 0x250) return primary; // Latin, which the editor font covers
        var codepoint = char.IsHighSurrogate(c) && index + 1 < text.Length ? char.ConvertToUtf32(c, text[index + 1]) : c;
        if (Covers(primary, codepoint)) return primary;
        if (_fallbacks.TryGetValue(codepoint, out var cached)) return cached;
        var fallback = SKFontManager.Default.MatchCharacter(primary.FamilyName, primary.FontStyle, null, codepoint) ?? primary;
        _fallbacks[codepoint] = fallback;
        return fallback;
    }

    private bool Covers(SKTypeface typeface, int codepoint)
    {
        if (_covers.TryGetValue((typeface, codepoint), out var covered)) return covered;
        return _covers[(typeface, codepoint)] = typeface.ContainsGlyph(codepoint);
    }

    /// <summary>Characters that belong with the one before them whatever font covers them: combining marks, joiners, variation selectors.</summary>
    private static bool Joins(string text, int index)
    {
        var c = text[index];
        if (c is '‍' or >= '︀' and <= '️') return true;
        if (char.IsLowSurrogate(c)) return true;
        var category = CharUnicodeInfo.GetUnicodeCategory(text, index);
        return category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.SpacingCombiningMark
            || (index > 0 && text[index - 1] == '‍');
    }

    private static int Step(string text, int index) =>
        char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;

    private Face HarfBuzzFace(SKTypeface typeface)
    {
        if (_faces.TryGetValue(typeface, out var face)) return face;
        using var stream = typeface.OpenStream(out var index);
        var data = new byte[stream.Length];
        stream.Read(data, data.Length);
        // The blob copies nothing: it reads from the pinned array, which lives as long as this shaper.
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        _pins.Add(handle);
        using (var blob = new Blob(handle.AddrOfPinnedObject(), data.Length, MemoryMode.ReadOnly))
        {
            face = new Face(blob, index);
        }

        _faces[typeface] = face;
        return face;
    }

    private readonly List<GCHandle> _pins = [];

    private SKFont SkiaFont(SKTypeface typeface)
    {
        if (!_skFonts.TryGetValue(typeface, out var font)) _skFonts[typeface] = font = _fonts.CreateFont(typeface);
        return font;
    }

    public void Dispose()
    {
        _buffer.Dispose();
        foreach (var face in _faces.Values) face.Dispose();
        foreach (var font in _skFonts.Values) font.Dispose();
        foreach (var pin in _pins) pin.Free();
    }
}
