using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Wicked.App.Terminal;

/// <summary>The colours of a terminal (0xAARRGGBB): the surface, the cursor and selection, and the 16 ANSI colours.</summary>
public sealed record TerminalTheme
{
    public uint Background { get; init; } = 0xFF111216;

    public uint Foreground { get; init; } = 0xFFD4D4D4;

    public uint Cursor { get; init; } = 0xFFA194FA;

    public uint Selection { get; init; } = 0x598B7BF7;

    public uint SelectionInactive { get; init; } = 0x332C3038;

    public uint FindMatch { get; init; } = 0x66F2B84B;

    public uint FindMatchCurrent { get; init; } = 0xB3F2B84B;

    /// <summary>The marks beside a command: it worked, it failed, it is still running.</summary>
    public uint CommandSuccess { get; init; } = 0xFF3FCF8E;

    public uint CommandFailure { get; init; } = 0xFFF0616D;

    public uint CommandRunning { get; init; } = 0xFF6C7280;

    public uint Scrollbar { get; init; } = 0x40FFFFFF;

    /// <summary>The 256-colour palette, the first 16 being the theme's ANSI colours.</summary>
    public uint[] Palette { get; init; } = TerminalPalette.Build(null);

    public static TerminalTheme Default { get; } = new();

    /// <summary>The theme the window's resources define (Brush.Terminal.* in Tokens.axaml); anything missing keeps the default.</summary>
    public static TerminalTheme FromResources(Application? app = null)
    {
        app ??= Application.Current;
        if (app is null) return Default;
        uint Pick(string key, uint fallback) =>
            app.TryFindResource(key, app.ActualThemeVariant, out var value) && value is ISolidColorBrush brush
                ? ((uint)brush.Color.A << 24) | ((uint)brush.Color.R << 16) | ((uint)brush.Color.G << 8) | brush.Color.B
                : fallback;

        var ansi = new uint[16];
        for (var i = 0; i < 16; i++) ansi[i] = Pick($"Brush.Terminal.Ansi{i}", Default.Palette[i]);
        return Default with
        {
            Background = Pick("Brush.Terminal.Background", Default.Background),
            Foreground = Pick("Brush.Terminal.Foreground", Default.Foreground),
            Cursor = Pick("Brush.Terminal.Cursor", Default.Cursor),
            Selection = Pick("Brush.Terminal.Selection", Default.Selection),
            SelectionInactive = Pick("Brush.Terminal.Selection.Inactive", Default.SelectionInactive),
            FindMatch = Pick("Brush.Terminal.FindMatch", Default.FindMatch),
            FindMatchCurrent = Pick("Brush.Terminal.FindMatch.Current", Default.FindMatchCurrent),
            CommandSuccess = Pick("Brush.Success", Default.CommandSuccess),
            CommandFailure = Pick("Brush.Danger", Default.CommandFailure),
            Palette = TerminalPalette.Build(ansi),
        };
    }

    /// <summary>This theme with the colours a VS Code theme gives its terminal (<c>terminal.background</c>, <c>terminal.ansiRed</c>, ...); keys it lacks stay.</summary>
    public TerminalTheme WithVsCodeColors(IReadOnlyDictionary<string, uint> colors)
    {
        uint Pick(string key, uint fallback) => colors.TryGetValue(key, out var c) && c != 0 ? c : fallback;
        string[] names = ["Black", "Red", "Green", "Yellow", "Blue", "Magenta", "Cyan", "White"];
        var ansi = new uint[16];
        for (var i = 0; i < 8; i++)
        {
            ansi[i] = Pick($"terminal.ansi{names[i]}", Palette[i]);
            ansi[i + 8] = Pick($"terminal.ansiBright{names[i]}", Palette[i + 8]);
        }

        return this with
        {
            Background = Pick("terminal.background", Pick("editor.background", Background)),
            Foreground = Pick("terminal.foreground", Pick("editor.foreground", Foreground)),
            Cursor = Pick("terminalCursor.foreground", Cursor),
            Selection = Pick("terminal.selectionBackground", Selection),
            Palette = TerminalPalette.Build(ansi),
        };
    }
}

/// <summary>Builds the 256-colour table from the theme's 16.</summary>
internal static class TerminalPalette
{
    public static uint[] Build(uint[]? ansi) => Wicked.Core.Terminal.Palette.Build(ansi);
}
