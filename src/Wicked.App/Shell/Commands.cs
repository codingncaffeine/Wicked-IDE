using Avalonia.Input;

namespace Wicked.App.Shell;

/// <summary>
/// A workbench command: an id (for keybindings.json), the title the palette shows, its category, its default keys
/// (VS Code's, a chord written "Ctrl+K Ctrl+O"), and what it does.
/// </summary>
public sealed record WorkbenchCommand(string Id, string Category, string Title, string? Keys, Func<Task> Run)
{
    public string Label => Category.Length > 0 ? $"{Category}: {Title}" : Title;

    /// <summary>
    /// When the command's keys apply (VS Code's "when"): F11 is Step Into while debugging and full screen otherwise. Null:
    /// always. The palette lists the command either way.
    /// </summary>
    public Func<bool>? When { get; init; }

    public bool Applies => When?.Invoke() != false;

    /// <summary>The "when" as VS Code writes it ("inDebugMode"), shown in the Keyboard Shortcuts editor.</summary>
    public string? WhenText { get; init; }

    /// <summary>One of the code editor's own actions: its keys are the editor's (EditorKeymap), shown here, bound there.</summary>
    public bool EditorKeys { get; init; }

    /// <summary>The keys as gestures: one for a plain shortcut, two for a chord.</summary>
    public KeyGesture[] Gestures { get; } = Keys is null ? [] : [.. Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyGesture.Parse)];
}

/// <summary>
/// Matches key presses to commands, chords included: after the first half of a chord (Ctrl+K) the next press completes
/// it or is let go. Lives on the UI thread and does no more than compare gestures.
/// </summary>
public sealed class KeyDispatcher
{
    private readonly IReadOnlyList<(KeyGesture[] Gestures, WorkbenchCommand Command, string Source)> _bindings;
    private KeyGesture? _pending;
    private readonly HashSet<string> _editorActions;

    /// <summary>Where a binding comes from when it is not a default: keybindings.json.</summary>
    public const string UserSource = "User";

    /// <summary>The commands' default keys, with keybindings.json applied: its bindings first, its removals gone.</summary>
    public KeyDispatcher(IReadOnlyList<WorkbenchCommand> commands, IReadOnlyList<Wicked.Core.Settings.Keybinding>? overrides = null)
        : this(commands, [.. (overrides ?? []).Select(o => (o, UserSource))])
    {
    }

    /// <summary>
    /// The commands' default keys with layers over them, each binding named by where it comes from (a keymap preset,
    /// an extension, keybindings.json): a later layer's bindings come first, its removals take earlier keys away.
    /// </summary>
    public KeyDispatcher(IReadOnlyList<WorkbenchCommand> commands, IReadOnlyList<(Wicked.Core.Settings.Keybinding Binding, string Source)> layers)
    {
        var byId = commands.ToDictionary(c => c.Id);
        // The defaults with a "when" come before those without, so a key shared by two commands (F11: Step Into while
        // debugging, full screen otherwise) reaches the specific one first; keybindings.json's go before them all.
        var list = commands.Where(c => c.Gestures.Length > 0).OrderBy(c => c.When is null ? 1 : 0).Select(c => (c.Gestures, Command: c, Source: "Default")).ToList();
        foreach (var (o, source) in layers)
        {
            if (!byId.TryGetValue(o.Command, out var command)) continue;
            var gestures = o.Keys is null ? null : o.Keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(KeyGesture.Parse).ToArray();
            if (o.Remove) list.RemoveAll(b => b.Command == command && (gestures is null || b.Gestures.SequenceEqual(gestures)));
            else if (gestures is not null) list.Insert(0, (gestures, command, source));
        }

        _bindings = list;
        _editorActions = [.. commands.Where(c => c.EditorKeys).Select(c => c.Id)];
    }

    /// <summary>The keys that run a command now, in Avalonia's names ("Ctrl+OemPlus", "Ctrl+D1"; "" when none).</summary>
    public string KeysFor(string commandId) =>
        _bindings.FirstOrDefault(b => b.Command.Id == commandId) is { Command: not null } b ? string.Join(' ', b.Gestures.Select(g => g.ToString()))
        : _editorActions.Contains(commandId) && Wicked.App.Editor.EditorKeymap.Effective.FirstOrDefault(d => d.Command.Id == commandId) is { Command: not null } e ? e.Gesture.ToString()
        : "";

    /// <summary>Every binding in force, in Avalonia's names, the ones keybindings.json added marked: the Keyboard Shortcuts editor lists these.</summary>
    public IEnumerable<(string Keys, WorkbenchCommand Command, string Source)> Bindings =>
        _bindings.Select(b => (string.Join(' ', b.Gestures.Select(g => g.ToString())), b.Command, b.Source));

    /// <summary>The keys that run a command now, as people read them ("Ctrl+=", "Ctrl+1"): the palette, menus and help show these.</summary>
    public string DisplayKeysFor(string commandId) => KeyNames.Readable(KeysFor(commandId));

    /// <summary>The first half of a chord is waiting for its second ("Ctrl+K was pressed...").</summary>
    public bool ChordPending => _pending is not null;

    public WorkbenchCommand? Match(KeyEventArgs e)
    {
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return null;
        if (_pending is { } first)
        {
            _pending = null;
            return _bindings.FirstOrDefault(b => b.Gestures.Length == 2 && b.Gestures[0].Equals(first) && b.Gestures[1].Matches(e) && b.Command.Applies).Command;
        }

        var single = _bindings.FirstOrDefault(b => b.Gestures.Length == 1 && b.Gestures[0].Matches(e) && b.Command.Applies).Command;
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

/// <summary>Avalonia's key names as people read them, the way VS Code writes them: "Ctrl+OemPlus" is "Ctrl+=".</summary>
public static class KeyNames
{
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        ["OemPlus"] = "=", ["OemMinus"] = "-", ["OemComma"] = ",", ["OemPeriod"] = ".", ["OemTilde"] = "`", ["Oem3"] = "`",
        ["OemPipe"] = "\\", ["Oem5"] = "\\", ["OemBackslash"] = "\\", ["OemOpenBrackets"] = "[", ["Oem4"] = "[",
        ["OemCloseBrackets"] = "]", ["Oem6"] = "]", ["OemQuestion"] = "/", ["Oem2"] = "/", ["OemSemicolon"] = ";", ["Oem1"] = ";",
        ["OemQuotes"] = "'", ["Oem7"] = "'", ["Add"] = "Num +", ["Subtract"] = "Num -", ["Multiply"] = "Num *", ["Divide"] = "Num /",
        ["Return"] = "Enter", ["Back"] = "Backspace", ["Next"] = "PageDown", ["Prior"] = "PageUp", ["Escape"] = "Escape",
    };

    /// <summary>"Ctrl+K Ctrl+OemPlus" → "Ctrl+K Ctrl+="; digits lose their D ("Ctrl+D1" → "Ctrl+1").</summary>
    public static string Readable(string keys) =>
        string.Join(' ', keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(chord =>
            string.Join('+', chord.Split('+').Select(Name))));

    private static string Name(string part) =>
        Names.TryGetValue(part, out var name) ? name
        : part.Length == 2 && part[0] == 'D' && char.IsAsciiDigit(part[1]) ? part[1..]
        : part.StartsWith("NumPad", StringComparison.Ordinal) && part.Length == 7 ? "Num " + part[6]
        : part;
}
