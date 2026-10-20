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
