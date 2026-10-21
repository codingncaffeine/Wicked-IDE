using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class ScrollbackAndReflowTests
{
    private static string Numbered(int from, int to) => string.Join("\r\n", Enumerable.Range(from, to - from + 1).Select(i => $"line{i}"));

    [Fact]
    public void LinesThatScrollOffTheTopAreKept()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 6));
        Assert.Equal("line4\nline5\nline6", screen.Screen);
        Assert.Equal(["line1", "line2", "line3"], screen.Scrollback());
    }

    [Fact]
    public void TheScrollbackIsLimited()
    {
        var screen = new VtScreen(10, 3, new TerminalOptions { Scrollback = 5 }).Feed(Numbered(1, 20));
        Assert.Equal(5, screen.Emulator.ScrollbackCount);
        Assert.Equal(["line13", "line14", "line15", "line16", "line17"], screen.Scrollback());
        Assert.Equal(12, screen.Emulator.BaseLine);
    }

    [Fact]
    public void ANoScrollbackSettingKeepsNothing()
    {
        var screen = new VtScreen(10, 3, new TerminalOptions { Scrollback = 0 }).Feed(Numbered(1, 10));
        Assert.Equal(0, screen.Emulator.ScrollbackCount);
        Assert.Equal("line8\nline9\nline10", screen.Screen);
    }

    [Fact]
    public void AScrollRegionBelowTheTopKeepsNothing()
    {
        var screen = new VtScreen(10, 4).Feed("\e[2;4r\e[4;1H1\n2\n3\n");
        Assert.Equal(0, screen.Emulator.ScrollbackCount);
    }

    [Fact]
    public void ScrollbackLinesAreStoredWithoutTheirBlankTails()
    {
        var screen = new VtScreen(120, 2).Feed("abc\r\ndef\r\nghi");
        Assert.Equal(3, screen.Emulator.LineAt(screen.Emulator.BaseLine)!.Cells.Length);
    }

    [Fact]
    public void TenThousandLinesStaySmall()
    {
        var screen = new VtScreen(200, 24);
        for (var i = 0; i < 10_030; i++) screen.Feed($"build step {i}: compiling a source file with a fairly ordinary line length\r\n");
        Assert.Equal(10_000, screen.Emulator.ScrollbackCount);
        // A line of 200 columns holding about 70 characters is kept as those 70 cells, 16 bytes each, not as 200.
        long cells = 0;
        for (long i = 0; i < 10_000; i++) cells += screen.Emulator.LineAt(screen.Emulator.BaseLine + i)!.Cells.Length;
        Assert.True(cells < 10_000L * 80, $"{cells} cells for 10000 lines");
    }

    [Fact]
    public void ClearingTheScrollbackKeepsTheScreen()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 6) + "\e[3J");
        Assert.Equal(0, screen.Emulator.ScrollbackCount);
        Assert.Equal("line4\nline5\nline6", screen.Screen);
    }

    [Fact]
    public void ScrollingTheViewShowsOlderLines()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 8));
        screen.Emulator.ScrollViewport(2);
        var frame = screen.Frame;
        Assert.Equal(2, frame.ViewportOffset);
        Assert.Equal("line4\nline5\nline6", screen.Screen);
        Assert.Equal(-1, frame.CursorRow);
        screen.Emulator.ScrollViewportToBottom();
        Assert.Equal("line6\nline7\nline8", screen.Screen);
    }

    [Fact]
    public void TheViewIsClampedToTheScrollback()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 5));
        screen.Emulator.ScrollViewport(100);
        Assert.Equal(2, screen.Emulator.ViewportOffset);
        screen.Emulator.ScrollViewport(-100);
        Assert.Equal(0, screen.Emulator.ViewportOffset);
    }

    [Fact]
    public void ANewLineKeepsAScrolledBackViewWhereItIs()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 8));
        screen.Emulator.ScrollViewport(2);
        var before = screen.Screen;
        screen.Feed("\r\nmore");
        Assert.Equal(before, screen.Screen);
    }

    [Fact]
    public void AFrameDoesNotChangeWhenTheTerminalDoes()
    {
        var screen = new VtScreen(10, 3).Feed("first");
        var frame = screen.Frame;
        screen.Feed("\e[2J\e[Hsecond\r\n\r\n\r\n\r\nmore");
        Assert.Equal("first", screen.Emulator.RowText(frame.Lines[0].Cells, 0, 10).TrimEnd());
    }

    [Fact]
    public void AbsoluteLineNumbersAreStable()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 5));
        var frame = screen.Frame;
        Assert.Equal(2, frame.TopLine);
        screen.Feed("\r\nline6");
        Assert.Equal("line3", screen.Emulator.RowText(screen.Emulator.LineAt(2)!.Cells, 0, 10).TrimEnd());
        Assert.Equal(3, screen.Frame.TopLine);
    }

    // ── Reflow ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NarrowingWrapsALongLine()
    {
        var screen = new VtScreen(20, 5).Feed("0123456789abcdefghij");
        screen.Emulator.Resize(10, 5);
        Assert.Equal("0123456789\nabcdefghij", screen.Screen);
        Assert.True(screen.Frame.Lines[0].Wrapped);
    }

    [Fact]
    public void WideningJoinsWrappedRowsBack()
    {
        var screen = new VtScreen(10, 5).Feed("0123456789abcdefghij");
        screen.Emulator.Resize(20, 5);
        Assert.Equal("0123456789abcdefghij", screen.Screen);
    }

    [Fact]
    public void ALineTheProgramEndedIsNotJoined()
    {
        var screen = new VtScreen(10, 5).Feed("one\r\ntwo\r\nthree");
        screen.Emulator.Resize(30, 5);
        Assert.Equal("one\ntwo\nthree", screen.Screen);
    }

    [Fact]
    public void TheCursorFollowsItsTextThroughAReflow()
    {
        var screen = new VtScreen(20, 5).Feed("$ 0123456789abcdef");
        screen.Emulator.Resize(10, 5);
        Assert.Equal((1, 8), screen.Cursor);
        screen.Feed("XY");
        Assert.Equal("$ 01234567\n89abcdefXY", screen.Screen);
        screen.Emulator.Resize(30, 5);
        Assert.Equal("$ 0123456789abcdefXY", screen.Screen);
        Assert.Equal((0, 20), screen.Cursor);
    }

    [Fact]
    public void ACursorAfterTheTextStaysAfterIt()
    {
        var screen = new VtScreen(20, 5).Feed("$ ");
        screen.Emulator.Resize(10, 5);
        Assert.Equal((0, 2), screen.Cursor);
        screen.Emulator.Resize(6, 5);
        Assert.Equal((0, 2), screen.Cursor);
    }

    [Fact]
    public void ACursorWaitingToWrapStillWaits()
    {
        var screen = new VtScreen(10, 5).Feed("0123456789");
        Assert.True(screen.Emulator.CursorPendingWrap);
        screen.Emulator.Resize(5, 5);
        Assert.Equal((1, 4), screen.Cursor);
        Assert.True(screen.Emulator.CursorPendingWrap);
        screen.Feed("X");
        Assert.Equal("01234\n56789\nX", screen.Screen);
    }

    [Fact]
    public void ShorteningPushesTheTopIntoTheScrollback()
    {
        var screen = new VtScreen(10, 5).Feed(Numbered(1, 5));
        screen.Emulator.Resize(10, 3);
        Assert.Equal("line3\nline4\nline5", screen.Screen);
        Assert.Equal(["line1", "line2"], screen.Scrollback());
        Assert.Equal((2, 5), screen.Cursor);
    }

    [Fact]
    public void LengtheningPullsTheScrollbackBack()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 6));
        screen.Emulator.Resize(10, 5);
        Assert.Equal("line2\nline3\nline4\nline5\nline6", screen.Screen);
        Assert.Equal(["line1"], screen.Scrollback());
        Assert.Equal((4, 5), screen.Cursor);
    }

    [Fact]
    public void BlankRowsBelowTheCursorAreNotPushedUp()
    {
        var screen = new VtScreen(10, 6).Feed("one\r\ntwo");
        screen.Emulator.Resize(10, 3);
        Assert.Equal("one\ntwo", screen.Screen);
        Assert.Equal(0, screen.Emulator.ScrollbackCount);
        screen.Emulator.Resize(10, 8);
        Assert.Equal("one\ntwo", screen.Screen);
        Assert.Equal((1, 3), screen.Cursor);
    }

    [Fact]
    public void TheScrollbackIsReflowedToo()
    {
        var screen = new VtScreen(20, 2).Feed("0123456789abcdefghij\r\nsecond\r\nthird");
        Assert.Equal(["0123456789abcdefghij"], screen.Scrollback());
        screen.Emulator.Resize(10, 2);
        Assert.Equal(["0123456789", "abcdefghij"], screen.Scrollback());
        Assert.Equal("second\nthird", screen.Screen);
    }

    [Fact]
    public void AWideCharacterIsNotSplitByTheNewEdge()
    {
        var screen = new VtScreen(10, 4).Feed("abcd漢字");
        screen.Emulator.Resize(5, 4);
        Assert.Equal("abcd\n漢字", screen.Screen);
        foreach (var row in Enumerable.Range(0, 2))
        {
            for (var c = 0; c < 5; c++)
            {
                var cell = screen.Cell(row, c);
                if ((cell.Flags & CellFlags.Wide) != 0) Assert.Equal(CellFlags.WideTail, screen.Cell(row, c + 1).Flags & CellFlags.WideTail);
            }
        }
    }

    [Fact]
    public void StylesSurviveAReflow()
    {
        var screen = new VtScreen(20, 3).Feed("\e[31mred\e[0m plain");
        screen.Emulator.Resize(5, 3);
        Assert.Equal(1, CellColor.PaletteIndex(screen.Cell(0, 0).Fg));
        Assert.Equal(0u, screen.Cell(1, 1).Fg);
    }

    [Fact]
    public void TheAlternateScreenIsCroppedNotReflowed()
    {
        var screen = new VtScreen(10, 3).Feed("\e[?1049h0123456789abc");
        screen.Emulator.Resize(5, 3);
        Assert.Equal("56789", screen.Screen.Split('\n')[0] == "01234" ? "56789" : "x");
        screen.Feed("\e[?1049l");
        Assert.Equal("", screen.Screen);
    }

    [Fact]
    public void TheMainScreenIsReflowedWhileTheAlternateIsShowing()
    {
        var screen = new VtScreen(20, 4).Feed("$ 0123456789abcdef\e[?1049hfull screen app");
        screen.Emulator.Resize(10, 4);
        screen.Feed("\e[?1049l");
        Assert.Equal("$ 01234567\n89abcdef", screen.Screen);
        Assert.Equal((1, 8), screen.Cursor);
    }

    [Fact]
    public void ResizingToTheSameSizeDoesNothing()
    {
        var screen = new VtScreen(10, 3).Feed(Numbered(1, 5));
        var epoch = screen.Emulator.Epoch;
        screen.Emulator.Resize(10, 3);
        Assert.Equal(epoch, screen.Emulator.Epoch);
    }

    [Fact]
    public void AReflowOfTenThousandLinesIsQuick()
    {
        var screen = new VtScreen(120, 30);
        for (var i = 0; i < 10_000; i++) screen.Feed($"line {i} " + new string('x', i % 150) + "\r\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        screen.Emulator.Resize(80, 30);
        screen.Emulator.Resize(140, 30);
        Assert.True(watch.ElapsedMilliseconds < 6000, $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void TypingAfterAShrinkAndGrowKeepsTheLine()
    {
        var screen = new VtScreen(30, 4).Feed("prompt> ls -la --color");
        screen.Emulator.Resize(8, 4);
        screen.Emulator.Resize(30, 4);
        Assert.Equal("prompt> ls -la --color", screen.Screen);
        Assert.Equal((0, 22), screen.Cursor);
    }

    [Fact]
    public void OneRowAndOneColumnAreAllowed()
    {
        var screen = new VtScreen(1, 1).Feed("ab\r\ncd");
        Assert.Equal("d", screen.Screen);
        screen.Emulator.Resize(5, 2);
        screen.Emulator.Resize(1, 1);
        Assert.NotNull(screen.Frame);
    }

    [Fact]
    public void ResizingBumpsTheEpochSoOldPositionsAreKnownToBeStale()
    {
        var screen = new VtScreen(10, 3).Feed("x");
        var epoch = screen.Frame.Epoch;
        screen.Emulator.Resize(12, 3);
        Assert.NotEqual(epoch, screen.Frame.Epoch);
    }
}
