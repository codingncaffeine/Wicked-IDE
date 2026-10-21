using Avalonia.Controls;
using Avalonia.VisualTree;
using SkiaSharp;
using Wicked.App.Chat;
using Wicked.App.Previews;
using Wicked.Editor.Model;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>Images open in a preview; Markdown has a preview that follows its edits, in the same group or to the side.</summary>
public sealed class PreviewWindowTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task AnImageOpensInAPreviewAndABrokenOneSaysSo()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        using (var bitmap = new SKBitmap(40, 30))
        {
            bitmap.Erase(SKColors.Orange);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(root, "logo.png"), data.ToArray());
        }

        File.WriteAllBytes(Path.Combine(root, "broken.png"), [1, 2, 3, 4, 5]);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            var open = bench.OpenAsync(Path.Combine(root, "logo.png"));
            Assert.True(Ui.PumpUntil(() => open.IsCompleted && open.Result is not null, 5), "the image did not open");
            var preview = Assert.IsType<ImagePreviewView>(open.Result!.CustomView);
            Assert.True(Ui.PumpUntil(() => preview.Bitmap is not null, 5), "the image was not decoded");
            Assert.Equal((40, 30), (preview.Bitmap!.PixelSize.Width, preview.Bitmap.PixelSize.Height));
            Assert.StartsWith("40 × 30", preview.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PreviewInfo").Text, StringComparison.Ordinal);
            Assert.Equal(Path.Combine(root, "logo.png"), open.Result.Tooltip);
            Assert.True(preview.Fit);
            preview.Fit = false;
            Assert.False(preview.Fit);

            // Opening it again brings the same tab forward.
            var again = bench.OpenAsync(Path.Combine(root, "logo.png"));
            Assert.True(Ui.PumpUntil(() => again.IsCompleted, 5));
            Assert.Same(open.Result, again.Result);

            var broken = bench.OpenAsync(Path.Combine(root, "broken.png"));
            Assert.True(Ui.PumpUntil(() => broken.IsCompleted && broken.Result is not null, 5));
            var brokenView = Assert.IsType<ImagePreviewView>(broken.Result!.CustomView);
            Assert.True(Ui.PumpUntil(() => brokenView.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "PreviewInfo" && t.Text?.StartsWith("This image could not be shown", StringComparison.Ordinal) == true), 5),
                "no word about the broken image");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheMarkdownPreviewFollowsTheEditsAndOpensToTheSide()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "README.md"), "# Title\n\nSome *text*.\n");
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            var md = OpenByClicks(window, shell, "README.md");

            var run = shell.RunCommandAsync("markdown.showPreview");
            Assert.True(Ui.PumpUntil(() => run.IsCompleted, 5));
            var preview = Assert.IsType<MarkdownPreviewView>(bench.ActiveTab!.CustomView);
            Assert.Equal("Preview README.md", bench.ActiveTab.Title);
            Assert.True(Ui.PumpUntil(() => preview.Blocks is { Count: 2 }, 5), $"blocks: {preview.Blocks?.Count}");
            Assert.IsType<MdHeading>(preview.Blocks![0]);

            // An edit to the file shows in the preview a moment later.
            md.Document.SetSelections([Selection.Caret(md.Document.Text.Length)]);
            Edits.Type(md.Document, "\n## Added\n", EditOptions.Default);
            Assert.True(Ui.PumpUntil(() => preview.Blocks is { Count: 3 } blocks && blocks[2] is MdHeading { Level: 2 }, 5), $"blocks: {preview.Blocks?.Count}");

            // To the side: the preview opens in a second group, the file stays in the first.
            bench.ActiveGroup.Activate(md);
            run = shell.RunCommandAsync("markdown.showPreviewToSide");
            Assert.True(Ui.PumpUntil(() => run.IsCompleted, 5));
            Assert.Equal(2, bench.Groups.Count);
            Assert.IsType<MarkdownPreviewView>(bench.Groups[1].Active!.CustomView);
            Assert.Contains(md, bench.Groups[0].Tabs);
            window.Close();
            return 0;
        });
    }
}
