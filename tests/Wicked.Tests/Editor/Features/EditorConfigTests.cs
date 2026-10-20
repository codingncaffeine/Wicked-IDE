using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Tests.Editor.Features;

public sealed class EditorConfigTests
{
    private static EditorConfigResolver Fake(Dictionary<string, string> files, List<string>? reads = null) =>
        new(dir =>
        {
            reads?.Add(dir);
            return files.GetValueOrDefault(dir);
        });

    private static EditorConfigProperties Resolve(string config, string file) => Fake(new() { ["/proj"] = config }).Resolve(file);

    private static bool Glob(string glob, string path) => EditorConfigGlob.Create(glob)!.IsMatch(path);

    // ── Globs ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StarMatchesWithinOneDirectoryLevel()
    {
        Assert.True(Glob("*.cs", "a.cs"));
        Assert.True(Glob("*.cs", "src/deep/a.cs")); // no '/' in the glob: any level
        Assert.False(Glob("*.cs", "a.csx"));
        Assert.True(Glob("src/*.cs", "src/a.cs"));
        Assert.False(Glob("src/*.cs", "src/deep/a.cs"));
        Assert.False(Glob("src/*.cs", "other/src/a.cs")); // anchored
    }

    [Fact]
    public void DoubleStarCrossesDirectories()
    {
        Assert.True(Glob("src/**.cs", "src/deep/er/a.cs"));
        Assert.True(Glob("**.cs", "a/b/c.cs"));
        Assert.True(Glob("**/gen/*.cs", "x/gen/a.cs"));
        Assert.False(Glob("**/gen/*.cs", "x/gen/y/a.cs"));
    }

    [Fact]
    public void QuestionMarkIsOneNonSeparatorCharacter()
    {
        Assert.True(Glob("a?c", "abc"));
        Assert.False(Glob("a?c", "ac"));
        Assert.False(Glob("a?c", "a/c"));
    }

    [Fact]
    public void CharacterClasses()
    {
        Assert.True(Glob("file[ab].txt", "filea.txt"));
        Assert.False(Glob("file[ab].txt", "filec.txt"));
        Assert.True(Glob("file[!ab].txt", "filec.txt"));
        Assert.False(Glob("file[!ab].txt", "filea.txt"));
        Assert.True(Glob("file[a-c].txt", "fileb.txt"));
    }

    [Fact]
    public void UnclosedClassAndBraceAreLiteral()
    {
        Assert.True(Glob("a[b", "a[b"));
        Assert.True(Glob("a{b", "a{b"));
        Assert.True(Glob("a[b/c]d", "a[b/c]d")); // a class holding '/' is literal
    }

    [Fact]
    public void BraceAlternatives()
    {
        Assert.True(Glob("*.{js,ts}", "x.ts"));
        Assert.True(Glob("*.{js,ts}", "x.js"));
        Assert.False(Glob("*.{js,ts}", "x.py"));
        Assert.True(Glob("{a,b{1,2}}.txt", "b2.txt"));
        Assert.True(Glob("{a,b{1,2}}.txt", "a.txt"));
        Assert.False(Glob("{a,b{1,2}}.txt", "b3.txt"));
        Assert.True(Glob("{src,test}/*.cs", "test/a.cs"));
    }

    [Fact]
    public void ABraceWithOneItemIsLiteral()
    {
        Assert.True(Glob("{single}.txt", "{single}.txt"));
        Assert.False(Glob("{single}.txt", "single.txt"));
    }

    [Fact]
    public void NumericRanges()
    {
        Assert.True(Glob("file{1..3}.txt", "file2.txt"));
        Assert.True(Glob("file{1..3}.txt", "file3.txt"));
        Assert.False(Glob("file{1..3}.txt", "file4.txt"));
        Assert.False(Glob("file{1..3}.txt", "file0.txt"));
        Assert.True(Glob("v{-2..2}", "v-1"));
        Assert.True(Glob("v{-2..2}", "v0"));
        Assert.False(Glob("v{-2..2}", "v-3"));
        Assert.True(Glob("{1..3}.{a,b}", "2.b"));
        Assert.True(Glob("f{3..1}", "f2")); // reversed bounds
    }

    [Fact]
    public void EscapesMakeSpecialCharactersLiteral()
    {
        Assert.True(Glob(@"a\*b", "a*b"));
        Assert.False(Glob(@"a\*b", "axb"));
        Assert.True(Glob(@"a\{b,c\}", "a{b,c}"));
    }

    [Fact]
    public void ALeadingSlashAnchorsToTheDirectory()
    {
        Assert.True(Glob("/a.txt", "a.txt"));
        Assert.False(Glob("/a.txt", "sub/a.txt"));
    }

    [Fact]
    public void ADotDoesNotMatchAnyCharacter()
    {
        Assert.False(Glob("a.b", "axb"));
    }

