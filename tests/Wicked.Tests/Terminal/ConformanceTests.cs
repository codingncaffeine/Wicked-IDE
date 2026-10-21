namespace Wicked.Tests.Terminal;

/// <summary>Escape sequences in, the screen out: vttest-style cases on a 20 by 6 screen.</summary>
public sealed class ConformanceTests
{
    public static TheoryData<string, string, string> Screens => new()
    {
        { "plain text", "hello", "hello" },
        { "carriage return and line feed", "a\r\nb", "a\nb" },
        { "line feed alone keeps the column", "a\nb", "a\n b" },
        { "backspace", "abc\bX", "abX" },
        { "backspace at the margin stays put", "\bx", "x" },
        { "cursor position", "\e[3;5Hx", "\n\n    x" },
        { "cursor position defaults to home", "abc\e[Hx", "xbc" },
        { "cursor up", "\e[3;3H\e[2A*", "  *" },
        { "cursor up stops at the top", "\e[2;1H\e[9A*", "*" },
        { "cursor down", "\e[2B*", "\n\n*" },
        { "cursor forward", "\e[5C*", "     *" },
        { "cursor back", "abcde\e[3D*", "ab*de" },
        { "next line", "ab\e[2E*", "ab\n\n*" },
        { "previous line", "\e[4;5H\e[2F*", "\n*" },
        { "column absolute", "abc\e[2G*", "a*c" },
        { "row absolute", "a\e[3dX", "a\n\n X" },
        { "erase to end of line", "abcdef\e[3G\e[K", "ab" },
        { "erase to start of line", "abcdef\e[3G\e[1K", "   def" },
        { "erase whole line", "abcdef\e[2K", "" },
        { "erase below", "1\r\n2\r\n3\e[2;1H\e[J", "1" },
        { "erase above", "1\r\n2\r\n3\e[2;1H\e[1J", "\n\n3" },
        { "erase all", "1\r\n2\e[2J", "" },
        { "insert characters", "abcdef\e[3G\e[2@", "ab  cdef" },
        { "delete characters", "abcdef\e[3G\e[2P", "abef" },
        { "erase characters", "abcdef\e[3G\e[2X", "ab  ef" },
        { "insert lines", "1\r\n2\r\n3\e[2;1H\e[L", "1\n\n2\n3" },
        { "delete lines", "1\r\n2\r\n3\e[2;1H\e[M", "1\n3" },
        { "scroll region confines the scroll", "a\r\nb\r\nc\r\nd\r\ne\e[2;4r\e[4;1H\n", "a\nc\nd\n\ne" },
        { "scroll up", "1\r\n2\r\n3\e[S", "2\n3" },
        { "scroll down", "1\r\n2\e[T", "\n1\n2" },
        { "reverse index scrolls at the top", "1\r\n2\e[H\eM", "\n1\n2" },
        { "index scrolls at the bottom", "\e[6;1Hx\eD", "\n\n\n\nx" },
        { "next line control", "ab\eEc", "ab\nc" },
        { "autowrap", new string('x', 25), new string('x', 20) + "\nxxxxx" },
        { "autowrap waits for the next character", new string('x', 20) + "\r\ny", new string('x', 20) + "\ny" },
        { "autowrap off overwrites the last column", "\e[?7l" + new string('a', 20) + "Z", new string('a', 19) + "Z" },
        { "tab stops every eight", "a\tb\tc", "a       b       c" },
        { "set and clear a tab stop", "\e[3g\e[1;4H\eH\e[1;1H\tx", "   x" },
        { "backtab", "\e[1;12H\e[Zx", "        x" },
        { "repeat the last character", "a\e[3b", "aaaa" },
        { "origin mode counts from the region", "\e[3;5r\e[?6h\e[1;1Hx", "\n\nx" },
        { "cursor stays inside the region in origin mode", "\e[3;5r\e[?6h\e[9;1Hx", "\n\n\n\nx" },
        { "save and restore the cursor", "ab\e7\e[3;3Hcd\e8X", "abX\n\n  cd" },
        { "csi save and restore", "ab\e[s\e[3;3Hcd\e[uX", "abX\n\n  cd" },
        { "insert mode pushes text right", "abc\e[1G\e[4hX", "Xabc" },
        { "alignment pattern", "\e#8", string.Join('\n', Enumerable.Repeat(new string('E', 20), 6)) },
        { "line drawing set", "\e(0lqk\e(B", "┌─┐" },
        { "shift out selects the second set", "\e)0\x0elqk\x0f", "┌─┐" },
        { "utf-8 text", "héllo 世", "héllo 世" },
        { "an unknown final byte is ignored", "a\e[99zb", "ab" },
        { "an unknown private mode is ignored", "a\e[?9999hb", "ab" },
        { "very many parameters are tolerated", "\e[" + string.Join(';', Enumerable.Range(0, 60)) + "mx", "x" },
        { "cancel aborts a sequence", "\e[3\x18X", "X" },
        { "substitute aborts a sequence", "\e[3\x1aX", "X" },
        { "escape inside a sequence starts a new one", "\e[3\e[2;2Hx", "\n x" },
        { "control characters inside a sequence still act", "\e[2\nCx", "\n  x" },
        { "an interrupted osc is dropped", "\e]0;title\e[2;1Hx", "\nx" },
        { "device control strings are skipped", "a\eP1;2|data\e\\b", "ab" },
        { "application program commands are skipped", "a\e_Gdata\e\\b", "ab" },
        { "full reset", "abc\ec", "" },
        { "soft reset keeps the screen", "abc\e[!p", "abc" },
        { "tab does not pass the last column", "\e[1;19H\t\tx", new string(' ', 19) + "x" },
        { "delete characters at the end", "abc\e[1G\e[9P", "" },
        { "insert lines outside the region do nothing", "a\e[3;5r\e[1;1H\e[L", "a" },
        { "linefeed at the region bottom scrolls only the region", "1\r\n2\r\n3\r\n4\e[1;3r\e[3;1H\n", "2\n3\n\n4" },
    };

