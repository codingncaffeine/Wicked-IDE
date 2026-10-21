using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class ModesAndReplyTests
{
    [Fact]
    public void DeviceAttributesAreAnswered()
    {
        var screen = new VtScreen().Feed("\e[c");
        Assert.Equal("\e[?62;22c", screen.Replies.ToString());
    }

    [Fact]
    public void SecondaryDeviceAttributesAreAnswered()
    {
        var screen = new VtScreen().Feed("\e[>c");
        Assert.StartsWith("\e[>", screen.Replies.ToString());
        Assert.EndsWith("c", screen.Replies.ToString());
    }

    [Fact]
    public void TheCursorPositionIsReported()
    {
        var screen = new VtScreen().Feed("\e[3;7H\e[6n");
        Assert.Equal("\e[3;7R", screen.Replies.ToString());
    }

    [Fact]
    public void TheCursorPositionInOriginModeIsRelativeToTheRegion()
    {
        var screen = new VtScreen().Feed("\e[3;5r\e[?6h\e[2;4H\e[6n");
        Assert.Equal("\e[2;4R", screen.Replies.ToString());
    }

    [Fact]
    public void StatusIsReportedOk()
    {
        Assert.Equal("\e[0n", new VtScreen().Feed("\e[5n").Replies.ToString());
    }

    [Fact]
    public void TheTextAreaSizeIsReported()
    {
        Assert.Equal("\e[8;6;20t", new VtScreen().Feed("\e[18t").Replies.ToString());
    }

    [Fact]
    public void TheVersionIsReported()
    {
        var reply = new VtScreen().Feed("\e[>q").Replies.ToString();
        Assert.StartsWith("\eP>|Wicked", reply);
        Assert.EndsWith("\e\\", reply);
    }

    [Theory]
    [InlineData("\e[?25$p", "\e[?25;1$y")]
    [InlineData("\e[?25l\e[?25$p", "\e[?25;2$y")]
    [InlineData("\e[?2004$p", "\e[?2004;2$y")]
    [InlineData("\e[?2004h\e[?2004$p", "\e[?2004;1$y")]
    [InlineData("\e[?7$p", "\e[?7;1$y")]
    [InlineData("\e[?9999$p", "\e[?9999;0$y")]
    [InlineData("\e[4$p", "\e[4;2$y")]
    public void ModesCanBeAskedAbout(string input, string reply) =>
        Assert.Equal(reply, new VtScreen().Feed(input).Replies.ToString());

    [Fact]
    public void BracketedPasteIsAMode()
    {
        var screen = new VtScreen();
        Assert.False(screen.Emulator.Modes.BracketedPaste);
        screen.Feed("\e[?2004h");
        Assert.True(screen.Emulator.Modes.BracketedPaste);
        screen.Feed("\e[?2004l");
        Assert.False(screen.Emulator.Modes.BracketedPaste);
    }

    [Theory]
    [InlineData("\e[?1000h", MouseTracking.Buttons)]
    [InlineData("\e[?1002h", MouseTracking.Drag)]
    [InlineData("\e[?1003h", MouseTracking.Any)]
    [InlineData("\e[?1003h\e[?1003l", MouseTracking.None)]
    public void MouseTrackingFollowsTheMode(string input, MouseTracking expected) =>
        Assert.Equal(expected, new VtScreen().Feed(input).Emulator.Modes.Mouse);

    [Theory]
    [InlineData("\e[?1006h", MouseEncoding.Sgr)]
    [InlineData("\e[?1005h", MouseEncoding.Utf8)]
    [InlineData("\e[?1015h", MouseEncoding.Urxvt)]
    [InlineData("\e[?1006h\e[?1006l", MouseEncoding.X10)]
    public void MouseEncodingFollowsTheMode(string input, MouseEncoding expected) =>
        Assert.Equal(expected, new VtScreen().Feed(input).Emulator.Modes.MouseEncoding);

    [Fact]
    public void ApplicationCursorKeysAndKeypad()
    {
        var screen = new VtScreen().Feed("\e[?1h\e=");
        Assert.True(screen.Emulator.Modes.ApplicationCursor);
        Assert.True(screen.Emulator.Modes.ApplicationKeypad);
        screen.Feed("\e[?1l\e>");
        Assert.False(screen.Emulator.Modes.ApplicationCursor);
        Assert.False(screen.Emulator.Modes.ApplicationKeypad);
    }

    [Fact]
    public void CursorVisibilityAndStyle()
    {
        var screen = new VtScreen().Feed("\e[?25l");
        Assert.False(screen.Emulator.Modes.CursorVisible);
        Assert.Equal(-1, screen.Frame.CursorRow);
        screen.Feed("\e[?25h\e[5 q");
        Assert.Equal(CursorShape.Bar, screen.Emulator.Modes.CursorShape);
        Assert.True(screen.Emulator.Modes.CursorBlink);
        screen.Feed("\e[2 q");
        Assert.Equal(CursorShape.Block, screen.Emulator.Modes.CursorShape);
        Assert.False(screen.Emulator.Modes.CursorBlink);
        screen.Feed("\e[0 q");
        Assert.Null(screen.Emulator.Modes.CursorShape);
    }

    [Fact]
    public void FocusReportingIsAMode()
    {
        Assert.True(new VtScreen().Feed("\e[?1004h").Emulator.Modes.FocusReporting);
    }

    [Fact]
    public void ModeChangesAreAnnounced()
    {
        var screen = new VtScreen();
        var seen = new List<TerminalModes>();
        screen.Emulator.ModesChanged += seen.Add;
        screen.Feed("\e[?2004h\e[?2004h\e[?1h");
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void TheAlternateScreenKeepsTheMainScreenSafe()
    {
        var screen = new VtScreen().Feed("main text\e[?1049h");
        Assert.True(screen.Emulator.Modes.AlternateScreen);
        Assert.Equal("", screen.Screen);
        screen.Feed("\e[2;1Halt text");
        Assert.Equal("\nalt text", screen.Screen);
        screen.Feed("\e[?1049l");
        Assert.False(screen.Emulator.Modes.AlternateScreen);
        Assert.Equal("main text", screen.Screen);
        Assert.Equal((0, 9), screen.Cursor);
    }

    [Fact]
    public void TheAlternateScreenStartsBlankEachTime()
    {
        var screen = new VtScreen().Feed("\e[?1049hold\e[?1049l\e[?1049h");
        Assert.Equal("", screen.Screen);
    }

    [Fact]
    public void TheAlternateScreenHasNoScrollback()
    {
        var screen = new VtScreen(10, 3).Feed("\e[?1049h1\r\n2\r\n3\r\n4\r\n5");
        Assert.Equal(0, screen.Emulator.ScrollbackCount);
        Assert.Equal("3\n4\n5", screen.Screen);
        screen.Feed("\e[?1049l");
        Assert.Equal(0, screen.Emulator.ScrollbackCount);
    }

    [Fact]
    public void TheOlderAlternateScreenModesWork()
    {
        var screen = new VtScreen().Feed("keep\e[?47h\e[Halt\e[?47l");
        Assert.Equal("keep", screen.Screen);
    }

    [Fact]
    public void ReverseVideoIsAMode()
    {
        Assert.True(new VtScreen().Feed("\e[?5h").Emulator.Modes.ReverseVideo);
    }

    [Fact]
    public void SynchronizedOutputIsAMode()
    {
        var screen = new VtScreen().Feed("\e[?2026h");
        Assert.True(screen.Emulator.Modes.SynchronizedOutput);
    }

    [Fact]
    public void ALinefeedModeMakesLineFeedReturnTheCarriage()
    {
        Assert.Equal("a\nb", new VtScreen().Feed("\e[20ha\nb").Screen);
    }

    [Fact]
    public void AFullResetClearsTheModes()
    {
        var screen = new VtScreen().Feed("\e[?2004h\e[?1000h\e[?1h\ec");
        Assert.False(screen.Emulator.Modes.BracketedPaste);
        Assert.Equal(MouseTracking.None, screen.Emulator.Modes.Mouse);
        Assert.False(screen.Emulator.Modes.ApplicationCursor);
    }

    // ── Strings ──────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("\e]0;my title\a", "my title")]
    [InlineData("\e]2;my title\e\\", "my title")]
    [InlineData("\e]0;caf\u00E9 \u4E16\u754C\a", "caf\u00E9 \u4E16\u754C")]
    public void TheTitleIsSet(string input, string title)
    {
        var screen = new VtScreen().Feed(input);
        Assert.Equal([title], screen.Titles);
        Assert.Equal(title, screen.Emulator.Title);
        Assert.Equal("", screen.Screen);
    }

    [Fact]
    public void ControlCharactersAreRemovedFromTheTitle()
    {
        var screen = new VtScreen().Feed("\e]0;a\u0001b\u009Bc\a");
        Assert.Equal("abc", screen.Emulator.Title);
    }

    [Fact]
    public void ATitleSplitAcrossWritesIsOneTitle()
    {
        var screen = new VtScreen().Feed("\e]0;par").Feed("t one\a");
        Assert.Equal(["part one"], screen.Titles);
    }

    [Fact]
    public void TheWorkingDirectoryIsReported()
    {
        var screen = new VtScreen().Feed("\e]7;file://host/home/user/my%20dir\a");
        Assert.Equal(["/home/user/my dir"], screen.Directories);
        Assert.Equal("/home/user/my dir", screen.Emulator.WorkingDirectory);
    }

    [Fact]
    public void AWindowsDirectoryLosesItsLeadingSlash()
    {
        var screen = new VtScreen().Feed("\e]7;file:///C:/Users/me\a");
        Assert.Equal(["C:/Users/me"], screen.Directories);
    }

    [Fact]
    public void HyperlinksMarkTheirCells()
    {
        var screen = new VtScreen().Feed("a\e]8;;https://example.com/x\ab\e]8;;\ac");
        Assert.Equal(0, screen.Cell(0, 0).Link);
        var id = screen.Cell(0, 1).Link;
        Assert.NotEqual(0, id);
        Assert.Equal("https://example.com/x", screen.Frame.Links[id]);
        Assert.Equal(0, screen.Cell(0, 2).Link);
    }

    [Fact]
    public void LinksToOneAddressShareAnIdAndOthersDoNot()
    {
        var screen = new VtScreen().Feed("\e]8;;u\aa\e]8;;\a \e]8;;u\ab\e]8;;\a \e]8;;v\ac\e]8;;\a");
        Assert.NotEqual(0, screen.Cell(0, 0).Link);
        Assert.Equal(screen.Cell(0, 0).Link, screen.Cell(0, 2).Link);
        Assert.NotEqual(screen.Cell(0, 0).Link, screen.Cell(0, 4).Link);
    }

    [Fact]
    public void ClipboardWritesAreIgnoredUnlessAllowed()
    {
        var text = Convert.ToBase64String("hello"u8.ToArray());
        Assert.Empty(new VtScreen().Feed($"\e]52;c;{text}\a").Clipboard);
        var allowed = new VtScreen(20, 6, new TerminalOptions { AllowClipboardWrite = true }).Feed($"\e]52;c;{text}\a");
        Assert.Equal(["hello"], allowed.Clipboard);
    }

    [Fact]
    public void ClipboardReadsAreNeverAnswered()
    {
        var screen = new VtScreen(20, 6, new TerminalOptions { AllowClipboardWrite = true }).Feed("\e]52;c;?\a");
        Assert.Equal("", screen.Replies.ToString());
        Assert.Empty(screen.Clipboard);
    }

    [Fact]
    public void BadClipboardDataIsIgnored()
    {
        var screen = new VtScreen(20, 6, new TerminalOptions { AllowClipboardWrite = true }).Feed("\e]52;c;***\a");
        Assert.Empty(screen.Clipboard);
    }

    [Fact]
    public void TheDefaultColoursAreReportedToProgramsThatAsk()
    {
        var screen = new VtScreen(20, 6, new TerminalOptions { Foreground = 0xFFAABBCC, Background = 0xFF102030 }).Feed("\e]10;?\a\e]11;?\a");
        Assert.Equal("\e]10;rgb:aaaa/bbbb/cccc\e\\\e]11;rgb:1010/2020/3030\e\\", screen.Replies.ToString());
    }

    [Fact]
    public void ThePaletteCanBeSetAndAsked()
    {
        var screen = new VtScreen().Feed("\e]4;1;rgb:ff/00/80\a\e]4;1;?\a");
        Assert.Equal("\e]4;1;rgb:ffff/0000/8080\e\\", screen.Replies.ToString());
        Assert.Equal(0xFFFF0080u, screen.Frame.PaletteOverrides![1]);
        screen.Feed("\e]104;1\a");
        Assert.Equal(0u, screen.Frame.PaletteOverrides![1]);
    }

    [Theory]
    [InlineData("rgb:ff/80/00", 0xFFFF8000u)]
    [InlineData("rgb:f/8/0", 0xFFFF8800u)]
    [InlineData("rgb:ffff/8080/0000", 0xFFFF8000u)]
    [InlineData("#ff8000", 0xFFFF8000u)]
    public void ColourSpecsAreRead(string spec, uint argb) => Assert.Equal(argb, TerminalEmulator.ParseColor(spec));

    [Theory]
    [InlineData("red")]
    [InlineData("rgb:zz/00/00")]
    [InlineData("rgb:ff/00")]
    [InlineData("#fff")]
    public void BadColourSpecsAreRefused(string spec) => Assert.Null(TerminalEmulator.ParseColor(spec));

    [Fact]
    public void ProgressIsReported()
    {
        var screen = new VtScreen();
        (int State, int Value)? seen = null;
        screen.Emulator.ProgressChanged += (s, v) => seen = (s, v);
        screen.Feed("\e]9;4;1;42\a");
        Assert.Equal((1, 42), seen);
    }

    [Fact]
    public void ADecrqssForTheCursorStyleIsAnswered()
    {
        var screen = new VtScreen().Feed("\e[6 q\eP$q q\e\\");
        Assert.Equal("\eP1$r6 q\e\\", screen.Replies.ToString());
    }

    [Fact]
    public void AnOversizedStringIsDroppedWithoutHarm()
    {
        var screen = new VtScreen().Feed("\e]0;" + new string('a', 2_000_000) + "\a" + "ok");
        Assert.Equal("ok", screen.Screen);
    }
}