    // ── Resolving ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ResolvesAllTheProperties()
    {
        var p = Resolve("""
            root = true

            [*.cs]
            indent_style = space
            indent_size = 4
            end_of_line = crlf
            charset = utf-8-bom
            trim_trailing_whitespace = true
            insert_final_newline = false
            """, "/proj/src/a.cs");
        Assert.Equal(IndentStyle.Space, p.IndentStyle);
        Assert.Equal(4, p.IndentSize);
        Assert.Equal(4, p.TabWidth);
        Assert.Equal(EndOfLine.CrLf, p.EndOfLine);
        Assert.Equal("\r\n", p.LineBreak);
        Assert.Equal("utf-8-bom", p.Charset);
        Assert.True(p.TrimTrailingWhitespace);
        Assert.False(p.InsertFinalNewline);
    }

    [Fact]
    public void NothingMatchingGivesEmptyProperties()
    {
        var p = Resolve("[*.py]\nindent_size = 2\n", "/proj/a.cs");
        Assert.Null(p.IndentSize);
        Assert.Empty(p.Raw);
        Assert.Null(p.LineBreak);
        Assert.Equal(EditorConfigProperties.Empty.IndentStyle, p.IndentStyle);
    }

    [Fact]
    public void LaterSectionsOverrideEarlierOnes()
    {
        var p = Resolve("[*]\nindent_size = 2\ncharset = utf-8\n[*.cs]\nindent_size = 4\n", "/proj/a.cs");
        Assert.Equal(4, p.IndentSize);
        Assert.Equal("utf-8", p.Charset);
    }

    [Fact]
    public void NearerFilesOverrideFartherOnes()
    {
        var files = new Dictionary<string, string>
        {
            ["/proj"] = "root = true\n[*]\nindent_size = 2\nend_of_line = lf\n",
            ["/proj/src"] = "[*.cs]\nindent_size = 8\n",
        };
        var p = Fake(files).Resolve("/proj/src/a.cs");
        Assert.Equal(8, p.IndentSize);
        Assert.Equal(EndOfLine.Lf, p.EndOfLine);
    }

    [Fact]
    public void RootStopsTheSearch()
    {
        var files = new Dictionary<string, string>
        {
            ["/"] = "[*]\nindent_size = 9\n",
            ["/proj"] = "root = true\n[*]\nend_of_line = lf\n",
        };
        var p = Fake(files).Resolve("/proj/a.cs");
        Assert.Null(p.IndentSize);
        Assert.Equal(EndOfLine.Lf, p.EndOfLine);
        // Without root = true the parent contributes.
        files["/proj"] = "[*]\nend_of_line = lf\n";
        Assert.Equal(9, Fake(files).Resolve("/proj/a.cs").IndentSize);
    }

    [Fact]
    public void PathsInAFolderAreRelativeToItsConfig()
    {
        var files = new Dictionary<string, string> { ["/proj/src"] = "[sub/*.cs]\nindent_size = 3\n" };
        Assert.Equal(3, Fake(files).Resolve("/proj/src/sub/a.cs").IndentSize);
        Assert.Null(Fake(files).Resolve("/proj/src/other/sub/a.cs").IndentSize);
        Assert.Null(Fake(files).Resolve("/proj/lib/sub/a.cs").IndentSize);
    }

    [Fact]
    public void IndentSizeTabUsesTabWidth()
    {
        var p = Resolve("[*]\nindent_size = tab\ntab_width = 8\n", "/proj/a.txt");
        Assert.Equal(8, p.IndentSize);
        Assert.False(p.IndentSizeIsTab);
        var q = Resolve("[*]\nindent_size = tab\n", "/proj/a.txt");
        Assert.Null(q.IndentSize);
        Assert.True(q.IndentSizeIsTab);
    }

    [Fact]
    public void TabStyleWithoutASizeFollowsTabWidth()
    {
        var p = Resolve("[*]\nindent_style = tab\ntab_width = 4\n", "/proj/a.txt");
        Assert.Equal(4, p.IndentSize);
        var q = Resolve("[*]\nindent_style = tab\n", "/proj/a.txt");
        Assert.True(q.IndentSizeIsTab);
        // an explicit size wins
        Assert.Equal(2, Resolve("[*]\nindent_style = tab\nindent_size = 2\n", "/proj/a.txt").IndentSize);
    }

    [Fact]
    public void TabWidthDefaultsToTheIndentSize()
    {
        Assert.Equal(2, Resolve("[*]\nindent_size = 2\n", "/proj/a").TabWidth);
        Assert.Equal(8, Resolve("[*]\nindent_size = 2\ntab_width = 8\n", "/proj/a").TabWidth);
    }

    [Fact]
    public void UnsetRemovesAProperty()
    {
        var p = Resolve("[*]\nindent_size = 2\ncharset = utf-8\n[*.cs]\nindent_size = unset\n", "/proj/a.cs");
        Assert.Null(p.IndentSize);
        Assert.Equal("utf-8", p.Charset);
        Assert.False(p.Raw.ContainsKey("indent_size"));
    }

    [Fact]
    public void KeysAndKnownValuesAreCaseInsensitive()
    {
        var p = Resolve("[*]\nIndent_Style = SPACE\nEND_OF_LINE = CRLF\nTrim_Trailing_Whitespace = TRUE\n", "/proj/a");
        Assert.Equal(IndentStyle.Space, p.IndentStyle);
        Assert.Equal(EndOfLine.CrLf, p.EndOfLine);
        Assert.True(p.TrimTrailingWhitespace);
    }

