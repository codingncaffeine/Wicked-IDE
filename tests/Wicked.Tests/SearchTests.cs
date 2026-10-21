using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Search;
using Wicked.Core.Files;
using Wicked.Core.Search;

namespace Wicked.Tests;

/// <summary>Searching a folder: what is found, where, which files are looked at, and what a replacement writes.</summary>
public sealed class ProjectSearchTests : IDisposable
{
    private readonly TempProfile _profile = new();

    public void Dispose() => _profile.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Folder()
    {
        var root = Path.Combine(_profile.Root, "p");
        Directory.CreateDirectory(Path.Combine(root, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(root, "node_modules", "lib"));
        File.WriteAllText(Path.Combine(root, "src", "a.cs"), "class Total\n{\n    int total = 1;\n    int subtotal = total + 2;\n}\n");
        File.WriteAllText(Path.Combine(root, "src", "deep", "b.ts"), "let total = 0;\r\nexport { total };\r\n");
        File.WriteAllText(Path.Combine(root, "readme.md"), "Total of totals.\n");
        File.WriteAllText(Path.Combine(root, "node_modules", "lib", "x.js"), "total\n");
        File.WriteAllBytes(Path.Combine(root, "src", "blob.bin"), [0, 1, 2, (byte)'t', (byte)'o', (byte)'t', (byte)'a', (byte)'l', 0, 0]);
        return root;
    }

    private async Task<(List<FileMatches> Found, SearchSummary Summary)> Search(string root, ProjectQuery query, IReadOnlyDictionary<string, Func<string>>? open = null)
    {
        var index = FileIndex.Build(root, Ct);
        var found = new List<FileMatches>();
        var summary = await ProjectSearch.RunAsync(root, index.Files, query, open ?? new Dictionary<string, Func<string>>(), f =>
        {
            lock (found) found.Add(f);
        }, Ct);
        return ([.. found.OrderBy(f => f.Path, StringComparer.Ordinal)], summary);
    }

    [Fact]
    public async Task ALiteralSearchFindsEveryMatchWithItsLineColumnAndPreviewAndSkipsIgnoredAndBinaryFiles()
    {
        var root = Folder();
        var (found, summary) = await Search(root, new ProjectQuery("total"));
        Assert.Equal(["readme.md", "a.cs", "b.ts"], found.Select(f => Path.GetFileName(f.Path)).OrderBy(n => n == "readme.md" ? 0 : n == "a.cs" ? 1 : 2));
        var a = found.Single(f => f.Path.EndsWith("a.cs", StringComparison.Ordinal)).Matches;
        Assert.Equal([(0, 6), (2, 8), (3, 11), (3, 19)], a.Select(m => (m.Line, m.Column)));      // Total, total, subTOTAL, total
        Assert.Equal("int subtotal = total + 2;", a[2].Preview.Trim());
        var ts = found.Single(f => f.Path.EndsWith("b.ts", StringComparison.Ordinal)).Matches;
        Assert.Equal([(0, 4), (1, 9)], ts.Select(m => (m.Line, m.Column)));                    // CRLF lines counted right
        Assert.DoesNotContain(found, f => f.Path.Contains("node_modules", StringComparison.Ordinal) || f.Path.EndsWith(".bin", StringComparison.Ordinal));
        Assert.Equal(8, summary.Matches);
    }

    [Fact]
    public async Task CaseWholeWordsAndRegularExpressionsNarrowIt()
    {
        var root = Folder();
        Assert.Equal(2, (await Search(root, new ProjectQuery("Total", MatchCase: true))).Summary.Matches);
        Assert.Equal(6, (await Search(root, new ProjectQuery("total", WholeWord: true))).Summary.Matches);       // Total and total, not subtotal nor totals
        var (found, _) = await Search(root, new ProjectQuery(@"int \w+ =", Regex: true));
        Assert.Equal(2, found.Single().Matches.Count);
        var bad = await Search(root, new ProjectQuery("(unclosed", Regex: true));
        Assert.NotNull(bad.Summary.Error);
    }

    [Fact]
    public async Task IncludeAndExcludeGlobsChooseTheFiles()
    {
        var root = Folder();
        Assert.Equal(["b.ts"], (await Search(root, new ProjectQuery("total", Include: "*.ts"))).Found.Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(["a.cs", "b.ts"], (await Search(root, new ProjectQuery("total", Include: "src"))).Found.Select(f => Path.GetFileName(f.Path)).Order());
        Assert.Equal(["a.cs", "readme.md"], (await Search(root, new ProjectQuery("total", Exclude: "**/deep/**"))).Found.Select(f => Path.GetFileName(f.Path)).Order());
        Assert.Equal(["a.cs", "b.ts"], (await Search(root, new ProjectQuery("total", Include: "*.{cs,ts}"))).Found.Select(f => Path.GetFileName(f.Path)).Order());
    }

    [Fact]
    public async Task AnOpenFileIsSearchedAsItIsInItsEditor()
    {
        var root = Folder();
        var path = Path.Combine(root, "readme.md");
        var (found, _) = await Search(root, new ProjectQuery("unsaved"), new Dictionary<string, Func<string>> { [path] = () => "unsaved words here" });
        Assert.Equal(path, found.Single().Path);
    }

    [Fact]
    public async Task ReplacingOnDiskFillsInGroupsAndKeepsTheLineBreaksAndOnlyTouchesWhatWasAskedFor()
    {
        var root = Folder();
        var ts = Path.Combine(root, "src", "deep", "b.ts");
        var count = await ProjectSearch.ReplaceInFileAsync(ts, new ProjectQuery(@"let (\w+)", Regex: true), "const $1", null, Ct);
        Assert.Equal(1, count);
        Assert.Equal("const total = 0;\r\nexport { total };\r\n", File.ReadAllText(ts));

        var cs = Path.Combine(root, "src", "a.cs");
        var text = File.ReadAllText(cs);
        var second = text.IndexOf("= total", StringComparison.Ordinal) + 2;
        Assert.Equal(1, await ProjectSearch.ReplaceInFileAsync(cs, new ProjectQuery("total", MatchCase: true, WholeWord: true), "sum", new HashSet<int> { second }, Ct));
        Assert.Equal("class Total\n{\n    int total = 1;\n    int subtotal = sum + 2;\n}\n", File.ReadAllText(cs));
    }

    [Theory]
    [InlineData("*.ts", "src/deep/b.ts", true)]
    [InlineData("*.ts", "b.ts.bak", false)]
    [InlineData("src", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "src/deep/a.cs", false)]
    [InlineData("src/**/*.cs", "src/deep/a.cs", true)]
    [InlineData("**/deep/**", "src/deep/b.ts", true)]
    [InlineData("a?.cs", "src/ab.cs", true)]
    [InlineData("[ab].cs", "src/b.cs", true)]
    [InlineData("*.{cs,ts}", "x/y.ts", true)]
    [InlineData("*.{cs,ts}", "x/y.js", false)]
    [InlineData("readme.md, *.ts", "readme.md", true)]
    public void GlobsMatchAsVsCodesSearchBoxesDo(string glob, string path, bool matches) => Assert.Equal(matches, GlobSet.Parse(glob).Matches(path));
}

/// <summary>The Search panel in the window: typed, found, opened, replaced.</summary>
public sealed class SearchWindowTests
{
    [Fact]
    public async Task CtrlShiftFSearchesTheFolderAndAClickOpensTheMatchSelected()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.txt"), "alpha\nneedle here\n");
        File.WriteAllText(Path.Combine(root, "two.txt"), "a needle and a needle\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.Index?.Files.Length == 2, 10), "the folder was not indexed");
            Input.Chord(window, Key.F, RawInputModifiers.Control | RawInputModifiers.Shift);
            Assert.True(shell.IsSearch);
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SearchQuery");
            Assert.True(Ui.PumpUntil(() => box.IsFocused, 5), "the query box did not take the keyboard");
            window.KeyTextInput("needle");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            var search = shell.Search;
            Assert.True(Ui.PumpUntil(() => search.MatchCount == 3 && !search.IsSearching, 10), $"found {search.MatchCount}: {search.Summary}");
            Assert.Equal("3 results in 2 files", search.Summary);

            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SearchResults");
            var first = search.Rows.OfType<SearchMatchRow>().First(r => r.File.Name == "one.txt");
            list.SelectedItem = first;
            Ui.Pump(0.1);
            var item = list.ContainerFromItem(first)!;
            Input.Click(window, (Control)item);
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Title == "one.txt", 5), "the file did not open");
            var doc = shell.Workbench.ActiveTab!.Document;
            Assert.Equal("needle", doc.Text.Substring(doc.Primary.Start, doc.Primary.Length));
            Assert.Equal(1, doc.Text.LineOf(doc.Primary.Start));
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task ReplaceAllAsksOnceThenChangesOpenFilesInTheirEditorsAndTheRestOnDisk()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "open.txt"), "old one\n");
        File.WriteAllText(Path.Combine(root, "closed.txt"), "old two old\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "open.txt");
            Assert.True(Ui.PumpUntil(() => shell.Workbench.Index?.Files.Length == 2, 10));
            Input.Chord(window, Key.H, RawInputModifiers.Control | RawInputModifiers.Shift);
            var search = shell.Search;
            Assert.True(search.ShowReplace);
            search.Query = "old";
            search.Replace = "new";
            Assert.True(Ui.PumpUntil(() => search.MatchCount == 3 && !search.IsSearching, 10), search.Summary);
            Assert.All(search.Rows.OfType<SearchMatchRow>(), r => Assert.Equal("new", r.Replacement));

            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SearchReplaceAll"));
            var confirm = window.FindControl<Button>("DialogSave")!;
            Assert.True(Ui.PumpUntil(() => confirm.IsEffectivelyVisible, 5), "Replace All did not ask");
            Assert.Equal("Replace", confirm.Content);
            Input.Click(window, confirm);
            Assert.True(Ui.PumpUntil(() => File.ReadAllText(Path.Combine(root, "closed.txt")) == "new two new\n", 10), "the closed file was not written");
            Assert.Equal("new one\n", tab.Document.Text.ToString());
            Assert.True(tab.IsDirty);                                               // changed in its editor, not behind its back
            Assert.Equal("old one\n", File.ReadAllText(Path.Combine(root, "open.txt")));
            Assert.True(Ui.PumpUntil(() => search.MatchCount == 0 && !search.IsSearching, 10));
            window.Close();
            return 0;
        });
    }
}
