using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class UnicodeTests
{
    [Fact]
    public void AWideCharacterTakesTwoCells()
    {
        var screen = new VtScreen().Feed("漢字a");
        Assert.Equal("漢字a", screen.Screen);
        Assert.Equal((0, 5), screen.Cursor);
        Assert.Equal(CellFlags.Wide, screen.Cell(0, 0).Flags & (CellFlags.Wide | CellFlags.WideTail));
        Assert.Equal(CellFlags.WideTail, screen.Cell(0, 1).Flags & (CellFlags.Wide | CellFlags.WideTail));
        Assert.Equal('a', screen.Cell(0, 4).Rune);
    }

    [Fact]
    public void AWideCharacterThatDoesNotFitMovesToTheNextRow()
    {
        var screen = new VtScreen().Feed(new string('x', 19) + "漢");
        Assert.Equal(new string('x', 19) + "\n漢", screen.Screen);
        Assert.True(screen.Frame.Lines[0].Wrapped);
    }

    [Fact]
    public void OverwritingHalfOfAWideCharacterBlanksTheOtherHalf()
    {
        var screen = new VtScreen().Feed("漢字\e[1;2HX");
        Assert.Equal(" X字", screen.Screen);
        Assert.Equal(0, screen.Cell(0, 0).Rune);
    }

    [Fact]
    public void ErasingHalfOfAWideCharacterBlanksTheOtherHalf()
    {
        var screen = new VtScreen().Feed("漢字\e[1;4H\e[K");
        Assert.Equal("漢", screen.Screen);
    }

    [Fact]
    public void DeletingCharactersNeverLeavesAHalf()
    {
        var screen = new VtScreen().Feed("a漢b\e[1;1H\e[P");
        Assert.DoesNotContain('\0', screen.Screen);
        foreach (var column in Enumerable.Range(0, 5))
        {
            var cell = screen.Cell(0, column);
            if ((cell.Flags & CellFlags.WideTail) != 0) Assert.NotEqual(CellFlags.None, screen.Cell(0, column - 1).Flags & CellFlags.Wide);
        }
    }

    [Fact]
    public void ACombiningMarkJoinsTheCharacterBefore()
    {
        var screen = new VtScreen().Feed("éx");
        Assert.Equal("éx", screen.Screen);
        Assert.Equal((0, 2), screen.Cursor);
        Assert.NotEqual(CellFlags.None, screen.Cell(0, 0).Flags & CellFlags.Cluster);
    }

    [Fact]
    public void SeveralMarksStackOnOneCell()
    {
        var screen = new VtScreen().Feed("à́̂");
        Assert.Equal("à́̂", screen.Screen);
        Assert.Equal((0, 1), screen.Cursor);
    }

    [Fact]
    public void AZeroWidthJoinerSequenceIsOneWideCell()
    {
        var family = "\U0001F468‍\U0001F469‍\U0001F467";
        var screen = new VtScreen().Feed(family + "x");
        Assert.Equal(family + "x", screen.Screen);
        Assert.Equal((0, 3), screen.Cursor);
    }

    [Fact]
    public void ASkinToneModifierJoinsItsEmoji()
    {
        var screen = new VtScreen().Feed("\U0001F44D\U0001F3FDx");
        Assert.Equal((0, 3), screen.Cursor);
    }

    [Fact]
    public void AFlagIsOneWideCell()
    {
        var flag = "\U0001F1E9\U0001F1EA";
        var screen = new VtScreen().Feed(flag + flag);
        Assert.Equal(flag + flag, screen.Screen);
        Assert.Equal((0, 4), screen.Cursor);
    }

    [Fact]
    public void TheEmojiSelectorWidensASymbol()
    {
        var screen = new VtScreen().Feed("❤️x");
        Assert.Equal((0, 3), screen.Cursor);
        Assert.Equal(CellFlags.Wide, screen.Cell(0, 0).Flags & CellFlags.Wide);
    }

    [Theory]
    [InlineData(0x41, 1)]
    [InlineData(0xE9, 1)]
    [InlineData(0x4E16, 2)]
    [InlineData(0xAC00, 2)]
    [InlineData(0xFF21, 2)]
    [InlineData(0x1F600, 2)]
    [InlineData(0x2764, 1)]
    [InlineData(0x0301, 0)]
    [InlineData(0x200D, 0)]
    [InlineData(0xFE0F, 0)]
    [InlineData(0x20000, 2)]
    [InlineData(0x2500, 1)]
    public void WidthOfACodePoint(int rune, int width) => Assert.Equal(width, CharWidth.Of(rune));

    [Fact]
    public void CombiningMarksAtTheStartOfALineAreDropped()
    {
        var screen = new VtScreen().Feed("́a");
        Assert.Equal("a", screen.Screen);
    }

    [Fact]
    public void AMarkAfterTheLastColumnJoinsTheLastCharacter()
    {
        var screen = new VtScreen().Feed(new string('x', 19) + "é");
        Assert.Equal(new string('x', 19) + "é", screen.Screen);
    }
}
