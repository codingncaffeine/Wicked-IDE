using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class TextAndCommandTests
{
    private static TerminalSelection Sel(long l1, int c1, long l2, int c2, SelectionKind kind = SelectionKind.Stream) =>
        new(new TerminalPoint(l1, c1), new TerminalPoint(l2, c2), kind);

    [Fact]
    public void ASelectionInsideALineGivesThatText()
    {
        var screen = new VtScreen().Feed("hello world");
        Assert.Equal("lo wo", screen.Emulator.GetText(Sel(0, 3, 0, 7)));
    }

    [Fact]
    public void ASelectionAcrossLinesPutsNewlinesWhereTheProgramEndedTheLine()
    {
        var screen = new VtScreen().Feed("one\r\ntwo\r\nthree");
        Assert.Equal("ne\ntwo\nth", screen.Emulator.GetText(Sel(0, 1, 2, 1)));
    }

    [Fact]
    public void AWrappedLineIsCopiedAsOne()
    {
        var screen = new VtScreen(10, 4).Feed("0123456789abcdef\r\nnext");
        Assert.Equal("0123456789abcdef", screen.Emulator.GetText(Sel(0, 0, 1, 9)));
        Assert.Equal("0123456789abcdef\nnext", screen.Emulator.GetText(Sel(0, 0, 2, 9)));
    }

    [Fact]
    public void TrailingBlanksOfALineAreNotCopied()
    {
        var screen = new VtScreen().Feed("ab\r\ncd");
        Assert.Equal("ab\ncd", screen.Emulator.GetText(Sel(0, 0, 1, 19)));
    }

    [Fact]
    public void WholeLinesAreCopiedInLineMode()
    {
        var screen = new VtScreen().Feed("one\r\ntwo\r\nthree");
        Assert.Equal("one\ntwo", screen.Emulator.GetText(Sel(0, 2, 1, 0, SelectionKind.Lines)));
    }

    [Fact]
    public void ABlockTakesTheSameColumnsOfEveryLine()
    {
        var screen = new VtScreen().Feed("abcdef\r\nghijkl\r\nmnopqr");
        Assert.Equal("cd\nij\nop", screen.Emulator.GetText(Sel(0, 2, 2, 3, SelectionKind.Block)));
    }

    [Fact]
    public void TheEndsOfASelectionMayBeGivenInEitherOrder()
    {
        var screen = new VtScreen().Feed("hello world");
        Assert.Equal("lo wo", screen.Emulator.GetText(Sel(0, 7, 0, 3)));
    }

    [Fact]
    public void WideAndCombinedCharactersAreCopiedWhole()
    {
        var screen = new VtScreen().Feed("a漢éz");
        Assert.Equal("漢é", screen.Emulator.GetText(Sel(0, 1, 0, 3)));
    }

    [Fact]
    public void ScrollbackLinesCanBeCopied()
    {
        var screen = new VtScreen(10, 2).Feed("first\r\nsecond\r\nthird");
        Assert.Equal("first\nsecond", screen.Emulator.GetText(Sel(screen.Emulator.BaseLine, 0, screen.Emulator.BaseLine + 1, 9)));
    }

    [Fact]
    public void AWordIsFoundByItsBounds()
    {
        var screen = new VtScreen(30, 3).Feed("run ./build.sh --flag=1 now");
        Assert.Equal((4, 13), screen.Emulator.WordAt(0, 6));
        Assert.Equal((15, 22), screen.Emulator.WordAt(0, 15));
        Assert.Null(screen.Emulator.WordAt(0, 3));
        Assert.Equal((0, 2), screen.Emulator.WordAt(0, 0));
    }

    [Fact]
    public void BracketsAndQuotesEndAWord()
    {
        var screen = new VtScreen(30, 3).Feed("call(arg) \"quoted\"");
        Assert.Equal((0, 3), screen.Emulator.WordAt(0, 1));
        Assert.Equal((5, 7), screen.Emulator.WordAt(0, 6));
        Assert.Equal((11, 16), screen.Emulator.WordAt(0, 12));
    }

    [Fact]
    public void ALogicalLineSpansItsWrappedRows()
    {
        var screen = new VtScreen(10, 5).Feed("0123456789abcdefghij0123\r\nnext");
        Assert.Equal((0, 2), screen.Emulator.LogicalLineAt(1));
        Assert.Equal((3, 3), screen.Emulator.LogicalLineAt(3));
    }

    // ── Search ───────────────────────────────────────────────────────────────────────────────────

    private static List<TerminalMatch> Find(VtScreen screen, string query, SearchOptions options = default) =>
        screen.Emulator.SnapshotForSearch().Find(query, options, CancellationToken.None);

    [Fact]
    public void SearchFindsEveryMatchIncludingInTheScrollback()
    {
        var screen = new VtScreen(20, 3).Feed("error one\r\nfine\r\nerror two\r\nok\r\nerror three");
        var matches = Find(screen, "error");
        Assert.Equal(3, matches.Count);
        Assert.Equal(new TerminalPoint(0, 0), matches[0].Start);
        Assert.Equal(new TerminalPoint(0, 5), matches[0].End);
        Assert.Equal(new TerminalPoint(4, 0), matches[2].Start);
    }

    [Fact]
    public void SearchIgnoresCaseUnlessAsked()
    {
        var screen = new VtScreen().Feed("Error error ERROR");
        Assert.Equal(3, Find(screen, "error").Count);
        Assert.Single(Find(screen, "error", new SearchOptions(CaseSensitive: true)));
    }

    [Fact]
    public void SearchCanTakeWholeWordsOnly()
    {
        var screen = new VtScreen().Feed("cat concat cat.");
        Assert.Equal(3, Find(screen, "cat").Count);
        Assert.Equal(2, Find(screen, "cat", new SearchOptions(WholeWord: true)).Count);
    }

    [Fact]
    public void SearchCanUseARegularExpression()
    {
        var screen = new VtScreen().Feed("a1 b22 c333");
        var matches = Find(screen, @"\d+", new SearchOptions(Regex: true));
        Assert.Equal(3, matches.Count);
        Assert.Equal(new TerminalPoint(0, 4), matches[1].Start);
        Assert.Equal(new TerminalPoint(0, 6), matches[1].End);
    }

    [Fact]
    public void ABadRegularExpressionFindsNothing()
    {
        Assert.Empty(Find(new VtScreen().Feed("(("), "(", new SearchOptions(Regex: true)));
    }

    [Fact]
    public void AMatchCanRunAcrossAWrappedRow()
    {
        var screen = new VtScreen(10, 4).Feed("xxxxxxxxneedle yy");
        var match = Assert.Single(Find(screen, "needle"));
        Assert.Equal(new TerminalPoint(0, 8), match.Start);
        Assert.Equal(new TerminalPoint(1, 4), match.End);
    }

    [Fact]
    public void ALongRunningExpressionOnABigLineIsStopped()
    {
        var screen = new VtScreen(200, 3).Feed(new string('a', 150) + "!");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Find(screen, "(a+)+$", new SearchOptions(Regex: true));
        Assert.True(watch.ElapsedMilliseconds < 10_000);
    }

    [Fact]
    public void SearchCanBeCancelled()
    {
        var screen = new VtScreen().Feed("a");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => screen.Emulator.SnapshotForSearch().Find("a", default, cancelled.Token));
    }

    [Fact]
    public void WideCharactersAreFoundAtTheirCells()
    {
        var screen = new VtScreen().Feed("ab漢字cd");
        var match = Assert.Single(Find(screen, "字"));
        Assert.Equal(new TerminalPoint(0, 4), match.Start);
        Assert.Equal(new TerminalPoint(0, 6), match.End);
    }

    // ── The shell's markers ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ACommandIsRecordedFromPromptToExit()
    {
        var screen = new VtScreen(30, 6);
        var started = new List<CommandRecord>();
        var finished = new List<CommandRecord>();
        screen.Emulator.CommandStarted += started.Add;
        screen.Emulator.CommandFinished += finished.Add;

        screen.Feed("\e]133;A\a$ \e]133;B\als -la\r\n\e]133;C\afile1\r\nfile2\r\n\e]133;D;0\a");
        var command = Assert.Single(screen.Emulator.Commands());
        Assert.Equal(0, command.Line);
        Assert.Equal("ls -la", command.Record.CommandLine);
        Assert.Equal(0, command.Record.ExitCode);
        Assert.True(command.Record.Finished);
        Assert.Single(started);
        Assert.Single(finished);
    }

    [Fact]
    public void TheExitCodeOfAFailedCommandIsKept()
    {
        var screen = new VtScreen(30, 6).Feed("\e]133;A\a$ \e]133;B\afalse\r\n\e]133;C\a\e]133;D;1\a");
        Assert.Equal(1, Assert.Single(screen.Emulator.Commands()).Record.ExitCode);
    }

    [Fact]
    public void ACommandStillRunningHasNoExitCodeYet()
    {
        var screen = new VtScreen(30, 6).Feed("\e]133;A\a$ \e]133;B\asleep 9\r\n\e]133;C\a");
        var record = Assert.Single(screen.Emulator.Commands()).Record;
        Assert.True(record.Running);
        Assert.Null(record.ExitCode);
    }

    [Fact]
    public void AnEmptyPromptLeavesNoMark()
    {
        var screen = new VtScreen(30, 6).Feed("\e]133;A\a$ \e]133;B\a\r\n\e]133;A\a$ \e]133;B\a");
        Assert.Single(screen.Emulator.Commands());
    }

    [Fact]
    public void SeveralCommandsAreListedInOrderWithTheirLines()
    {
        var screen = new VtScreen(30, 4);
        for (var i = 1; i <= 5; i++) screen.Feed($"\e]133;A\a$ \e]133;B\acmd{i}\r\n\e]133;C\aout{i}\r\n\e]133;D;{i % 2}\a");
        var commands = screen.Emulator.Commands();
        Assert.Equal(5, commands.Count);
        Assert.Equal(["cmd1", "cmd2", "cmd3", "cmd4", "cmd5"], commands.Select(c => c.Record.CommandLine));
        Assert.Equal([0L, 2, 4, 6, 8], commands.Select(c => c.Line));
        Assert.Equal([1, 0, 1, 0, 1], commands.Select(c => c.Record.ExitCode!.Value));
    }

    [Fact]
    public void VsCodesMarkersAndTheExplicitCommandLineWork()
    {
        var screen = new VtScreen(30, 6).Feed("\e]633;A\a$ \e]633;B\a\e]633;E;echo hi\\x3b there\a\r\n\e]633;C\ahi\r\n\e]633;D;2\a\e]633;P;Cwd=/work\a");
        var record = Assert.Single(screen.Emulator.Commands()).Record;
        Assert.Equal("echo hi; there", record.CommandLine);
        Assert.Equal(2, record.ExitCode);
        Assert.Equal("/work", screen.Emulator.WorkingDirectory);
    }

    [Fact]
    public void ACommandSurvivesScrollingIntoTheScrollback()
    {
        var screen = new VtScreen(30, 3).Feed("\e]133;A\a$ \e]133;B\amake\r\n\e]133;C\a1\r\n2\r\n3\r\n4\r\n\e]133;D;0\a");
        var command = Assert.Single(screen.Emulator.Commands());
        Assert.Equal(screen.Emulator.BaseLine, command.Line);
        Assert.Equal(0, command.Record.ExitCode);
    }

    [Fact]
    public void ACommandSurvivesAReflow()
    {
        var screen = new VtScreen(30, 5).Feed("\e]133;A\a$ \e]133;B\amake\r\n\e]133;C\aout\r\n");
        screen.Emulator.Resize(10, 5);
        Assert.Single(screen.Emulator.Commands());
        screen.Feed("\e]133;D;3\a");
        Assert.Equal(3, screen.Emulator.Commands()[0].Record.ExitCode);
    }

    [Fact]
    public void TheFrameCarriesTheMarkOnItsRow()
    {
        var screen = new VtScreen(30, 5).Feed("plain\r\n\e]133;A\a$ ");
        var frame = screen.Frame;
        Assert.Null(frame.Lines[0].Command);
        Assert.NotNull(frame.Lines[1].Command);
    }

    [Fact]
    public void MarkersInTheAlternateScreenAreIgnored()
    {
        var screen = new VtScreen(30, 5).Feed("\e[?1049h\e]133;A\a$ \e]133;B\a");
        screen.Feed("\e[?1049l");
        Assert.Empty(screen.Emulator.Commands());
    }

    [Fact]
    public void ADirectoryReportedWithThePromptIsKept()
    {
        var screen = new VtScreen(30, 5).Feed("\e]7;file:///srv/app\a\e]133;A\a$ ");
        Assert.Equal("/srv/app", Assert.Single(screen.Emulator.Commands()).Record.Directory);
    }

    [Fact]
    public void ContinuationPromptsDoNotStartNewCommands()
    {
        var screen = new VtScreen(30, 5).Feed("\e]133;A\a$ \e]133;B\aecho \\\r\n\e]133;A;k=s\a> more\r\n");
        Assert.Single(screen.Emulator.Commands());
    }
}
