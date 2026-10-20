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
public sealed class KeyDispatcher
{
    private readonly IReadOnlyList<(KeyGesture[] Gestures, WorkbenchCommand Command)> _bindings;
    private KeyGesture? _pending;

    /// <summary>The commands' default keys, with keybindings.json applied: its bindings first, its removals gone.</summary>
    public KeyDispatcher(IReadOnlyList<WorkbenchCommand> commands, IReadOnlyList<Wicked.Core.Settings.Keybinding>? overrides = null)
    {
        var byId = commands.ToDictionary(c => c.Id);
        var list = commands.Where(c => c.Gestures.Length > 0).Select(c => (c.Gestures, Command: c)).ToList();
        foreach (var o in overrides ?? [])
        {
            if (!byId.TryGetValue(o.Command, out var command)) continue;
            var gestures = o.Keys is null ? null : o.Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyGesture.Parse).ToArray();
            if (o.Remove) list.RemoveAll(b => b.Command == command && (gestures is null || b.Gestures.SequenceEqual(gestures)));
            else if (gestures is not null) list.Insert(0, (gestures, command));
        }

        _bindings = list;
    }

    /// <summary>The keys that run a command now, as shown in the palette ("" when none).</summary>
    public string KeysFor(string commandId) =>
        _bindings.FirstOrDefault(b => b.Command.Id == commandId) is { Command: not null } b ? string.Join(' ', b.Gestures.Select(g => g.ToString())) : "";

    /// <summary>The first half of a chord is waiting for its second ("Ctrl+K was pressed...").</summary>
    public bool ChordPending => _pending is not null;

    public WorkbenchCommand? Match(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        if (_pending is { } first)
        {
            _pending = null;
            return _bindings.FirstOrDefault(b => b.Gestures.Length == 2 && b.Gestures[0].Equals(first) && b.Gestures[1].Matches(e)).Command;
        }

        var single = _bindings.FirstOrDefault(b => b.Gestures.Length == 1 && b.Gestures[0].Matches(e)).Command;
        if (single is not null) return single;
        var chordStart = _bindings.FirstOrDefault(b => b.Gestures.Length == 2 && b.Gestures[0].Matches(e));
        if (chordStart.Command is not null)
        {
            _pending = chordStart.Gestures[0];
            e.Handled = true;
        }

        return null;
    }
}
