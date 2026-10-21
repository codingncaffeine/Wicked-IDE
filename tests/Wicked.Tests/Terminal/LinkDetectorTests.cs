using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class LinkDetectorTests
{
    private static TerminalLink Only(string text) => Assert.Single(LinkDetector.Find(text));

    [Theory]
    [InlineData("see https://example.com/a/b?c=d#e for more", "https://example.com/a/b?c=d#e")]
    [InlineData("(https://example.com/x)", "https://example.com/x")]
    [InlineData("https://example.com/x.", "https://example.com/x")]
    [InlineData("https://example.com/x, then", "https://example.com/x")]
    [InlineData("<http://localhost:8080/>", "http://localhost:8080/")]
    [InlineData("https://en.wikipedia.org/wiki/Foo_(bar)", "https://en.wikipedia.org/wiki/Foo_(bar)")]
    [InlineData("file:///home/me/x.txt", "file:///home/me/x.txt")]
    [InlineData("ssh://git@host/repo.git", "ssh://git@host/repo.git")]
    public void AnAddressIsFoundWithoutItsSurroundingPunctuation(string text, string url)
    {
        var link = Only(text);
        Assert.Equal(LinkKind.Url, link.Kind);
        Assert.Equal(url, link.Target);
        Assert.Equal(url, text.Substring(link.Start, link.Length));
    }

    [Theory]
    [InlineData("src/app.cs:12:5: error CS0103", "src/app.cs", 12, 5)]
    [InlineData("src/app.cs:12: warning", "src/app.cs", 12, null)]
    [InlineData("./build.sh:3", "./build.sh", 3, null)]
    [InlineData("../lib/util.py:40:1", "../lib/util.py", 40, 1)]
    [InlineData("/usr/lib/foo.so:1", "/usr/lib/foo.so", 1, null)]
    [InlineData("~/notes/todo.md", "~/notes/todo.md", null, null)]
    [InlineData("Program.cs(42,13): error", "Program.cs", 42, 13)]
    [InlineData("Program.cs(42): error", "Program.cs", 42, null)]
    [InlineData("C:\\work\\app\\Main.java:7:2", "C:\\work\\app\\Main.java", 7, 2)]
    [InlineData("  at Foo.Bar() in /src/Foo.cs:line", "/src/Foo.cs", null, null)]
    [InlineData("error in main.rs:88:9", "main.rs", 88, 9)]
    [InlineData("package.json", "package.json", null, null)]
    public void AFileReferenceKeepsItsPositionInTheFile(string text, string path, int? line, int? column)
    {
        var link = LinkDetector.Find(text).First(l => l.Kind == LinkKind.Path);
        Assert.Equal(path, link.Target);
        Assert.Equal(line, link.Line);
        Assert.Equal(column, link.Column);
    }

    [Fact]
    public void APythonTracebackLineIsAFileReference()
    {
        var link = Only("  File \"/work/app/main.py\", line 17, in <module>");
        Assert.Equal("/work/app/main.py", link.Target);
        Assert.Equal(17, link.Line);
    }

    [Theory]
    [InlineData("version 1.2.3 is out")]
    [InlineData("took 3.5 seconds")]
    [InlineData("e.g. this")]
    [InlineData("12:30:45")]
    [InlineData("just words and spaces")]
    public void OrdinaryTextHasNoLinks(string text) => Assert.Empty(LinkDetector.Find(text));

    [Fact]
    public void AnAddressInsideAPathIsNotAlsoAPath()
    {
        var links = LinkDetector.Find("open https://example.com/dir/file.html now");
        Assert.Single(links);
        Assert.Equal(LinkKind.Url, links[0].Kind);
    }

    [Fact]
    public void SeveralLinksComeBackInOrder()
    {
        var links = LinkDetector.Find("a.cs:1 and https://x.org/y and b/c.cs:2:3");
        Assert.Equal(["a.cs", "https://x.org/y", "b/c.cs"], links.Select(l => l.Target));
    }

    [Fact]
    public void TheLinkAtAnOffsetIsFound()
    {
        const string text = "go to src/a.cs:9 now";
        Assert.Equal("src/a.cs", LinkDetector.At(text, 8)!.Target);
        Assert.Null(LinkDetector.At(text, 1));
    }

    [Fact]
    public void AVeryLongLineIsReadInBoundedTime()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        LinkDetector.Find(new string('a', 100_000) + "/" + new string('b', 100_000));
        Assert.True(watch.ElapsedMilliseconds < 5000, $"{watch.ElapsedMilliseconds} ms");
    }
}
