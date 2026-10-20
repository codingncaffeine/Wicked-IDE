using Wicked.Core.Engines;

namespace Wicked.Tests;

public sealed class PresetWriterTests
{
    [Fact]
    public void RendersTheDocumentedShapeDeterministically()
    {
        var text = PresetWriter.Render(
            new Dictionary<string, string> { ["n-gpu-layers"] = "all", ["c"] = "8192" },
            [
                PresetSection.ForModel("zeta/model:Q8_0", "/models/z.gguf"),
                PresetSection.ForModel("alpha/vision:Q4_K_M", "/models/a.gguf", "/models/mmproj-F16.gguf", 32768, "auto", new Dictionary<string, string> { ["jinja"] = "true" }),
            ]);

        Assert.Equal(
            """
            version = 1

            [*]
            c = 8192
            n-gpu-layers = all

            [alpha/vision:Q4_K_M]
            c = 32768
            jinja = true
            mmproj = /models/mmproj-F16.gguf
            model = /models/a.gguf
            n-gpu-layers = auto

            [zeta/model:Q8_0]
            model = /models/z.gguf

            """.ReplaceLineEndings("\n"),
            text);
    }

    [Fact]
    public void AnEmptyGlobalsBlockIsStillWritten() =>
        Assert.Equal("version = 1\n\n[*]\n", PresetWriter.Render(null, []));

    [Fact]
    public async Task OnlyRewritesTheFileWhenTheTextChanges()
    {
        using var dir = new TempProfile();
        var path = Path.Combine(dir.Root, "sub", "preset.ini");
        var section = PresetSection.ForModel("m", "/x.gguf");

        Assert.True(await PresetWriter.WriteAsync(path, null, [section], TestContext.Current.CancellationToken));
        var stamp = File.GetLastWriteTimeUtc(path);
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(await PresetWriter.WriteAsync(path, null, [section], TestContext.Current.CancellationToken));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
        Assert.True(await PresetWriter.WriteAsync(path, null, [PresetSection.ForModel("m", "/y.gguf")], TestContext.Current.CancellationToken));
        Assert.Contains("/y.gguf", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsInjectionThroughNamesKeysAndValues()
    {
        Assert.Throws<ArgumentException>(() => PresetWriter.Render(null, [new PresetSection("bad]\n[evil", new Dictionary<string, string> { ["a"] = "b" })]));
        Assert.Throws<ArgumentException>(() => PresetWriter.Render(null, [new PresetSection("ok", new Dictionary<string, string> { ["a = b\n[x"] = "b" })]));
        Assert.Throws<ArgumentException>(() => PresetWriter.Render(null, [new PresetSection("ok", new Dictionary<string, string> { ["a"] = "b\n[evil]" })]));
    }
}
