using Avalonia.Headless;
using Avalonia.Input;
using Wicked.Editor.View;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>Word wrap through the window: Alt+Z, the keys that move by rows, and editor.wordWrap applied live.</summary>
public sealed class WordWrapWindowTests
{
    private static readonly string Long = "    var sentence = \"" + string.Join(' ', Enumerable.Repeat("a fairly long line of words", 12)) + "\";";

    private static void Key(Avalonia.Controls.Window window, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Ui.Pump(0.05);
    }

    [Fact]
    public async Task AltZWrapsTheLineAndTheKeysMoveByRows()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "Long.cs"), "class Long\n{\n" + Long + "\n}\n");
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_WRAP");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
            var opened = shell.OpenFolderAndRememberAsync(root);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && Rows(window).Any(), 10), "the folder did not open");
            var tab = OpenByClicks(window, shell, "Long.cs");
            var editor = tab.Editor!;
            var doc = tab.Document;
            Assert.False(editor.View.Wrapping);
            var unwrappedRows = editor.View.VisualLineCount;

            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Alt);
            Assert.Equal(WordWrapMode.On, editor.WordWrap.Mode);
            Ui.Pump(0.1);
            Assert.True(editor.View.Wrapping);
            Assert.True(editor.View.VisualLineCount > unwrappedRows + 1, "the long line did not wrap");
            Assert.Equal(0, editor.View.MaxScrollX);

            // On the long line, End stops at its first row's end; Down goes to the next row of the same line.
            doc.SetSelections([Wicked.Editor.Model.Selection.Caret(doc.Text.LineStart(2))]);
            Key(window, Avalonia.Input.Key.End);
            var rowEnd = doc.Primary.Active;
            Assert.Equal(2, doc.Text.LineOf(rowEnd));
            Assert.True(rowEnd < doc.Text.LineStart(2) + doc.Text.LineLength(2), "End went to the line's end, not the row's");
            Key(window, Avalonia.Input.Key.Down);
            Assert.Equal(2, doc.Text.LineOf(doc.Primary.Active));
            Key(window, Avalonia.Input.Key.End);
            Key(window, Avalonia.Input.Key.End);
            Key(window, Avalonia.Input.Key.Down);
            Assert.Equal(3, doc.Text.LineOf(doc.Primary.Active));

            if (!string.IsNullOrEmpty(shot))
            {
                doc.SetSelections([new Wicked.Editor.Model.Selection(doc.Text.LineStart(2) + 30, rowEnd + 40)]);
                editor.InvalidateVisual();
                Ui.Pump(0.5);
                using var image = window.CaptureRenderedFrame();
                image?.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            // Alt+Z again: back to one row per line and a horizontal scroll.
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Alt);
            Assert.False(editor.View.Wrapping);
            Assert.Equal(unwrappedRows, editor.View.VisualLineCount);

            // editor.wordWrap set by hand applies to an open editor (the toggle is for this editor only until then).
            File.WriteAllText(profile.Paths.SettingsFile, "{ \"editor.wordWrap\": \"wordWrapColumn\", \"editor.wordWrapColumn\": 40 }");
            Assert.True(Ui.PumpUntil(() => shell.Settings.WordWrap == "wordWrapColumn", 5), "the setting was not read");
            Assert.Equal(WordWrapMode.Off, editor.WordWrap.Mode); // Alt+Z's choice holds for this editor
            Key(window, Avalonia.Input.Key.Z, RawInputModifiers.Alt);
            Assert.Equal(new WordWrap(WordWrapMode.WordWrapColumn, 40), editor.WordWrap);
            Ui.Pump(0.1);
            Assert.True(editor.View.Wrapping);
            window.Close();
            return 0;
        });
    }
}
