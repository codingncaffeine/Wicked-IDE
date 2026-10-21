using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.Terminal;
using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

/// <summary>
/// The terminal control in a real (headless) window: a real shell on a real pty, keys and the mouse through the window.
/// Avalonia has one keyboard focus for all windows, so each test makes its window, types into it and looks at it inside a
/// single turn on the UI thread; a second test's window opening in between would take the focus.
/// </summary>
public sealed class TerminalWindowTests
{
    private sealed class Rig
    {
        public required Window Window { get; init; }

        public required TerminalPane Pane { get; init; }

        public required TerminalSession Session { get; init; }

        public required ScratchDir Home { get; init; }

        public TerminalView View => Pane.View;

        public string Screen
        {
            get
            {
                var frame = View.Frame;
                if (frame is null) return "";
                var rows = frame.Lines.Select(row => string.Concat(row.Cells.Where(c => (c.Flags & CellFlags.WideTail) == 0).Select(c => frame.TextOf(c))).TrimEnd());
                return string.Join('\n', rows).TrimEnd();
            }
        }

        /// <summary>The centre of a cell, in window coordinates.</summary>
        public Point CellCentre(int row, int column)
        {
            var m = View.Metrics;
            var local = new Point(14 + (column + 0.5) * m.Width, (row + 0.5) * m.Height);
            return View.TranslatePoint(local, Window) ?? throw new InvalidOperationException("the terminal is not in the window");
        }

        /// <summary>For programs that echo their input with cat -v: returns once an Enter sent from here has come back, so the program is reading.</summary>
        public void WaitUntilReading()
        {
            Assert.True(WaitForScreen(s => s.Contains("ready", StringComparison.Ordinal)));
            Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(WaitForScreen(s => s.Contains("^M", StringComparison.Ordinal)), Screen);
        }

        public bool WaitForScreen(Func<string, bool> predicate, double seconds = 20) => Ui.PumpUntil(() => predicate(Screen), seconds);
    }

    /// <summary>
    /// Opens a window on a shell running <paramref name="script"/>, runs <paramref name="body"/> in the same turn on the UI
    /// thread, then <paramref name="after"/> (for questions that wait on the session), and closes everything.
    /// </summary>
    private static async Task RunAsync(string script, Action<Rig> body, Func<Rig, Task>? after = null)
    {
        var fonts = await TerminalFonts.GetAsync(14);
        using var home = new ScratchDir();
        var env = Pty.BuildEnvironment(new Dictionary<string, string?> { ["HOME"] = home.Root, ["ENV"] = Path.Combine(home.Root, "none"), ["PS1"] = "$ ", ["DBUS_SESSION_BUS_ADDRESS"] = null, ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = null });
        using var session = new TerminalSession(new TerminalSessionOptions { Launch = new PtyOptions("/bin/sh", ["-c", script], home.Root, env, 80, 24) });
        try
        {
            var rig = await Ui.Run(() =>
            {
                var pane = new TerminalPane(fonts);
                var window = new Window { Width = 900, Height = 500, Content = pane };
                window.Show();
                window.Activate();
                pane.View.Attach(session);
                Ui.Pump(0.2);
                // Starting a process is not for the UI thread: the test starts it from here only because nothing else is running.
                session.Start();
                pane.View.Focus();
                // Keys sent before the view has the focus would go nowhere.
                Assert.True(Ui.PumpUntil(() => pane.View.IsFocused, 10), "the terminal did not take the focus");
                var made = new Rig { Window = window, Pane = pane, Session = session, Home = home };
                try
                {
                    body(made);
                }
                finally
                {
                    // Closed in the same turn: another test's window must not find this one still open.
                    window.Close();
                }

                return made;
            });
            if (after is not null) await after(rig);
        }
        finally
        {
            session.Dispose();
        }
    }

