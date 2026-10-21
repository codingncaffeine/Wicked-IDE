using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Wicked.App.Chat;
using Wicked.Editor.Syntax;

namespace Wicked.Tests.Chat;

/// <summary>A chat code block's lines: shaped on a worker, never blank while a block grows or is recoloured.</summary>
public sealed class CodeTextTests
{
    private static HighlightedLine Line(string text, uint color = 0xFF569CD6) => new(text, [new ColoredSpan(0, Math.Min(6, text.Length), color, TokenStyle.None)]);

    /// <summary>How many pixels of the captured frame in a code row are not the black background.</summary>
    private static int InkInRow(Window window, int row)
    {
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no frame");
        using var locked = frame.Lock();
        var top = (int)(CodeText.Padding.Top + row * CodeText.LineHeight);
        var ink = 0;
        for (var y = top; y < top + (int)CodeText.LineHeight && y < locked.Size.Height; y++)
        {
            for (var x = 0; x < Math.Min(300, locked.Size.Width); x++)
            {
                if ((System.Runtime.InteropServices.Marshal.ReadInt32(locked.Address, y * locked.RowBytes + x * 4) & 0x00FFFFFF) != 0) ink++;
            }
        }

        return ink;
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task LinesAreShapedOnAWorkerAndAGrowingBlockIsNeverDrawnBlank()
    {
        await Ui.Run(() =>
        {
            var code = new CodeText { Foreground = Brushes.White };
            var window = new Window { Width = 500, Height = 200, Background = Brushes.Black, Content = new ScrollViewer { Content = code } };
            window.Show();
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                code.SetLines([Line("public int Total = 1;"), Line("return Total;")]);
                Assert.True(Ui.PumpUntil(() => InkInRow(window, 0) > 20 && InkInRow(window, 1) > 20, 5), "the first lines were never drawn");

                // The worker is held. The block grows (new line objects, as the highlighter hands back) and its first line is
                // recoloured: frames go on being drawn, the lines it had keep their old shapes, and the new line waits for the
                // worker: the UI thread shapes nothing.
                CodeText.ShapingGate = gate.Task;
                code.SetLines([Line("public int Total = 1;", 0xFFCE9178), Line("return Total;"), Line("// one more line")]);
                Ui.Pump(0.3);
                Assert.True(InkInRow(window, 0) > 20, "a recoloured line went blank until its new shape was ready");
                Assert.True(InkInRow(window, 1) > 20, "a line that did not change went blank while the block grew");
                Assert.Equal(0, InkInRow(window, 2));

                gate.SetResult();
                Assert.True(Ui.PumpUntil(() => InkInRow(window, 2) > 20, 5), "the new line was never drawn");
            }
            finally
            {
                CodeText.ShapingGate = null;
                gate.TrySetResult();
                window.Close();
            }

            return 0;
        });
    }
}
