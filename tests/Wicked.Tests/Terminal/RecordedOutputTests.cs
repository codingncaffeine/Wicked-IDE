using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

/// <summary>
/// The terminal output of real programs (vim, less, nano, dialog, ls) recorded once with script(1) at 80 by 24 and
/// replayed here. The expected screens next to them were checked by eye when recorded; they catch any later change in
/// how the emulator reads what these programs send. Set WICKED_UPDATE_RECORDINGS=1 to rewrite them after a deliberate change.
/// </summary>
public sealed class RecordedOutputTests
{
    private static string Dir => Path.Combine(AppContext.BaseDirectory, "Terminal", "Recordings");

    private static string Describe(VtScreen screen)
    {
        var rows = Enumerable.Range(0, 24).Select(r => screen.Row(r)).ToList();
        var (row, column) = screen.Cursor;
        var alt = screen.Emulator.Modes.AlternateScreen ? "alternate" : "main";
        return string.Join('\n', rows) + $"\n-- cursor {row},{column} {alt} scrollback {screen.Emulator.ScrollbackCount}\n";
    }

    public static TheoryData<string> Names => ["vim", "less", "nano", "dialog", "ls"];

    [Theory]
    [MemberData(nameof(Names))]
    public void ARecordedProgramLooksTheSameAsWhenItWasChecked(string name)
    {
        var screen = new VtScreen(80, 24).Feed(File.ReadAllBytes(Path.Combine(Dir, name + ".vt")));
        var actual = Describe(screen);
        var goldenPath = Path.Combine(Dir, name + ".screen");
        if (Environment.GetEnvironmentVariable("WICKED_UPDATE_RECORDINGS") == "1")
        {
            // Written next to the source so the change shows up in the working tree.
            var source = Path.Combine(FindRepo(), "tests", "Wicked.Tests", "Terminal", "Recordings", name + ".screen");
            File.WriteAllText(source, actual);
            return;
        }

        Assert.Equal(File.ReadAllText(goldenPath).ReplaceLineEndings("\n"), actual);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void TheSameScreenComesOutWhateverTheChunking(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(Dir, name + ".vt"));
        var whole = Describe(new VtScreen(80, 24).Feed(bytes));
        foreach (var size in new[] { 1, 2, 3, 7, 64, 1000 })
        {
            var screen = new VtScreen(80, 24);
            for (var i = 0; i < bytes.Length; i += size) screen.Feed(bytes[i..Math.Min(bytes.Length, i + size)]);
            Assert.Equal(whole, Describe(screen));
        }
    }

    [Fact]
    public void TheDialogBoxIsDrawnWithLineDrawingAndColour()
    {
        var screen = new VtScreen(80, 24).Feed(File.ReadAllBytes(Path.Combine(Dir, "dialog.vt")));
        var text = screen.Screen;
        Assert.Contains('┌', text);
        Assert.Contains('┘', text);
        Assert.Contains("Hello", text);
        Assert.Contains("red text and more text", text);
        var redRow = Enumerable.Range(0, 24).First(r => screen.Row(r).Contains("red text", StringComparison.Ordinal));
        var redColumn = screen.Row(redRow).IndexOf("red", StringComparison.Ordinal);
        Assert.True(CellColor.IsPalette(screen.Cell(redRow, redColumn).Fg));
    }

    [Fact]
    public void VimUsesTheAlternateScreen()
    {
        var screen = new VtScreen(80, 24).Feed(File.ReadAllBytes(Path.Combine(Dir, "vim.vt")));
        Assert.True(screen.Emulator.Modes.AlternateScreen);
        Assert.Equal(0, screen.Emulator.ScrollbackCount);
    }

    [Fact]
    public void TrueColourAndBackgroundsInTheLsRecordingArePreserved()
    {
        var screen = new VtScreen(80, 24).Feed(File.ReadAllBytes(Path.Combine(Dir, "ls.vt")));
        var row = Enumerable.Range(0, 24).First(r => screen.Row(r).Contains("true colour", StringComparison.Ordinal));
        var column = screen.Row(row).IndexOf("true colour", StringComparison.Ordinal);
        var cell = screen.Cell(row, column);
        Assert.Equal(0xFFFF6400u, CellColor.ToArgb(cell.Fg));
        Assert.Equal(CellFlags.Bold | CellFlags.Underline, cell.Flags & CellFlags.Pen);
    }

    private static string FindRepo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Wicked.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("the repository root was not found");
    }
}
