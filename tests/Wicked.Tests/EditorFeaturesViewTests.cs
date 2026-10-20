using Avalonia.Headless;
using Wicked.App.Editor;
using Wicked.Editor.Layout;
using Wicked.Editor.Model;
using Wicked.Editor.Syntax;

namespace Wicked.Tests;

public sealed class EditorFeaturesViewTests
{
    [Fact]
    public void AnOverrideColoursExactlyOneCharacter()
    {
        var segments = LineShaper.Segments("f(a)", null, null, 0xFF111111, new ColorOverrides([1, 3], [0xFFFFD700, 0xFFFFD700]));
        Assert.Equal([(0, 1, 0xFF111111u), (1, 2, 0xFFFFD700u), (2, 3, 0xFF111111u), (3, 4, 0xFFFFD700u)], segments.Select(s => (s.Start, s.End, s.Color)));
    }

    [Fact]
    public async Task BracketsGuidesAndWhitespaceShowInTheEditor()
    {
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_FEATURES");
        await Ui.Run(() =>
        {
            var code = "class A\n{\n    void F(int x)\n    {\n        if (x > 0) { G(new[] { x }); }\n    }\n}\n";
            var editor = new CodeEditor(EditorServices.Ready.Result, new Document(code), Languages.ForFile("a.cs"));
            var window = new Avalonia.Controls.Window { Width = 900, Height = 400, Content = editor };
            window.Show();
            var doc = editor.Document;
            doc.SetSelections([Selection.Caret(doc.Text.Offset(4, 11))]); // just after "if ("... the "(" before x
            Assert.True(Ui.PumpUntil(() => editor.View.Syntax?.ValidLines == doc.Text.LineCount && editor.View.Features?.Version == doc.Version, 10), "features did not arrive");
            Ui.Pump(0.2);

            var frame = editor.View.BuildFrame();
            Assert.Equal(2, frame.BracketBoxes.Count);
            Assert.NotEmpty(frame.Guides);
            Assert.Contains(frame.Guides, g => g.Active);

            doc.SetSelections([new Selection(doc.Text.Offset(2, 0), doc.Text.Offset(2, 8))]);
            Assert.NotEmpty(editor.View.BuildFrame().Whitespace);

            if (!string.IsNullOrEmpty(shot))
            {
                doc.SetSelections([Selection.Caret(doc.Text.Offset(4, 11))]);
                Ui.Pump(0.4);
                using var image = window.CaptureRenderedFrame();
                image?.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }

            window.Close();
            return 0;
        });
    }
}

public sealed class FoldingViewTests
{
    [Fact]
    public async Task FoldingHidesTheBlockAndTheCaretStepsOverIt()
    {
        await Ui.Run(() =>
        {
            var code = "class A\n{\n    void F()\n    {\n        one();\n        two();\n    }\n    void G() { }\n}\n";
            var editor = new CodeEditor(EditorServices.Ready.Result, new Document(code), Languages.ForFile("a.cs"));
            var window = new Avalonia.Controls.Window { Width = 900, Height = 400, Content = editor };
            window.Show();
            editor.Focus();
            var doc = editor.Document;
            Assert.True(Ui.PumpUntil(() => editor.View.Features?.Version == doc.Version && editor.View.Features.Folding.Count > 0, 10), "no folding ranges");

            // Fold "void F()" from inside it: lines 4..6 disappear.
            doc.SetSelections([Selection.Caret(doc.Text.Offset(4, 10))]);
            Input.Chord(window, Avalonia.Input.Key.OemOpenBrackets, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift);
            var before = doc.Text.LineCount;
            Assert.True(editor.View.VisualLineCount < before, "nothing folded");
            Assert.True(editor.View.IsHidden(5));
            var frame = editor.View.BuildFrame();
            Assert.DoesNotContain(frame.Lines, l => l.Line == 5);
            Assert.Contains(frame.Lines, l => l.Fold == Wicked.Editor.View.FoldMark.Folded);

            // The fold is the indentation range under the "{" line (VS Code's rule): the statements hide, the "}" stays.
            // The caret went to the fold's line; Down steps over the hidden lines to the "}".
            var caretLine = doc.Text.LineOf(doc.Primary.Active);
            Assert.False(editor.View.IsHidden(caretLine));
            Assert.Equal("    {", doc.Text.Line(caretLine));
            Input.Chord(window, Avalonia.Input.Key.Down, Avalonia.Input.RawInputModifiers.None);
            Assert.Equal(6, doc.Text.LineOf(doc.Primary.Active));

            // An edit above keeps the fold where it was.
            doc.SetSelections([Selection.Caret(0)]);
            Edits.Type(doc, "// top\n", EditOptions.Default);
            Assert.True(editor.View.IsHidden(6));

            // The gutter's arrow opens it.
            var header = editor.View.Folds.Collapsed[0].StartLine;
            var y = editor.View.LineTop(header) + editor.View.LineHeight / 2;
            var point = new Avalonia.Point(editor.View.GutterWidth - 10, y);
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Ui.Pump(0.1);
            Assert.False(editor.View.IsHidden(6));
            window.Close();
            return 0;
        });
    }
}

public sealed class MinimapViewTests
{
    [Fact]
    public async Task TheMinimapShowsTheFileAndAClickScrollsThere()
    {
        await Ui.Run(() =>
        {
            var code = string.Join('\n', Enumerable.Range(0, 2000).Select(i => $"    int value{i} = {i}; // line {i}"));
            var editor = new CodeEditor(EditorServices.Ready.Result, new Document(code), Languages.ForFile("a.cs"));
            var window = new Avalonia.Controls.Window { Width = 1000, Height = 600, Content = editor };
            window.Show();
            Assert.True(Ui.PumpUntil(() => editor.View.Features?.Version == editor.Document.Version, 10));
            var frame = editor.View.BuildFrame();
            Assert.NotNull(frame.Minimap);
            Assert.NotEmpty(frame.Minimap!.Lines);
            Assert.Equal(0, editor.View.ScrollY);

            // A click near the minimap's foot scrolls down the file.
            var point = new Avalonia.Point(editor.View.MinimapLeft + 50, 580);
            window.MouseDown(point, Avalonia.Input.MouseButton.Left);
            window.MouseUp(point, Avalonia.Input.MouseButton.Left);
            Ui.Pump(0.1);
            Assert.True(editor.View.ScrollY > 0, "the minimap click did not scroll");
            window.Close();
            return 0;
        });
    }
}
