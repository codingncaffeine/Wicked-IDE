using Wicked.App.Extensions;
using Wicked.Core;
using Wicked.Core.Extensions;

namespace Wicked.Tests.Extensions;

public sealed class InitScriptTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public Rig(ScratchFolder scratch)
        {
            Paths = WickedPaths.Resolve(v => v == WickedPaths.HomeVariable ? scratch.Sub("home") : null);
            Directory.CreateDirectory(Paths.Config);
            Notices = new NoticeBoard();
            Bridge = new FakeBridge();
            Script = new InitScript(Paths, Bridge, Notices, new ExtensionRegistry());
        }

        public WickedPaths Paths { get; }

        public NoticeBoard Notices { get; }

        public FakeBridge Bridge { get; }

        public InitScript Script { get; }

        public void Write(string text) => File.WriteAllText(Script.Path, text);

        public InitScript Fresh() => new(Paths, Bridge, Notices, new ExtensionRegistry());

        public ValueTask DisposeAsync() => Script.DisposeAsync();
    }

    [Fact]
    public async Task AScriptRegistersACommandThatRuns()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("""
            wicked.Commands.Register("init.hi", "Say Hi", () => wicked.Notices.Post("hi from init.csx", NoticeLevel.Success, "hi"), "Ctrl+Alt+I", "Mine");
            var files = Directory.GetFiles(wicked.ExtensionFolder).Length;
            """);

        await rig.Script.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(rig.Script.Succeeded, string.Join('\n', rig.Bridge.Output));
        Assert.Equal("Mine: Say Hi", rig.Bridge.Commands["init.hi"].Title);
        await rig.Bridge.RunCommandAsync("init.hi");
        Assert.Equal("hi from init.csx", rig.Notices.Top?.Text);
        Assert.Equal(1, rig.Script.Compilations);
    }

    [Fact]
    public async Task ReloadingTakesBackWhatTheLastRunRegisteredAndUsesTheNewScript()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("""wicked.Commands.Register("init.one", "One", () => { });""");
        await rig.Script.RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains("init.one", rig.Bridge.CommandIds());

        rig.Write("""wicked.Commands.Register("init.two", "Two", () => { });""");
        await rig.Script.RunAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("init.one", rig.Bridge.CommandIds());
        Assert.Contains("init.two", rig.Bridge.CommandIds());
    }

    [Fact]
    public async Task ACompileErrorShowsItsLineInTheOutputChannelAndPostsANotice()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("// line one\nvar fine = 1;\nvar broken = ;\nvar later = 2;\n");

        await rig.Script.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(rig.Script.Succeeded);
        var line = Assert.Single(rig.Bridge.Output, l => l.StartsWith("init.csx: init.csx(3,", StringComparison.Ordinal));
        Assert.Contains("error CS", line, StringComparison.Ordinal);
        var notice = Assert.Single(rig.Notices.All, n => n.Key == "init.csx");
        Assert.Equal(NoticeKind.Error, notice.Kind);
        Assert.Equal("output/init.csx", notice.Route);
        Assert.Contains("error", notice.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARuntimeErrorShowsTheScriptLine()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("var a = 1;\nvar b = 2;\nthrow new InvalidOperationException(\"the script broke\");\n");

        await rig.Script.RunAsync(TestContext.Current.CancellationToken);

        Assert.False(rig.Script.Succeeded);
        var text = string.Join('\n', rig.Bridge.Output);
        Assert.Contains("the script broke", text, StringComparison.Ordinal);
        Assert.Contains("init.csx:line 3", text, StringComparison.Ordinal);
        Assert.Contains(rig.Notices.All, n => n.Key == "init.csx" && n.Text.Contains("the script broke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnchangedScriptIsNotCompiledAgainEvenInANewProcessSession()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("""wicked.Commands.Register("init.cached", "Cached", () => { });""");
        await rig.Script.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, rig.Script.Compilations);

        await rig.Script.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, rig.Script.Compilations);

        // A second runner on the same profile, as after a restart: it finds the build in the cache folder.
        await rig.Script.DisposeAsync();
        var again = rig.Fresh();
        await again.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, again.Compilations);
        Assert.Contains("init.cached", rig.Bridge.CommandIds());
        await again.DisposeAsync();
        Assert.Single(Directory.GetFiles(Path.Combine(rig.Paths.Cache, "init-csx"), "*.dll"));
    }

    [Fact]
    public async Task EditingTheScriptCompilesItAgain()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("""wicked.Commands.Register("init.a", "A", () => { });""");
        await rig.Script.RunAsync(TestContext.Current.CancellationToken);
        rig.Write("""wicked.Commands.Register("init.b", "B", () => { });""");
        await rig.Script.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, rig.Script.Compilations);
    }

    [Fact]
    public async Task SavingTheScriptInTheEditorRunsItAgain()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("""wicked.Commands.Register("init.v1", "V1", () => { });""");
        await rig.Script.RunAsync(TestContext.Current.CancellationToken);
        Assert.Contains("init.v1", rig.Bridge.CommandIds());

        rig.Write("""wicked.Commands.Register("init.v2", "V2", () => { });""");
        rig.Bridge.RaiseSaved(new EditorSnapshot("/elsewhere.txt", "/elsewhere.txt", null, 1, "", [], false));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Contains("init.v1", rig.Bridge.CommandIds()); // another file saved: nothing happens

        rig.Bridge.RaiseSaved(new EditorSnapshot(rig.Script.Path, rig.Script.Path, "csharp", 2, "", [], false));
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!rig.Bridge.CommandIds().Contains("init.v2") && DateTime.UtcNow < until) await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Contains("init.v2", rig.Bridge.CommandIds());
        Assert.DoesNotContain("init.v1", rig.Bridge.CommandIds());
    }

    [Fact]
    public async Task NoScriptMeansNothingRunsAndNothingIsReported()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);

        await rig.Script.RunAsync(TestContext.Current.CancellationToken);

        Assert.True(rig.Script.Succeeded);
        Assert.Empty(rig.Bridge.Output);
        Assert.Empty(rig.Notices.All);
        Assert.Equal(0, rig.Script.Compilations);
    }

    [Fact]
    public async Task TheStarterFileIsWrittenOnceAndRunsCleanly()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);

        var path = await rig.Script.EnsureStarterAsync(TestContext.Current.CancellationToken);
        File.AppendAllText(path, "// my own line\n");
        var again = await rig.Script.EnsureStarterAsync(TestContext.Current.CancellationToken);
        await rig.Script.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(path, again);
        Assert.Contains("my own line", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.Contains("wicked.Commands.Register", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.True(rig.Script.Succeeded, string.Join('\n', rig.Bridge.Output));
    }

    [Fact]
    public async Task AScriptCanUseTheEditorApiAndAvaloniaTypes()
    {
        using var scratch = new ScratchFolder();
        await using var rig = new Rig(scratch);
        rig.Write("""
            wicked.Commands.Register("init.mark", "Mark", async ct =>
            {
                var document = await wicked.Editors.GetActiveAsync();
                if (document is null) return;
                await wicked.Editors.SetDecorationsAsync(document.Id, "init", new Decorations(Ranges: new[] { new RangeMark(0, 3, MarkStyle.Info) }));
            });
            wicked.Views.AddBottomTabAsync("panel", "Mine", () => new TextBlock { Text = "hello" });
            """);
        rig.Bridge.Open.Add(new EditorSnapshot("/a.txt", "/a.txt", "plaintext", 1, "hello", [new TextRange(0, 0)], false));

        await rig.Script.RunAsync(TestContext.Current.CancellationToken);
        Assert.True(rig.Script.Succeeded, string.Join('\n', rig.Bridge.Output));
        await rig.Bridge.RunCommandAsync("init.mark");

        var (owner, document, layer, marks) = Assert.Single(rig.Bridge.Decorated);
        Assert.Equal(("init.csx", "/a.txt", "init"), (owner, document, layer));
        Assert.Equal(MarkStyle.Info, marks.Ranges![0].Style);
    }
}
