using SkiaSharp;
using Wicked.Editor.Features;
using Wicked.Editor.Layout;
using Wicked.Editor.Model;
using Wicked.Editor.View;

namespace Wicked.Tests.Editor;

/// <summary>The UI side of word wrap: rows kept in step with edits and folds, and a view that places, hits and steps by rows.</summary>
public sealed class WrapLayoutTests
{
    private static readonly WrapOptions Twenty = WrapOptions.AtColumn(20);

    private static void SameAsFresh(WrapLayout layout, Wicked.Editor.Text.Rope text)
    {
        var fresh = WrapLayout.From(WrapMap.Compute(text, 0, Twenty));
        layout.Sync(null);
        fresh.Sync(null);
        Assert.Equal(fresh.LineCount, layout.LineCount);
        Assert.Equal(fresh.RowCount, layout.RowCount);
        for (var line = 0; line < text.LineCount; line++)
        {
            Assert.Equal(fresh.RowsOf(line), layout.RowsOf(line));
            Assert.Equal(fresh.FirstRow(line), layout.FirstRow(line));
            for (var sub = 0; sub < fresh.RowsOf(line); sub++) Assert.Equal(fresh.Segment(line, sub, text.LineLength(line)), layout.Segment(line, sub, text.LineLength(line)));
        }
    }

    [Fact]
    public void EditsPatchTheRowsExactlyAsAFreshWrapWould()
    {
        var random = new Random(7);
        string Line() => string.Join(' ', Enumerable.Range(0, random.Next(0, 12)).Select(_ => new string('x', random.Next(1, 9))));
        var doc = new Document(string.Join('\n', Enumerable.Range(0, 40).Select(_ => Line())));
        var layout = WrapLayout.From(WrapMap.Compute(doc.Text, doc.Version, Twenty));
        doc.Changed += layout.Apply;
        for (var step = 0; step < 300; step++)
        {
            var rope = doc.Text;
            var at = random.Next(0, rope.Length + 1);
            var length = Math.Min(random.Next(0, 30), rope.Length - at);
            var insert = random.Next(4) switch { 0 => "", 1 => "\n", 2 => Line() + "\n" + Line(), _ => Line() };
            doc.Apply([new TextChange(at, length, insert)], [Selection.Caret(at + insert.Length)]);
            SameAsFresh(layout, doc.Text);
        }
    }

    [Fact]
    public void FoldedLinesTakeNoRows()
    {
        var text = new Document("short\n" + string.Join(' ', Enumerable.Repeat("word", 20)) + "\nend\nlast").Text;
        var layout = WrapLayout.From(WrapMap.Compute(text, 0, Twenty));
        layout.Sync(null);
        var wrapped = layout.RowsOf(1);
        Assert.True(wrapped > 2);
        Assert.Equal(wrapped + 3, layout.RowCount);

        var folds = FoldState.None(4).Collapse(new FoldRange(0, 2));
        layout.Sync(folds);
        Assert.Equal(2, layout.RowCount);           // "short" and "last"
        Assert.Equal((3, 0), layout.LineAtRow(1));
        Assert.Equal((0, 0), layout.LineAtRow(0));
    }
}

public sealed class WrapViewTests(FontsFixture fixture) : IClassFixture<FontsFixture>
{
    private readonly EditorFonts _fonts = fixture.Fonts;
    private static readonly string Long = string.Join(' ', Enumerable.Repeat("word", 60));

    private TextView Wrapped(LayoutService layout, Document doc)
    {
        var view = new TextView(doc, layout, EditOptions.Default) { ShowMinimap = false, WordWrap = new WordWrap(WordWrapMode.On) };
        view.Resize(400, 600);
        view.BuildFrame();
        return view;
    }

    [Fact]
    public void ALongLineBecomesRowsThatCoverItAndFitTheWidth()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(Long + "\nnext");
        var view = Wrapped(layout, doc);
        Assert.True(view.Wrapping);
        var rows = view.BuildFrame().Lines.Where(l => l.Line == 0).ToList();
        Assert.InRange(rows.Count, 5, 12);
        Assert.Equal(rows.Count + 1, view.VisualLineCount);
        Assert.Equal(0, view.MaxScrollX);
        for (var i = 0; i < rows.Count; i++)
        {
            Assert.Equal(i, rows[i].Sub);
            Assert.Equal(i == rows.Count - 1, rows[i].LastRow);
            if (i > 0) Assert.Equal(rows[i - 1].ClipRight, rows[i].ClipLeft);
            if (i < rows.Count - 1) Assert.InRange(rows[i].ClipRight - rows[i].ClipLeft, 1, 400 - view.TextLeft);
        }

