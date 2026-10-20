using System.Text;
using Wicked.Core.Files;

namespace Wicked.Tests.Files;

public sealed class TextFileTests
{
    [Fact]
    public void EncodingsAreRecognisedByTheirMarksAndUtf8ByItsRules()
    {
        Assert.Equal(("é", "utf-8", false), Decoded([0xC3, 0xA9]));
        Assert.Equal(("é", "utf-8", true), Decoded([0xEF, 0xBB, 0xBF, 0xC3, 0xA9]));
        Assert.Equal(("A", "utf-16le", true), Decoded([0xFF, 0xFE, 0x41, 0x00]));
        Assert.Equal(("é", "latin1", false), Decoded([0xE9]));   // not valid UTF-8: Latin-1
        Assert.Equal(FileKind.Binary, TextFile.Decode([0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]).Kind);
    }

    private static (string, string, bool) Decoded(byte[] bytes)
    {
        var (text, encoding, kind) = TextFile.Decode(bytes);
        Assert.Equal(FileKind.Text, kind);
        return (text, encoding.Name, encoding.Bom);
    }

    [Fact]
    public async Task ASaveKeepsTheEncodingAndTheMarkAndLeavesNoTemporaryFile()
    {
        using var profile = new TempProfile();
        var path = Path.Combine(profile.Root, "a.txt");
        await File.WriteAllBytesAsync(path, [0xEF, 0xBB, 0xBF, (byte)'x'], TestContext.Current.CancellationToken);
        var loaded = await TextFile.ReadAsync(path, false, TestContext.Current.CancellationToken);

        await TextFile.WriteAsync(path, "héllo", loaded.Encoding, TestContext.Current.CancellationToken);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("héllo")], bytes);
        Assert.Single(Directory.GetFiles(profile.Root));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task ASaveKeepsAScriptExecutable()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes");
        using var profile = new TempProfile();
        var path = Path.Combine(profile.Root, "run.sh");
        await File.WriteAllTextAsync(path, "#!/bin/sh\n", TestContext.Current.CancellationToken);
        File.SetUnixFileMode(path, (UnixFileMode)Convert.ToInt32("755", 8));
        await TextFile.WriteAsync(path, "#!/bin/sh\necho hi\n", FileEncoding.Utf8, TestContext.Current.CancellationToken);
        Assert.True(File.GetUnixFileMode(path).HasFlag(UnixFileMode.UserExecute));
    }
}

public sealed class IgnoreAndMatchTests
{
    [Theory]
    [InlineData("*.log", "a/b/debug.log", false, true)]
    [InlineData("*.log", "a/b/debug.txt", false, false)]
    [InlineData("/build", "build/x.o", false, true)]
    [InlineData("/build", "src/build/x.o", false, false)]
    [InlineData("build/", "src/build", true, true)]
    [InlineData("build/", "src/build", false, false)]
    [InlineData("docs/**/*.md", "docs/a/b/c.md", false, true)]
    [InlineData("**/temp", "x/y/temp/z", false, true)]
    public void GitignorePatterns(string pattern, string path, bool isDirectory, bool ignored)
    {
        var rules = new IgnoreRules();
        rules.AddGitignore("", [pattern]);
        Assert.Equal(ignored, rules.IsIgnored(path, isDirectory));
    }

    [Fact]
    public void ANegationBringsAPathBack()
    {
        var rules = new IgnoreRules();
        rules.AddGitignore("", ["*.log", "!keep.log"]);
        Assert.True(rules.IsIgnored("a.log", false));
        Assert.False(rules.IsIgnored("keep.log", false));
    }

    [Fact]
    public void FuzzyMatchingPrefersFileNamesWordStartsAndShortPaths()
    {
        Assert.Equal(-1, FuzzyMatch.Score("xyz", "src/Program.cs"));
        Assert.True(FuzzyMatch.Score("prog", "src/Program.cs") > FuzzyMatch.Score("prog", "src/programs/data/x.cs"));
        Assert.True(FuzzyMatch.Score("mw", "src/MainWindow.cs") > FuzzyMatch.Score("mw", "src/somewhere.cs"));
        Assert.True(FuzzyMatch.Score("app", "app.cs") > FuzzyMatch.Score("app", "application-settings.cs"));
        Assert.Equal([4, 5, 6, 7], FuzzyMatch.Positions("prog", "src/program.cs"));
    }

    [Fact]
    public void TheIndexSkipsWhatIsIgnoredHiddenOrHeavy()
    {
        using var profile = new TempProfile();
        var root = profile.Root;
        void Touch(string relative)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
        }

        Touch("src/App.cs");
        Touch("src/Util.cs");
        Touch("debug.log");
        Touch(".git/config");
        Touch("node_modules/x/index.js");
        Touch("src/obj/gen.cs");
        Touch("docs/readme.md");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "*.log\n");
        File.WriteAllText(Path.Combine(root, "docs", ".gitignore"), "readme.md\n");

        var index = FileIndex.Build(root, TestContext.Current.CancellationToken);
        Assert.Equal([".gitignore", "docs/.gitignore", "src/App.cs", "src/Util.cs"], index.Files);
        Assert.Equal("src/App.cs", index.Search("app").First());
    }
}
