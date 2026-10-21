using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Wicked.Tests.Terminal;

public sealed class PtyTests
{
    private static string ProcPath(int pid) => Path.Combine(Path.DirectorySeparatorChar + "proc", pid.ToString(CultureInfo.InvariantCulture));

    private static void UnixOnly() => Assert.SkipWhen(OperatingSystem.IsWindows(), "these tests start /bin/sh");
    [Fact]
    public async Task ARunProgramsOutputAndExitCodeComeBack()
    {
        UnixOnly();
        using var home = new ScratchDir();
        using var run = new PtyRun("/bin/sh", ["-c", "printf 'hello %s' world; exit 3"], home.Root, home);
        Assert.Equal(3, await run.ExitAsync());
        Assert.Equal("hello world", run.Output);
    }

    [Fact]
    public async Task TheProgramSeesTheTerminalsEnvironmentAndFolder()
    {
        UnixOnly();
        using var home = new ScratchDir();
        using var run = new PtyRun("/bin/sh", ["-c", "echo \"$TERM|$COLORTERM|$TERM_PROGRAM|$HOME|$(pwd)\""], home.Root, home);
        await run.ExitAsync();
        Assert.Contains($"xterm-256color|truecolor|Wicked|{home.Root}|{home.Root}", run.Output);
    }

    [Fact]
    public async Task TheProgramHasAControllingTerminalAndLeadsItsOwnSession()
    {
        UnixOnly();
        using var home = new ScratchDir();
        // Job control needs the pty to be the controlling terminal and the shell to lead the foreground group.
        using var run = new PtyRun("/bin/sh", ["-c", "tty; ps -o pid=,sid=,tpgid= -p $$"], home.Root, home);
        await run.ExitAsync();
        var lines = run.Output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("/dev/pts/", lines[0]); // not a path
        var ids = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(ids[0], ids[2]);
    }

    [Fact]
    public async Task InputReachesTheProgramAndEchoesBack()
    {
        UnixOnly();
        using var home = new ScratchDir();
        using var run = new PtyRun("/bin/cat", [], home.Root, home);
        run.Session.Write(Encoding.UTF8.GetBytes("typed text\n"));
        Assert.True(await run.WaitForAsync("typed text\r\ntyped text"));
        run.Session.Write([0x04]);
        Assert.Equal(0, await run.ExitAsync());
    }

    [Fact]
    public async Task ResizingChangesTheSizeTheProgramReads()
    {
        UnixOnly();
        using var home = new ScratchDir();
        using var run = new PtyRun("/bin/sh", ["-c", "stty size; read x; stty size"], home.Root, home, columns: 80, rows: 24);
        Assert.True(await run.WaitForAsync("24 80"));
        run.Session.Resize(132, 43);
        run.Session.Write("\n"u8);
        Assert.True(await run.WaitForAsync("43 132"));
        await run.ExitAsync();
    }

    [Fact]
    public async Task ClosingKillsTheProgramsWholeProcessGroup()
    {
        UnixOnly();
        using var home = new ScratchDir();
        var pidFile = Path.Combine(home.Root, "child.pid");
        using var run = new PtyRun("/bin/sh", ["-c", $"sleep 300 & echo $! > '{pidFile}'; wait"], home.Root, home);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(pidFile) && DateTime.UtcNow < until) await Task.Delay(20, TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var child = int.Parse((await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken)).Trim(), CultureInfo.InvariantCulture);
        Assert.True(Directory.Exists(ProcPath(child)));

        run.Session.Kill();
        await run.ExitAsync();
        var gone = Stopwatch.StartNew();
        while (Directory.Exists(ProcPath(child)) && gone.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(ProcPath(child)));
    }

    [Fact]
    public async Task ABurstOfOutputArrivesWhole()
    {
        UnixOnly();
        using var home = new ScratchDir();
        using var run = new PtyRun("/bin/sh", ["-c", "i=0; while [ $i -lt 2000 ]; do echo line-$i; i=$((i+1)); done; echo done-marker"], home.Root, home);
        await run.ExitAsync();
        Assert.Contains("line-0\r\n", run.Output);
        Assert.Contains("line-1999\r\n", run.Output);
        Assert.EndsWith("done-marker\r\n", run.Output);
    }

    [Fact]
    public async Task ADeathBySignalIsReportedAsShellsDo()
    {
        UnixOnly();
        using var home = new ScratchDir();
        using var run = new PtyRun("/bin/sh", ["-c", "kill -9 $$"], home.Root, home);
        Assert.Equal(137, await run.ExitAsync());
    }

    [Fact]
    public void AProgramThatDoesNotExistFailsToStart()
    {
        UnixOnly();
        using var home = new ScratchDir();
        Assert.Throws<IOException>(() => new PtyRun("/no/such/program", [], home.Root, home));
    }
}

public sealed class WindowsCommandLineTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
    [InlineData("", "\"\"")]
    public void AnArgumentIsQuotedAsCreateProcessReadsItBack(string argument, string expected) =>
        Assert.Equal(expected, Wicked.Core.Terminal.WindowsPty.Quote(argument));
}
