using Avalonia.Input;

namespace Wicked.App.Shell;

/// <summary>
/// A workbench command: an id (for keybindings.json), the title the palette shows, its category, its default keys
/// (VS Code's, a chord written "Ctrl+K Ctrl+O"), and what it does.
/// </summary>
public sealed record WorkbenchCommand(string Id, string Category, string Title, string? Keys, Func<Task> Run)
{
    public string Label => Category.Length > 0 ? $"{Category}: {Title}" : Title;

    /// <summary>The keys as gestures: one for a plain shortcut, two for a chord.</summary>
    public KeyGesture[] Gestures { get; } = Keys is null ? [] : [.. Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyGesture.Parse)];
}

/// <summary>
/// Matches key presses to commands, chords included: after the first half of a chord (Ctrl+K) the next press completes
/// it or is let go. Lives on the UI thread and does no more than compare gestures.
/// </summary>
public sealed class KeyDispatcher(IReadOnlyList<WorkbenchCommand> commands)
{
    private KeyGesture? _pending;

    /// <summary>The first half of a chord is waiting for its second ("Ctrl+K was pressed...").</summary>
    public bool ChordPending => _pending is not null;

    public WorkbenchCommand? Match(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        if (_pending is { } first)
        {
            _pending = null;
            return commands.FirstOrDefault(c => c.Gestures.Length == 2 && c.Gestures[0].Equals(first) && c.Gestures[1].Matches(e));
        }

        var single = commands.FirstOrDefault(c => c.Gestures.Length == 1 && c.Gestures[0].Matches(e));
        if (single is not null) return single;
        var chordStart = commands.FirstOrDefault(c => c.Gestures.Length == 2 && c.Gestures[0].Matches(e));
        if (chordStart is not null)
        {
            _pending = chordStart.Gestures[0];
            e.Handled = true;
        }

        return null;
    }
}
