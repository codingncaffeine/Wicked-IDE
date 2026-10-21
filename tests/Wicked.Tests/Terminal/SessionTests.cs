using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

/// <summary>Collects a session's frames and events for a test.</summary>
internal sealed class SessionWatcher
{
    private readonly TerminalSession _session;
    private volatile TerminalFrame? _frame;
    private readonly Lock _gate = new();

    public SessionWatcher(TerminalSession session)
    {
        _session = session;
        session.FrameReady += frame => _frame = frame;
        session.WorkingDirectoryChanged += dir =>
        {
            lock (_gate) Directories.Add(dir);
        };
        session.TitleChanged += title =>
        {
            lock (_gate) Titles.Add(title);
        };
        session.Bell += () => Interlocked.Increment(ref _bells);
    }

    private int _bells;

    public List<string> Directories { get; } = [];

    public List<string> Titles { get; } = [];

    public int Bells => Volatile.Read(ref _bells);

    public TerminalFrame? Frame => _frame;

    /// <summary>The text of the latest frame.</summary>
    public string Screen
    {
        get
        {
            var frame = _frame;
            if (frame is null) return "";
            var rows = frame.Lines.Select(row => string.Concat(row.Cells.Where(c => (c.Flags & CellFlags.WideTail) == 0).Select(c => frame.TextOf(c))).TrimEnd());
            return string.Join('\n', rows).TrimEnd();
        }
    }

    public async Task<bool> WaitForScreenAsync(Func<string, bool> predicate, double seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (predicate(Screen)) return true;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return predicate(Screen);
    }

    public async Task<bool> WaitForAsync(Func<Task<bool>> predicate, double seconds = 20)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await predicate()) return true;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        return await predicate();
    }
}

public sealed class SessionTests
{
    private static TerminalSession Start(ScratchDir home, string script, out SessionWatcher watch, int columns = 80, int rows = 24, TerminalOptions? terminal = null)
    {
        var env = Pty.BuildEnvironment(new Dictionary<string, string?> { ["HOME"] = home.Root, ["ENV"] = Path.Combine(home.Root, "none"), ["DBUS_SESSION_BUS_ADDRESS"] = null, ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = null });
        var session = new TerminalSession(new TerminalSessionOptions
        {
            Launch = new PtyOptions("/bin/sh", ["-c", script], home.Root, env, columns, rows),
            Terminal = terminal ?? new TerminalOptions(),
        });
        watch = new SessionWatcher(session);
        session.Start();
        return session;
    }

    [Fact]
    public async Task OutputReachesTheScreenAndTheExitIsReported()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "printf 'hello\\nworld'; exit 5", out var watch);
        Assert.True(await watch.WaitForAsync(() => Task.FromResult(session.ExitCode is not null)));
        Assert.True(await watch.WaitForScreenAsync(s => s == "hello\nworld"), watch.Screen);
        Assert.Equal(5, session.ExitCode);
    }

    [Fact]
    public async Task TheTitleAndTheBellComeThrough()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "printf '\\033]0;a title\\007\\007'", out var watch);
        Assert.True(await watch.WaitForAsync(() => Task.FromResult(watch.Titles.Count > 0 && watch.Bells > 0)));
        Assert.Equal("a title", watch.Titles[0]);
    }

    [Fact]
    public async Task ResizingTellsTheProgramAndReflowsTheScreen()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "stty size; read x; stty size", out var watch);
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("24 80", StringComparison.Ordinal)), watch.Screen);
        session.Resize(100, 30);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        session.Input("\r");
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("30 100", StringComparison.Ordinal)), watch.Screen);
        Assert.Equal(100, watch.Frame!.Columns);
        Assert.Equal(30, watch.Frame.Rows);
    }

    [Fact]
    public async Task APasteIsBracketedOnlyWhenTheProgramAsksForIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "printf '\\033[?2004h'; stty -echo; cat -v", out var watch);
        Assert.True(await watch.WaitForAsync(() => Task.FromResult(session.Modes.BracketedPaste)));
        session.Paste("one\ntwo\u001b[201~three\r\n");
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("^[[200~one", StringComparison.Ordinal) && s.Contains("three", StringComparison.Ordinal)), watch.Screen);
        Assert.DoesNotContain("^[[201~three", watch.Screen);
        Assert.Contains("[201~three", watch.Screen);
    }

    [Fact]
    public async Task APasteIsPlainWhenTheModeIsOff()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "stty -echo; cat -v", out var watch);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        session.Paste("a\nb\n");
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains('a', StringComparison.Ordinal) && s.Contains('b', StringComparison.Ordinal)), watch.Screen);
        Assert.DoesNotContain("200~", watch.Screen);
    }

    [Fact]
    public async Task ReportsGoBackToTheProgram()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        // The shell asks where the cursor is and prints the answer it reads back.
        using var session = Start(home, "printf 'ab\\033[6n'; IFS=R read -r -d R reply; printf '|%s|' \"$reply\" | cat -v", out var watch);
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("|^[[1;3|", StringComparison.Ordinal)), watch.Screen);
    }

    [Fact]
    public async Task TheViewportScrollsAndSnapsBack()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "i=1; while [ $i -le 100 ]; do echo line$i; i=$((i+1)); done; sleep 30", out var watch, rows: 10);
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("line100", StringComparison.Ordinal)), watch.Screen);
        session.ScrollViewport(5);
        Assert.True(await watch.WaitForAsync(() => Task.FromResult(watch.Frame!.ViewportOffset == 5)));
        Assert.DoesNotContain("line100", watch.Screen);
        session.ScrollViewportToBottom();
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("line100", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SelectionTextAndSearchRunWithoutTheCallerTouchingTheEmulator()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "echo alpha; echo beta; echo alpha again; sleep 30", out var watch);
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("alpha again", StringComparison.Ordinal)), watch.Screen);
        var text = await session.GetTextAsync(new TerminalSelection(new TerminalPoint(0, 0), new TerminalPoint(1, 3)));
        Assert.Equal("alpha\nbeta", text);
        var (_, matches) = await session.SearchAsync("alpha", default, TestContext.Current.CancellationToken);
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public async Task DisposingEndsTheProgram()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        var session = Start(home, "sleep 300", out _);
        var pid = session.ProcessId;
        Assert.True(Directory.Exists(Path.Combine(Path.DirectorySeparatorChar + "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        session.Dispose();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && Directory.Exists(Path.Combine(Path.DirectorySeparatorChar + "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)))) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(Path.Combine(Path.DirectorySeparatorChar + "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public async Task FramesAreNotBuiltFasterThanTheInterval()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var home = new ScratchDir();
        using var session = Start(home, "i=0; while [ $i -lt 3000 ]; do echo line$i; i=$((i+1)); done", out var watch);
        var frames = 0;
        session.FrameReady += _ => Interlocked.Increment(ref frames);
        await watch.WaitForAsync(() => Task.FromResult(session.ExitCode is not null));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(frames < 200, $"{frames} frames");
        Assert.True(await watch.WaitForScreenAsync(s => s.Contains("line2999", StringComparison.Ordinal)));
    }
}
