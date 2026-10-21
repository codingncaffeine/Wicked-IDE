using Avalonia.Input;
using Wicked.App.Models;
using Wicked.App.Shell;
using Wicked.App.Terminal;

namespace Wicked.App.ViewModels;

/// <summary>The integrated terminal in the window: a tab of the bottom panel, its commands, links into the editor.</summary>
public sealed partial class ShellViewModel
{
    /// <summary>The terminals (Ctrl+`): tabs, splits and profiles; shells start in the open folder.</summary>
    public TerminalPanelViewModel Terminal { get; private set; } = null!;

    private void WireTerminal()
    {
        Terminal = new TerminalPanelViewModel(Paths, Workbench.Root);
        BottomPanel.Add(PanelIds.Terminal, "TERMINAL", () =>
        {
            Terminal.ApplyTheme(TerminalTheme.FromResources());
            return new TerminalPanelView { DataContext = Terminal };
        });
        Terminal.OpenFileRequested += target => _ = OpenAtAsync(target.Path, Math.Max(0, (target.Line ?? 1) - 1), Math.Max(0, (target.Column ?? 1) - 1));
        Terminal.OpenUrlRequested += url => _ = Task.Run(() => Opener.Open(url));
        Terminal.TerminalAdded += instance =>
        {
            BottomPanel.Show(PanelIds.Terminal);
            instance.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TerminalInstanceViewModel.Pane) && instance.Pane is { } pane) pane.View.SkipShell = KeepsKey;
            };
            if (instance.Pane is { } ready) ready.View.SkipShell = KeepsKey;
        };
        Terminal.AllClosed += () =>
        {
            if (BottomPanel.Active?.Id == PanelIds.Terminal) BottomPanel.IsVisible = false;
        };
        Workbench.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Workbench.Root)) Terminal.WorkingDirectory = Workbench.Root;
        };
    }

    /// <summary>
    /// Keys the window keeps while a terminal has the keyboard (VS Code's commandsToSkipShell): the palette, quick open,
    /// the panel and side bar toggles, chords, editor switching, F-keys. Everything else goes to the shell.
    /// </summary>
    private bool KeepsKey(KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        if (e.Key is >= Key.F1 and <= Key.F12) return true;
        if (m == KeyModifiers.Control && e.Key is Key.P or Key.B or Key.J or Key.K or Key.OemTilde or Key.Tab or Key.PageUp or Key.PageDown or Key.OemComma) return true;
        if (m == (KeyModifiers.Control | KeyModifiers.Shift) && e.Key is Key.P or Key.E or Key.F or Key.G or Key.M or Key.OemTilde or Key.D5 or Key.U) return true;
        return false;
    }

    private IEnumerable<WorkbenchCommand> TerminalCommands()
    {
        Task Sync(Action a)
        {
            a();
            return Task.CompletedTask;
        }

        return
        [
            new("workbench.action.terminal.toggleTerminal", "Terminal", "Toggle Terminal", "Ctrl+OemTilde", () => Sync(ToggleTerminal)),
            new("workbench.action.terminal.new", "Terminal", "Create New Terminal", "Ctrl+Shift+OemTilde", () => Sync(() => Terminal.NewTerminal())),
            new("workbench.action.terminal.split", "Terminal", "Split Terminal", "Ctrl+Shift+D5", () => Sync(() => Terminal.Split())),
            new("workbench.action.terminal.kill", "Terminal", "Kill the Active Terminal", null, () => Sync(Terminal.KillActive)),
            new("workbench.action.terminal.clear", "Terminal", "Clear", null, () => Sync(Terminal.ClearActive)),
            new("workbench.action.terminal.focusNext", "Terminal", "Focus Next Terminal", null, () => Sync(() => Terminal.CycleGroup(1))),
            new("workbench.action.terminal.focusPrevious", "Terminal", "Focus Previous Terminal", null, () => Sync(() => Terminal.CycleGroup(-1))),
            new("workbench.action.terminal.runSelectedText", "Terminal", "Run Selected Text in Active Terminal", null, RunSelectionAsync),
        ];
    }

    /// <summary>Ctrl+`: the terminal shown (one started when there is none), or the panel hidden when it already shows it.</summary>
    private void ToggleTerminal()
    {
        if (BottomPanel.IsVisible && BottomPanel.Active?.Id == PanelIds.Terminal)
        {
            BottomPanel.IsVisible = false;
            Workbench.ActiveTab?.Editor?.Focus();
            return;
        }

        if (!Terminal.AllTerminals().Any()) Terminal.NewTerminal();
        BottomPanel.Show(PanelIds.Terminal);
    }

    private Task RunSelectionAsync()
    {
        if (Workbench.ActiveTab?.Document is not { } doc) return Task.CompletedTask;
        var p = doc.Primary;
        var text = p.IsEmpty ? doc.Text.Line(doc.Text.LineOf(p.Active)) : doc.Text.Substring(p.Start, p.Length);
        BottomPanel.Show(PanelIds.Terminal);
        return Terminal.RunInActiveTerminal(text);
    }
}
