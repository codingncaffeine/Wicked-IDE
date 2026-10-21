using System.Diagnostics;
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