        Assert.True(float.IsPositiveInfinity(rows[^1].ClipRight));
        Assert.Equal(1, view.VisualOf(1) - view.VisualOf(0) - rows.Count + 1);
    }

    [Fact]
    public void EveryPositionIsDrawnWhereAClickFindsIt()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document("    " + Long);
        var view = Wrapped(layout, doc);
        for (var offset = 0; offset <= doc.Text.Length; offset++)
        {
            var (row, x) = view.Place(0, offset);
            var y = row * view.LineHeight - view.ScrollY + view.LineHeight / 2;
            Assert.Equal(offset, view.OffsetAt(view.TextLeft + x + 0.5f, y));
        }
    }

    [Fact]
    public void ContinuationRowsLineUpUnderTheIndent()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document("        " + Long);
        var view = Wrapped(layout, doc);
        var second = view.BuildFrame().Lines.First(l => l.Sub == 1);
        Assert.Equal(8 * _fonts.CharAdvance, second.ClipLeft + second.Shift, 2);
    }

    [Fact]
    public void UpAndDownStepByRowsAndKeepTheirX()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(Long + "\nshort");
        var view = Wrapped(layout, doc);
        var (row0, x0) = view.Place(0, 6);
        var (to, goal) = view.RowStep(6, 1, -1);
        var (row1, x1) = view.Place(0, to);
        Assert.Equal(row0 + 1, row1);
        Assert.InRange(x1, x0 - _fonts.CharAdvance, x0 + _fonts.CharAdvance);

        // Down past the long line's last row reaches the next line; the aim is kept through a short line.
        var rows = view.VisualOf(1);
        var (atShort, kept) = view.RowStep(6, rows, -1);
        Assert.Equal(doc.Text.Length, atShort); // "short" is shorter than the aim: its end
        Assert.Equal(goal, kept);
    }

    [Fact]
    public void EndAndHomeStopAtTheRowFirst()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(Long);
        var view = Wrapped(layout, doc);
        var rowEnd = view.RowEnd(0);
        Assert.True(rowEnd < doc.Text.Length);
        Assert.Equal(0, view.Place(0, rowEnd).Row);
        Assert.Equal(doc.Text.Length, view.RowEnd(rowEnd));

        var secondRowStart = rowEnd + 1;
        Assert.Equal(1, view.Place(0, secondRowStart).Row);
        Assert.Equal(secondRowStart, view.RowHome(secondRowStart + 3));
        Assert.Null(view.RowHome(secondRowStart));
    }

    [Fact]
    public void ASelectionAcrossRowsIsARectanglePerRow()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document(Long);
        var view = Wrapped(layout, doc);
        var rowEnd = view.RowEnd(0);
        doc.SetSelections([new Selection(2, rowEnd + 6)]);
        var frame = view.BuildFrame();
        Assert.Equal(2, frame.Selections.Count);
        Assert.True(frame.Selections[1].Top > frame.Selections[0].Top);
        Assert.Single(frame.Carets);
        Assert.Equal(frame.Selections[1].Top + 1, frame.Carets[0].Top);
    }

    [Fact]
    public void AnEditKeepsTheRowsInStepWithoutAWorker()
    {
        using var layout = new LayoutService(_fonts);
        var doc = new Document("a\n" + Long);
        var view = Wrapped(layout, doc);
        var before = view.VisualLineCount;
        doc.SetSelections([Selection.Caret(doc.Text.Length)]);
        Edits.Type(doc, " " + Long, EditOptions.Default);
        Assert.True(view.Wrapping);
        Assert.True(view.VisualLineCount > before);
        Edits.NewLine(doc, EditOptions.Default);
        Assert.True(view.Wrapping);
        Assert.Equal(doc.Text.LineCount - 1, view.LineAt(view.LineTop(doc.Text.LineCount - 1) + 1));
    }

    [Fact]
    public async Task AShapedLineIsDrawnOnce()
    {
        // Text drawn twice over itself looks bolder: a shaped line must not also take the plain path.
        using var layout = new LayoutService(_fonts);
        var ready = new SemaphoreSlim(0);
        layout.Ready += () => ready.Release();
        var doc = new Document("Hello, wrapped world");
        var view = new TextView(doc, layout, EditOptions.Default) { ShowMinimap = false, CaretsVisible = false };
        view.Resize(400, 100);
        view.BuildFrame();
        Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var frame = view.BuildFrame();
        Assert.NotNull(frame.Lines[0].Shaped);

        static byte[] Pixels(EditorFrame frame)
        {
            using var surface = SKSurface.Create(new SKImageInfo(400, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
            EditorPainter.Paint(surface.Canvas, frame);
            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);
            return bitmap.Bytes;
        }

        // The same frame with no text for the plain path to draw: identical pixels when the shaped runs are all that is drawn.
        var shapedOnly = frame with { Lines = [.. frame.Lines.Select(l => l with { Text = "" })] };
        Assert.Equal(Pixels(shapedOnly), Pixels(frame));
    }
}
