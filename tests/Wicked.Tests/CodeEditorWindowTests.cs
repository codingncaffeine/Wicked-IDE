using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.Editor;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;

namespace Wicked.Tests;

/// <summary>The code editor in a real (headless) window: keys typed, frames drawn, the UI thread's cost measured.</summary>
public sealed class CodeEditorWindowTests
{
    private static (Window Window, CodeEditor Editor) Open(string text, string file)
    {
        var services = EditorServices.Ready.Result;
        var editor = new CodeEditor(services, new Document(text), Languages.ForFile(file));
        var window = new Window { Width = 1200, Height = 800, Content = editor };
        window.Show();
        editor.Focus();
        Ui.Pump(0.2);
        return (window, editor);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheLineBracketSelectionAndCursorKeysDoWhatVSCodesDo()
    {
        await Ui.Run(() =>
        {
            var (window, editor) = Open("f(alpha, beta) {\n    x\n}\n", "a.js");
            var doc = editor.Document;
            void Press(Key key, RawInputModifiers mods) => Input.Chord(window, key, mods);
            string Show() => Wicked.Tests.Editor.Marked.Show(doc);
            Assert.True(Ui.PumpUntil(() => editor.IsFocused, 5), "the editor did not take the keyboard");

            // Ctrl+Enter: a new line below, indented like this one, without splitting it.
            doc.SetSelections([Selection.Caret(doc.Text.Offset(1, 5))]);
            Press(Key.Enter, RawInputModifiers.Control);
            Assert.Equal("f(alpha, beta) {\n    x\n    |\n}\n", Show());
            Input.Chord(window, Key.Z, RawInputModifiers.Control);

            // Ctrl+Shift+\: from just inside "(" to just before ")", and back.
            doc.SetSelections([Selection.Caret(2)]);
            Press(Key.OemPipe, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => doc.Primary.Active == 13, 5), Show());
            Press(Key.OemPipe, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(Ui.PumpUntil(() => doc.Primary.Active == 2, 5), Show());

            // Shift+Alt+Right twice: the word, then the brackets' contents; Shift+Alt+Left steps back to the word.
            doc.SetSelections([Selection.Caret(4)]);
            Press(Key.Right, RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.True(Ui.PumpUntil(() => Show() == "f([alpha], beta) {\n    x\n}\n", 5), Show());
            Press(Key.Right, RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.True(Ui.PumpUntil(() => Show() == "f([alpha, beta]) {\n    x\n}\n", 5), Show());
            Press(Key.Left, RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.Equal("f([alpha], beta) {\n    x\n}\n", Show());

            // Ctrl+U: the caret goes back to where it was before it moved.
            doc.SetSelections([Selection.Caret(0)]);
            Press(Key.End, RawInputModifiers.None);
            Press(Key.Down, RawInputModifiers.None);
            Press(Key.U, RawInputModifiers.Control);
            Assert.Equal("f(alpha, beta) {|\n    x\n}\n", Show());
            Press(Key.U, RawInputModifiers.Control);
            Assert.Equal("|f(alpha, beta) {\n    x\n}\n", Show());

            // Shift+Alt+I: a caret at the end of each selected line.
            doc.SetSelections([new Selection(0, doc.Text.Offset(1, 2))]);
            Press(Key.I, RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.Equal("f(alpha, beta) {|\n  |  x\n}\n", Show());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task StickyScrollPinsTheScopesHeadersAndRulersAreDrawn()
    {
        var body = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"    let v{i} = {i};"));
        var text = "function outer() {\n  function inner() {\n" + body.Replace("    ", "      ", StringComparison.Ordinal) + "\n  }\n}\n";
        await Ui.Run(() =>
        {
            var (window, editor) = Open(text, "a.js");
            editor.ApplySettings(new Wicked.Core.Settings.WickedSettings { Rulers = [20], StickyScroll = true, Minimap = false });
            var view = editor.View;

            // Scrolled into inner(): both headers are pinned, outermost first, over the first two rows.
            view.ScrollTo(0, 20 * view.LineHeight);
            editor.InvalidateVisual();
            Assert.True(Ui.PumpUntil(() => view.BuildFrame().Sticky.Count == 2, 5), $"pinned: {view.BuildFrame().Sticky.Count}");
            var frame = view.BuildFrame();
            Assert.Equal([0, 1], frame.Sticky.Select(l => l.Line));
            Assert.Equal([0f, view.LineHeight], frame.Sticky.Select(l => l.Y));

            // The ruler at column 20, drawn in the ruler colour below the band.
            var rulerX = Assert.Single(frame.Rulers);
            Assert.Equal(view.TextLeft + 20 * view.Fonts.CharAdvance, rulerX, 0.5);
            Ui.Pump(0.2);
            using (var shot = window.CaptureRenderedFrame()!)
            {
                var pixel = PixelAt(shot, (int)MathF.Round(rulerX), (int)(view.LineHeight * 6));
                Assert.Equal(view.Theme.Ruler & 0xFFFFFF, pixel & 0xFFFFFF);
            }

            // At the top of the file nothing is pinned.
            view.ScrollTo(0, 0);
            Assert.Empty(view.BuildFrame().Sticky);

            // A click on a pinned header goes to it, the header at the top of the view.
            view.ScrollTo(0, 20 * view.LineHeight);
            view.BuildFrame();
            var centre = editor.TranslatePoint(new Avalonia.Point(view.TextLeft + 40, view.LineHeight * 1.5), window)!.Value;
            window.MouseDown(centre, MouseButton.Left);
            window.MouseUp(centre, MouseButton.Left);
            Ui.Pump(0.1);
            Assert.Equal(1, editor.Document.Text.LineOf(editor.Document.Primary.Active));
            Assert.Equal(view.LineHeight, view.ScrollY, 0.5);

            // Off, nothing is pinned.
            editor.ApplySettings(new Wicked.Core.Settings.WickedSettings { StickyScroll = false });
            view.ScrollTo(0, 20 * view.LineHeight);
            Assert.Empty(view.BuildFrame().Sticky);
            window.Close();
            return 0;
        });
    }

    /// <summary>A pixel of a captured frame as 0xAARRGGBB, whichever byte order the frame is in.</summary>
    private static uint PixelAt(Avalonia.Media.Imaging.WriteableBitmap bitmap, int x, int y)
    {
        using var buffer = bitmap.Lock();
        var raw = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(buffer.Address, y * buffer.RowBytes + x * 4);
        if (buffer.Format != Avalonia.Platform.PixelFormat.Rgba8888) return raw; // BGRA in memory reads as 0xAARRGGBB
        return (raw & 0xFF00FF00) | ((raw & 0xFF) << 16) | ((raw >> 16) & 0xFF);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TypingThroughTheWindowEditsTheDocument()
    {
        await Ui.Run(() =>
        {
            var (window, editor) = Open("class A\n{\n}\n", "a.cs");
            editor.Document.SetSelections([Selection.Caret(editor.Document.Text.Offset(1, 1))]);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyTextInput("int x = 1;");
            Ui.Pump(0.1);
            Assert.Equal("class A\n{\n    int x = 1;\n}\n", editor.Document.Text.ToString());

            Input.Chord(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal("class A\n{\n    \n}\n", editor.Document.Text.ToString());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheEditorDrawsHighlightedTextThroughTheCompositor()
    {
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_EDITOR");
        await Ui.Run(() =>
        {
            var code = string.Join('\n', Enumerable.Range(0, 60).Select(i => i % 7 == 0 ? $"// comment {i}" : $"public static int Value{i}(string name) => name.Length + {i};"));
            var (window, editor) = Open(code, "a.cs");
            Assert.True(Ui.PumpUntil(() => editor.View.Syntax?.ValidLines == editor.Document.Text.LineCount, 10), "highlighting did not arrive");
            Ui.Pump(0.3);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(shot)) frame.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ScrollingAHugeFileCostsTheUiThreadLittle()
    {
        await Ui.Run(() =>
        {
            var code = string.Join('\n', Enumerable.Range(0, 100_000).Select(i => $"    public static int Value{i}(string name) => name.Length + {i}; // line {i}"));
            var (window, editor) = Open(code, "a.cs");
            var view = editor.View;
            if (Environment.GetEnvironmentVariable("WICKED_TIMING_SETTLED") == "1")
            {
                Assert.True(Ui.PumpUntil(() => view.Syntax?.ValidLines == editor.Document.Text.LineCount, 60));
                GC.Collect();
            }

            // A frame's UI-thread work is building it: lookups, no shaping. Time it across a long scroll.
            var times = new List<double>();
            var collections = new List<string>();
            for (var i = 0; i < 200; i++)
            {
                view.ScrollBy(0, view.LineHeight * 37);
                var (g0, g1, g2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
                var clock = Stopwatch.StartNew();
                view.BuildFrame();
                times.Add(clock.Elapsed.TotalMilliseconds);
                if (times[^1] > 2) collections.Add($"#{i} {times[^1]:0.0} ms, collections during it gen0 {GC.CollectionCount(0) - g0} gen1 {GC.CollectionCount(1) - g1} gen2 {GC.CollectionCount(2) - g2}");
            }

            times.Sort();
            var median = times[times.Count / 2];
            var p95 = times[(int)(times.Count * 0.95)];
            var report = $"frame build while scrolling 100k lines: median {median:0.000} ms, p95 {p95:0.000} ms, worst {times[^1]:0.000} ms; over 2 ms: {string.Join("; ", collections)}";
            if (Environment.GetEnvironmentVariable("WICKED_TIMINGS") is { Length: > 0 } timings) File.AppendAllText(timings, report + "\n");
            Assert.True(median < 2, $"median frame build {median:0.000} ms");
            window.Close();
            return 0;
        });
    }
}