    [Theory]
    [MemberData(nameof(Screens))]
    public void TheScreenShowsWhatTheSequenceDoes(string name, string input, string expected)
    {
        _ = name;
        var screen = new VtScreen().Feed(input);
        Assert.Equal(expected, screen.Screen);
    }

    [Fact]
    public void TheCursorFollowsText()
    {
        var screen = new VtScreen().Feed("hello");
        Assert.Equal((0, 5), screen.Cursor);
        screen.Feed("\r\n");
        Assert.Equal((1, 0), screen.Cursor);
    }

    [Fact]
    public void ThePendingWrapKeepsTheCursorOnTheLastColumn()
    {
        var screen = new VtScreen().Feed(new string('x', 20));
        Assert.Equal((0, 19), screen.Cursor);
        Assert.True(screen.Emulator.CursorPendingWrap);
        screen.Feed("y");
        Assert.Equal((1, 1), screen.Cursor);
    }

    [Fact]
    public void AWrappedRowIsMarkedAsContinuing()
    {
        var screen = new VtScreen().Feed(new string('x', 25) + "\r\nnext");
        var frame = screen.Frame;
        Assert.True(frame.Lines[0].Wrapped);
        Assert.False(frame.Lines[1].Wrapped);
    }

    [Fact]
    public void RunsOfTextAndSingleCharactersLandIdentically()
    {
        // The fast path for runs of ASCII and the general path must agree, wherever a chunk boundary falls.
        var text = "plain text with \e[31mcolour\e[0m and a tab\there and wrapping " + new string('z', 30) + "\r\nend";
        var whole = new VtScreen().Feed(text);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var piecemeal = new VtScreen();
        foreach (var b in bytes) piecemeal.Feed(b);
        Assert.Equal(whole.Screen, piecemeal.Screen);
        Assert.Equal(whole.Cursor, piecemeal.Cursor);
    }

    [Fact]
    public void ABrokenUtf8SequenceBecomesAReplacementCharacter()
    {
        var screen = new VtScreen().Feed(0x61, 0xFF, 0x62, 0xC3, 0x28, 0x63);
        Assert.Equal("a�b�(c", screen.Screen);
    }

    [Fact]
    public void AUtf8CharacterSplitAcrossWritesIsOneCharacter()
    {
        var screen = new VtScreen().Feed(0xE4, 0xB8).Feed(0x96, 0x21);
        Assert.Equal("世!", screen.Screen);
    }

    [Fact]
    public void OverlongAndSurrogateEncodingsAreRejected()
    {
        var screen = new VtScreen().Feed(0xC0, 0x80, 0xED, 0xA0, 0x80);
        Assert.DoesNotContain('\0', screen.Screen);
        Assert.Contains('�', screen.Screen);
    }

    [Fact]
    public void TheBellIsReportedAndNothingIsDrawn()
    {
        var screen = new VtScreen().Feed("a\ab");
        Assert.Equal(1, screen.Bells);
        Assert.Equal("ab", screen.Screen);
    }
}