    [Fact]
    public async Task TypingThroughTheWindowReachesTheProgramAndItsAnswerIsDrawn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("cat", rig =>
        {
            rig.Window.KeyTextInput("hello terminal");
            rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(rig.WaitForScreen(s => s.Split('\n').Count(l => l.Contains("hello terminal", StringComparison.Ordinal)) == 2), rig.Screen);
        });
    }

    [Fact]
    public async Task TheGridFollowsTheWindowAndTheProgramIsToldItsSize()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("read x; stty size; sleep 30", rig =>
        {
            Ui.Pump(0.5);
            var (columns, rows) = rig.View.GridSize;
            rig.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(rig.WaitForScreen(s => s.Contains($"{rows} {columns}", StringComparison.Ordinal)), $"{rows} {columns}: {rig.Screen}");
            Assert.InRange(columns, 60, 120);
            rig.Window.Width = 1200;
            rig.Window.Height = 700;
            Ui.Pump(0.5);
            var (columns2, rows2) = rig.View.GridSize;
            Assert.True(columns2 > columns && rows2 > rows);
            Assert.Equal(columns2, rig.Session.Columns);
        });
    }

    [Fact]
    public async Task TheKeyEncodingFollowsTheProgramsModes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("printf '\\033[?1h'; stty -echo -icanon -isig -icrnl; echo ready; cat -v", rig =>
        {
            Assert.True(Ui.PumpUntil(() => rig.Session.Modes.ApplicationCursor, 10));
            rig.WaitUntilReading();
            rig.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            rig.Window.KeyPress(Key.Left, RawInputModifiers.Control, PhysicalKey.ArrowLeft, null);
            rig.Window.KeyPress(Key.F5, RawInputModifiers.None, PhysicalKey.F5, null);
            rig.Window.KeyPress(Key.X, RawInputModifiers.Alt, PhysicalKey.X, "x");
            Assert.True(rig.WaitForScreen(s => s.Contains("^[OA^[[1;5D^[[15~^[x", StringComparison.Ordinal)), rig.Screen);
        });
    }

    [Fact]
    public async Task ControlCStopsARunningProgram()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("echo ready; sleep 300", rig =>
        {
            Assert.True(rig.WaitForScreen(s => s.Contains("ready", StringComparison.Ordinal)));
            Ui.Pump(0.3);
            rig.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, null);
            Assert.True(Ui.PumpUntil(() => rig.Session.ExitCode is not null, 10));
            Assert.Equal(130, rig.Session.ExitCode);
        });
    }

    [Fact]
    public async Task AProgramThatAsksForTheMouseGetsClicksAndTheWheel()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("printf '\\033[?1000h\\033[?1006h'; stty -echo -icanon -icrnl; echo ready; cat -v", rig =>
        {
            Assert.True(Ui.PumpUntil(() => rig.Session.Modes.Mouse == MouseTracking.Buttons, 10));
            rig.WaitUntilReading();
            var point = rig.CellCentre(4, 9);
            rig.Window.MouseDown(point, MouseButton.Left);
            rig.Window.MouseUp(point, MouseButton.Left);
            Assert.True(rig.WaitForScreen(s => s.Contains("^[[<0;10;5M^[[<0;10;5m", StringComparison.Ordinal)), rig.Screen);
            rig.Window.MouseWheel(point, new Vector(0, 1));
            Assert.True(rig.WaitForScreen(s => s.Contains("^[[<64;10;5M", StringComparison.Ordinal)), rig.Screen);
        });
    }

    [Fact]
    public async Task DraggingSelectsTextAndTheSelectionIsReadBackFromTheBuffer()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("printf 'first line\\r\\nsecond line here\\r\\n'; sleep 30", rig =>
        {
            Assert.True(rig.WaitForScreen(s => s.Contains("second line here", StringComparison.Ordinal)));
            rig.Window.MouseDown(rig.CellCentre(0, 6), MouseButton.Left);
            rig.Window.MouseMove(rig.CellCentre(1, 5), RawInputModifiers.LeftMouseButton);
            rig.Window.MouseUp(rig.CellCentre(1, 5), MouseButton.Left);
            Ui.Pump(0.1);
            Assert.True(rig.View.HasSelection);
        }, async rig => Assert.Equal("line\nsecond", await rig.Session.GetTextAsync(rig.View.Selection!.Value)));
    }

    [Fact]
    public async Task DoubleClickSelectsAWord()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("printf 'alpha beta-gamma delta\\r\\n'; sleep 30", rig =>
        {
            Assert.True(rig.WaitForScreen(s => s.Contains("delta", StringComparison.Ordinal)));
            var point = rig.CellCentre(0, 8);
            // Two presses in the same place in quick succession are a double click; the word is looked up on the emulator's thread.
            rig.Window.MouseDown(point, MouseButton.Left);
            rig.Window.MouseUp(point, MouseButton.Left);
            rig.Window.MouseDown(point, MouseButton.Left);
            rig.Window.MouseUp(point, MouseButton.Left);
            Assert.True(Ui.PumpUntil(() => rig.View.Selection is { IsEmpty: false }, 10));
        }, async rig => Assert.Equal("beta-gamma", await rig.Session.GetTextAsync(rig.View.Selection!.Value)));
    }

    [Fact]
    public async Task TheWheelScrollsTheHistoryAndTypingBringsTheBottomBack()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("i=1; while [ $i -le 200 ]; do echo line$i; i=$((i+1)); done; cat", rig =>
        {
            Assert.True(rig.WaitForScreen(s => s.Contains("line200", StringComparison.Ordinal)));
            rig.Window.MouseWheel(rig.CellCentre(3, 3), new Vector(0, 5));
            Assert.True(Ui.PumpUntil(() => rig.View.Frame is { ViewportOffset: > 0 }, 10));
            Assert.DoesNotContain("line200", rig.Screen);
            rig.Window.KeyTextInput("q");
            Assert.True(Ui.PumpUntil(() => rig.View.Frame is { ViewportOffset: 0 }, 10));
        });
    }

    [Fact]
    public async Task ControlClickOnAFileReferenceFollowsIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        TerminalLinkActivation? followed = null;
        await RunAsync("printf 'error in src/app.cs:12:5 here\\r\\n'; sleep 30", rig =>
        {
            rig.View.LinkActivated += link => followed = link;
            Assert.True(rig.WaitForScreen(s => s.Contains("src/app.cs", StringComparison.Ordinal)));
            var point = rig.CellCentre(0, 14);
            // The link under the pointer is looked up on a worker; the click after it follows.
            rig.Window.MouseMove(point);
            Ui.Pump(0.4);
            rig.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.Control);
            rig.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.Control);
            Ui.Pump(0.1);
        });
        Assert.NotNull(followed);
        Assert.Equal("src/app.cs", followed!.Target);
        Assert.Equal(12, followed.Line);
        Assert.Equal(5, followed.Column);
    }

    [Fact]
    public async Task TheFindWidgetSearchesTheScrollbackAndHighlightsTheMatches()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("i=1; while [ $i -le 100 ]; do echo item$i; i=$((i+1)); done; sleep 30", rig =>
        {
            Assert.True(rig.WaitForScreen(s => s.Contains("item100", StringComparison.Ordinal)));
            rig.Window.KeyPress(Key.F, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.F, null);
            Assert.True(Ui.PumpUntil(() => rig.Pane.Find.IsOpen, 10));
            rig.Pane.Find.Query = "item7";
            Assert.True(Ui.PumpUntil(() => rig.Pane.Find.MatchCount == 11, 10), $"{rig.Pane.Find.MatchCount} {rig.Pane.Find.Status}");
            // It starts at the first match on or below the screen; going round to the first match, far above, scrolls the view to it.
            for (var i = 0; i < 11 && rig.Pane.Find.CurrentIndex != 0; i++) rig.Pane.Find.Next();
            Assert.StartsWith("1 of 11", rig.Pane.Find.Status);
            Assert.True(Ui.PumpUntil(() => rig.View.Frame is { ViewportOffset: > 0 }, 10));
            rig.Pane.Find.Close();
            Assert.False(rig.Pane.Find.IsOpen);
        });
    }

    [Fact]
    public async Task TheBellOnlyFlashesTheScreen()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("printf 'a\\007b\\r\\n'; sleep 30", rig =>
        {
            Assert.True(rig.WaitForScreen(s => s.Contains("ab", StringComparison.Ordinal)));
            Assert.Equal(1, rig.View.Frame!.BellCount);
            Ui.Pump(0.3);
        });
    }

    [Fact]
    public async Task APasteGoesToTheProgramInOneBlock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("printf '\\033[?2004h'; stty -echo -icanon -icrnl; echo ready; cat -v", rig =>
        {
            Assert.True(Ui.PumpUntil(() => rig.Session.Modes.BracketedPaste, 10));
            rig.WaitUntilReading();
            rig.Session.Paste("one\ntwo");
            Assert.True(rig.WaitForScreen(s => s.Contains("^[[200~one", StringComparison.Ordinal) && s.Contains("two^[[201~", StringComparison.Ordinal)), rig.Screen);
        });
    }

    [Fact]
    public async Task TheCursorIsDrawnWhereTheProgramLeftIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        await RunAsync("printf 'abc\\033[3;7H'; sleep 30", rig => Assert.True(Ui.PumpUntil(() => rig.View.Frame is { CursorRow: 2, CursorColumn: 6 }, 10)));
    }
}