    [Fact]
    public void InvalidValuesAreIgnored()
    {
        var p = Resolve("[*]\nindent_size = lots\nend_of_line = windows\ncharset = ebcdic\ntrim_trailing_whitespace = maybe\nindent_style = mixed\n", "/proj/a");
        Assert.Null(p.IndentSize);
        Assert.Null(p.EndOfLine);
        Assert.Null(p.Charset);
        Assert.Null(p.TrimTrailingWhitespace);
        Assert.Null(p.IndentStyle);
    }

    [Fact]
    public void CommentsBlankLinesAndColonSeparators()
    {
        var p = Resolve("# top\n; also a comment\n\nroot: true\n\n[*]\n# inner\nindent_size: 3\n  charset  =  utf-8  \n", "/proj/a");
        Assert.Equal(3, p.IndentSize);
        Assert.Equal("utf-8", p.Charset);
    }

    [Fact]
    public void WindowsLineEndingsInTheConfig()
    {
        var p = Resolve("root = true\r\n[*.cs]\r\nindent_size = 5\r\n", "/proj/a.cs");
        Assert.Equal(5, p.IndentSize);
    }

    [Fact]
    public void UnknownKeysAreKeptRaw()
    {
        var p = Resolve("[*]\ncsharp_new_line_before_open_brace = All\n", "/proj/a.cs");
        Assert.Equal("All", p.Raw["csharp_new_line_before_open_brace"]);
    }

    [Fact]
    public void ABrokenSectionHeaderIsSkipped()
    {
        var p = Resolve("[*.cs\nindent_size = 9\n[*.cs]\ncharset = utf-8\n", "/proj/a.cs");
        // properties after a broken header have no section to land in
        Assert.Null(p.IndentSize);
        Assert.Equal("utf-8", p.Charset);
    }

    [Fact]
    public void ParsedFilesAreCachedUntilInvalidated()
    {
        var reads = new List<string>();
        var files = new Dictionary<string, string> { ["/proj"] = "[*]\nindent_size = 2\n" };
        var resolver = Fake(files, reads);
        resolver.Resolve("/proj/a.cs");
        var first = reads.Count;
        resolver.Resolve("/proj/b.cs");
        Assert.Equal(first, reads.Count);
        files["/proj"] = "[*]\nindent_size = 6\n";
        Assert.Equal(2, resolver.Resolve("/proj/a.cs").IndentSize);
        resolver.Invalidate("/proj");
        Assert.Equal(6, resolver.Resolve("/proj/a.cs").IndentSize);
        resolver.Invalidate();
        Assert.Equal(6, resolver.Resolve("/proj/a.cs").IndentSize);
    }

    [Fact]
    public void WindowsStylePaths()
    {
        var files = new Dictionary<string, string> { [@"C:\proj"] = "[src/*.cs]\nindent_size = 7\n" };
        // Path.GetDirectoryName only splits backslashes on Windows; the resolver itself normalises the separators it compares.
        if (OperatingSystem.IsWindows()) Assert.Equal(7, Fake(files).Resolve(@"C:\proj\src\a.cs").IndentSize);
    }

    [Fact]
    public void AppliesToEditOptions()
    {
        var spaces = Resolve("[*]\nindent_style = space\nindent_size = 2\n", "/proj/a").Apply(EditOptions.Default);
        Assert.True(spaces.InsertSpaces);
        Assert.Equal(2, spaces.TabSize);
        Assert.Equal("  ", spaces.IndentUnit);

        var tabs = Resolve("[*]\nindent_style = tab\ntab_width = 8\n", "/proj/a").Apply(EditOptions.Default);
        Assert.False(tabs.InsertSpaces);
        Assert.Equal(8, tabs.TabSize);

        var none = EditorConfigProperties.Empty.Apply(EditOptions.Default);
        Assert.Equal(EditOptions.Default.TabSize, none.TabSize);
        Assert.Equal(EditOptions.Default.InsertSpaces, none.InsertSpaces);
    }

    [Fact]
    public void TheDiskDefaultReadsRealFilesAndIgnoresMissingOnes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wicked-editorconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, ".editorconfig");
            File.WriteAllText(configPath, "root = true\n[*.txt]\nindent_size = 3\n");
            var resolver = new EditorConfigResolver(d => File.Exists(Path.Combine(d, ".editorconfig")) ? File.ReadAllText(Path.Combine(d, ".editorconfig")) : null);
            Assert.Equal(3, resolver.Resolve(Path.Combine(dir, "x.txt")).IndentSize);
            Assert.Null(EditorConfigResolver.Disk.Resolve(Path.Combine(dir, "nonexistent-sub", "y.py")).Charset);
            Assert.Equal(3, EditorConfigResolver.Disk.Resolve(Path.Combine(dir, "x.txt")).IndentSize);
        }
        finally
        {
            File.Delete(Path.Combine(dir, ".editorconfig"));
            Directory.Delete(dir);
        }
    }
}
