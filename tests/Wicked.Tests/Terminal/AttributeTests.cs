using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class AttributeTests
{
    [Theory]
    [InlineData("\e[31mX", 1)]
    [InlineData("\e[37mX", 7)]
    [InlineData("\e[90mX", 8)]
    [InlineData("\e[97mX", 15)]
    [InlineData("\e[38;5;200mX", 200)]
    [InlineData("\e[38:5:17mX", 17)]
    public void APaletteForegroundIsRemembered(string input, int index)
    {
        var cell = new VtScreen().Feed(input).Cell(0, 0);
        Assert.True(CellColor.IsPalette(cell.Fg));
        Assert.Equal(index, CellColor.PaletteIndex(cell.Fg));
    }

    [Theory]
    [InlineData("\e[44mX", 4)]
    [InlineData("\e[102mX", 10)]
    [InlineData("\e[48;5;99mX", 99)]
    public void APaletteBackgroundIsRemembered(string input, int index)
    {
        var cell = new VtScreen().Feed(input).Cell(0, 0);
        Assert.Equal(index, CellColor.PaletteIndex(cell.Bg));
    }

    [Theory]
    [InlineData("\e[38;2;10;20;30mX")]
    [InlineData("\e[38:2:10:20:30mX")]
    [InlineData("\e[38:2::10:20:30mX")]
    public void TrueColourInEveryForm(string input)
    {
        var cell = new VtScreen().Feed(input).Cell(0, 0);
        Assert.True(CellColor.IsRgb(cell.Fg));
        Assert.Equal(0xFF0A141Eu, CellColor.ToArgb(cell.Fg));
    }

    [Fact]
    public void TrueColourBackgroundAndTheColoursAfterIt()
    {
        var cell = new VtScreen().Feed("\e[48;2;1;2;3;31mX").Cell(0, 0);
        Assert.Equal(0xFF010203u, CellColor.ToArgb(cell.Bg));
        Assert.Equal(1, CellColor.PaletteIndex(cell.Fg));
    }

    [Theory]
    [InlineData("\e[1mX", CellFlags.Bold)]
    [InlineData("\e[2mX", CellFlags.Dim)]
    [InlineData("\e[3mX", CellFlags.Italic)]
    [InlineData("\e[4mX", CellFlags.Underline)]
    [InlineData("\e[4:3mX", CellFlags.CurlyUnderline)]
    [InlineData("\e[4:4mX", CellFlags.DottedUnderline)]
    [InlineData("\e[4:5mX", CellFlags.DashedUnderline)]
    [InlineData("\e[4:2mX", CellFlags.DoubleUnderline)]
    [InlineData("\e[21mX", CellFlags.DoubleUnderline)]
    [InlineData("\e[5mX", CellFlags.Blink)]
    [InlineData("\e[7mX", CellFlags.Inverse)]
    [InlineData("\e[8mX", CellFlags.Invisible)]
    [InlineData("\e[9mX", CellFlags.Strike)]
    public void AStyleIsSet(string input, CellFlags expected) =>
        Assert.Equal(expected, new VtScreen().Feed(input).Cell(0, 0).Flags & CellFlags.Pen);

    [Theory]
    [InlineData("\e[1;2m\e[22mX")]
    [InlineData("\e[3m\e[23mX")]
    [InlineData("\e[4m\e[24mX")]
    [InlineData("\e[4:3m\e[24mX")]
    [InlineData("\e[5m\e[25mX")]
    [InlineData("\e[7m\e[27mX")]
    [InlineData("\e[8m\e[28mX")]
    [InlineData("\e[9m\e[29mX")]
    [InlineData("\e[1;3;4;7;9m\e[0mX")]
    [InlineData("\e[1;3;4;7;9m\e[mX")]
    [InlineData("\e[4:0mX")]
    public void AStyleIsCleared(string input) =>
        Assert.Equal(CellFlags.None, new VtScreen().Feed(input).Cell(0, 0).Flags & CellFlags.Pen);

    [Fact]
    public void ColoursResetToTheDefault()
    {
        var cell = new VtScreen().Feed("\e[31;42m\e[39;49mX").Cell(0, 0);
        Assert.Equal(0u, cell.Fg);
        Assert.Equal(0u, cell.Bg);
    }

    [Fact]
    public void AnUnderlineColourIsReadAndIgnored()
    {
        var cell = new VtScreen().Feed("\e[58;2;1;2;3;1mX\e[58:2::9:9:9;4mY").Cell(0, 0);
        Assert.Equal(CellFlags.Bold, cell.Flags & CellFlags.Pen);
        Assert.Equal(0u, cell.Fg);
        Assert.Equal(CellFlags.Underline, new VtScreen().Feed("\e[58:2::9:9:9;4mY").Cell(0, 0).Flags & CellFlags.Pen);
    }

    [Fact]
    public void ErasedCellsTakeTheCurrentBackground()
    {
        var screen = new VtScreen().Feed("\e[44m\e[2K");
        Assert.Equal(4, CellColor.PaletteIndex(screen.Cell(0, 10).Bg));
        Assert.Equal(4, CellColor.PaletteIndex(screen.Cell(0, 19).Bg));
    }

    [Fact]
    public void ScrollingBringsInLinesInTheCurrentBackground()
    {
        var screen = new VtScreen(10, 3).Feed("\e[41m\e[3;1H\n");
        Assert.Equal(1, CellColor.PaletteIndex(screen.Cell(2, 5).Bg));
    }

    [Fact]
    public void SavedCursorKeepsTheStyle()
    {
        var screen = new VtScreen().Feed("\e[1;31m\e7\e[0m\e8X");
        var cell = screen.Cell(0, 0);
        Assert.Equal(CellFlags.Bold, cell.Flags & CellFlags.Pen);
        Assert.Equal(1, CellColor.PaletteIndex(cell.Fg));
    }

    [Fact]
    public void TheStyleOfTextDoesNotLeakIntoWhatFollowsAReset()
    {
        var screen = new VtScreen().Feed("\e[1mA\e[0mB");
        Assert.Equal(CellFlags.Bold, screen.Cell(0, 0).Flags & CellFlags.Pen);
        Assert.Equal(CellFlags.None, screen.Cell(0, 1).Flags & CellFlags.Pen);
    }

    [Fact]
    public void DecrqssReportsTheStyle()
    {
        var screen = new VtScreen().Feed("\e[1;4;38;5;200;48;2;1;2;3m\eP$qm\e\\");
        Assert.Equal("\eP1$r0;1;4;38;5;200;48;2;1;2;3m\e\\", screen.Replies.ToString());
    }
}
