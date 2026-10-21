using Avalonia.Controls;
using Wicked.App.Terminal;
using Wicked.Core;
using Wicked.Core.Settings;
using Wicked.Core.Terminal;

namespace Wicked.Tests.Terminal;

public sealed class TerminalPanelTests
{
    private static readonly ShellProfile Sh = new("sh", "/bin/sh", [], ShellKind.Other);

    private sealed class Env : IDisposable
    {
        public Env()
        {
            Dir = new ScratchDir();
            Paths = WickedPaths.Resolve(v => v == WickedPaths.HomeVariable ? Dir.Root : null);
            Paths.EnsureCreated();
        }

        public ScratchDir Dir { get; }

        public WickedPaths Paths { get; }

        public void Dispose() => Dir.Dispose();
    }

    private static TerminalPanelViewModel Panel(Env env, params ShellProfile[] profiles)
    {
        // The shells here start with the scratch folder as their home, so no start-up file of the user's is read.
        Environment.SetEnvironmentVariable("ENV", Path.Combine(env.Dir.Root, "none"));
        return new TerminalPanelViewModel(env.Paths, env.Dir.Root, profiles.Length == 0 ? [Sh] : profiles);
    }

    private static string ScreenOf(TerminalInstanceViewModel t)
    {
        var frame = t.Pane?.View.Frame;
        if (frame is null) return "";
        return string.Join('\n', frame.Lines.Select(row => string.Concat(row.Cells.Where(c => (c.Flags & CellFlags.WideTail) == 0).Select(c => frame.TextOf(c))).TrimEnd())).TrimEnd();
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ANewTerminalStartsAShellAndGetsAPane()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var added = new List<TerminalInstanceViewModel>();
            panel.TerminalAdded += added.Add;
            var terminal = panel.NewTerminal()!;
            Assert.Single(panel.Groups);
            Assert.Same(terminal, panel.ActiveTerminal);
            Assert.Equal("sh", terminal.Title);
            Assert.Single(added);
            Assert.True(Ui.PumpUntil(() => terminal.Pane is not null && terminal.Session is not null, 20));
            Assert.Equal(env.Dir.Root, terminal.WorkingDirectory);
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TextRunInTheActiveTerminalIsExecutedThere()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            // No terminal yet: running something opens one.
            var run = panel.RunInActiveTerminal("echo from-the-editor-$((6*7))");
            Assert.True(Ui.PumpUntil(() => run.IsCompleted, 20));
            var terminal = panel.ActiveTerminal!;
            Assert.True(Ui.PumpUntil(() => ScreenOf(terminal).Contains("from-the-editor-42", StringComparison.Ordinal), 20), ScreenOf(terminal));
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task SeveralLinesAreRunAsOneBlock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Session is not null, 20));
            var run = panel.RunInActiveTerminal("echo first-line\necho second-line\n");
            Assert.True(Ui.PumpUntil(() => run.IsCompleted, 20));
            Assert.True(Ui.PumpUntil(() => ScreenOf(terminal).Contains("second-line\nsecond-line", StringComparison.Ordinal) || (ScreenOf(terminal).Contains("first-line", StringComparison.Ordinal) && ScreenOf(terminal).Contains("second-line", StringComparison.Ordinal)), 20), ScreenOf(terminal));
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ASplitSitsBesideTheActiveTerminalInTheSameTab()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var first = panel.NewTerminal()!;
            var second = panel.Split()!;
            Assert.Single(panel.Groups);
            Assert.Equal([first, second], panel.Groups[0].Terminals);
            Assert.Same(second, panel.ActiveTerminal);
            panel.CycleSplit(-1);
            Assert.Same(first, panel.ActiveTerminal);
            var third = panel.NewTerminal()!;
            Assert.Equal(2, panel.Groups.Count);
            panel.CycleGroup(1);
            Assert.Same(first, panel.Groups[0].Terminals[0]);
            Assert.NotSame(third, panel.ActiveTerminal);
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ClosingATerminalKillsItsShellAndClosesTheTabWhenItWasTheLast()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        var pid = 0;
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var closed = false;
            panel.AllClosed += () => closed = true;
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Session is not null, 20));
            pid = terminal.Session!.ProcessId;
            Assert.True(Directory.Exists(Path.Combine(Path.DirectorySeparatorChar + "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            panel.KillActive();
            Assert.Empty(panel.Groups);
            Assert.Null(panel.ActiveTerminal);
            Assert.True(closed);
            return 0;
        });
        var until = DateTime.UtcNow.AddSeconds(5);
        var proc = Path.Combine(Path.DirectorySeparatorChar + "proc", pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        while (Directory.Exists(proc) && DateTime.UtcNow < until) await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.False(Directory.Exists(proc));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AShellThatEndsNormallyTakesItsTabWithItAndOneThatFailsStays()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Session is not null, 20));
            panel.SendText("exit 0\r");
            Assert.True(Ui.PumpUntil(() => panel.Groups.Count == 0, 20));

            var failing = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => failing.Session is not null && failing.Pane is not null, 20));
            panel.SendText("exit 3\r");
            Assert.True(Ui.PumpUntil(() => failing.ExitCode == 3, 20));
            Assert.True(Ui.PumpUntil(() => ScreenOf(failing).Contains("exited with code 3", StringComparison.Ordinal), 10), ScreenOf(failing));
            Assert.Single(panel.Groups);
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ATerminalCanBeRenamedAndFollowsItsTitle()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Session is not null, 20));
            panel.SendText("printf '\\033]0;build output\\007'\r");
            Assert.True(Ui.PumpUntil(() => terminal.Title == "build output", 20));
            Assert.Equal("build output", panel.ActiveGroup!.Title);
            panel.Rename(terminal, "my build");
            Assert.Equal("my build", terminal.Title);
            panel.Rename(terminal, "  ");
            Assert.Equal("build output", terminal.Title);
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheWorkingDirectoryTheShellReportsIsTracked()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Session is not null, 20));
            panel.SendText("printf '\\033]7;file:///srv/app\\007'\r");
            Assert.True(Ui.PumpUntil(() => terminal.WorkingDirectory == "/srv/app", 20));
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task SettingsReachOpenAndNewTerminals()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Pane is not null, 20));
            var view = terminal.Pane!.View;
            Assert.Equal(CursorShape.Block, view.CursorStyle);
            panel.ApplySettings(new WickedSettings { TerminalCursorStyle = "line", TerminalCursorBlinking = true, TerminalCopyOnSelection = true });
            Assert.Equal(CursorShape.Bar, view.CursorStyle);
            Assert.True(view.CursorBlinking);
            Assert.True(view.CopyOnSelection);
            panel.ApplySettings(new WickedSettings { TerminalCursorStyle = "underline" });
            Assert.Equal(CursorShape.Underline, view.CursorStyle);
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ChangingTheFontSizeChangesTheCellSize()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Pane is not null, 20));
            var before = terminal.Pane!.View.Metrics;
            panel.ApplySettings(new WickedSettings { TerminalFontSize = 20 });
            Assert.True(Ui.PumpUntil(() => terminal.Pane.View.Metrics.Width > before.Width, 20));
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheDefaultProfileFollowsTheSettingAndTheShellsList()
    {
        using var env = new Env();
        await Ui.Run(() =>
        {
            var fish = new ShellProfile("fish", "/usr/bin/fish", [], ShellKind.Fish);
            using var panel = Panel(env, Sh, fish);
            Assert.Equal("sh", panel.DefaultProfile!.Name);
            panel.ApplySettings(new WickedSettings { TerminalProfile = "fish" });
            Assert.Equal("fish", panel.DefaultProfile!.Name);
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AShellThatCannotBeStartedSaysSo()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts a program");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env, new ShellProfile("nothing", "/no/such/shell", [], ShellKind.Other));
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.StartError is not null, 20));
            Assert.Contains("could not start", terminal.StartError, StringComparison.Ordinal);
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task FollowingALinkOpensTheFileOnlyWhenItExists()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        var src = Path.Combine(env.Dir.Root, "src");
        Directory.CreateDirectory(src);
        var file = Path.Combine(src, "app.cs");
        await File.WriteAllTextAsync(file, "class A {}", TestContext.Current.CancellationToken);
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var opened = new List<TerminalFileTarget>();
            var urls = new List<string>();
            panel.OpenFileRequested += opened.Add;
            panel.OpenUrlRequested += urls.Add;
            var terminal = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => terminal.Pane is not null, 20));
            var view = terminal.Pane!.View;
            view.ActivateLink(new TerminalLinkActivation(LinkKind.Path, "src/app.cs", 12, 3));
            view.ActivateLink(new TerminalLinkActivation(LinkKind.Path, "src/missing.cs", 1, null));
            view.ActivateLink(new TerminalLinkActivation(LinkKind.Url, "https://example.com/", null, null));
            Assert.True(Ui.PumpUntil(() => opened.Count == 1 && urls.Count == 1, 20));
            Assert.Equal(new TerminalFileTarget(file, 12, 3), opened[0]);
            Assert.Equal(["https://example.com/"], urls);
            return 0;
        });
    }

    [Theory]
    [InlineData("src/app.cs", true)]
    [InlineData("./src/app.cs", true)]
    [InlineData("src/nope.cs", false)]
    [InlineData("", false)]
    public void PathsAreResolvedFromTheTerminalsFolder(string written, bool exists)
    {
        using var dir = new ScratchDir();
        Directory.CreateDirectory(Path.Combine(dir.Root, "src"));
        File.WriteAllText(Path.Combine(dir.Root, "src", "app.cs"), "x");
        var resolved = TerminalPanelViewModel.ResolvePath(written, dir.Root);
        Assert.Equal(exists, resolved is not null);
        if (exists) Assert.Equal(Path.GetFullPath(Path.Combine(dir.Root, "src", "app.cs")), resolved);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ThePanelViewShowsTabsAndThePanes()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "starts /bin/sh");
        using var env = new Env();
        await Ui.Run(() =>
        {
            using var panel = Panel(env);
            var view = new TerminalPanelView { DataContext = panel };
            var window = new Window { Width = 900, Height = 500, Content = view };
            window.Show();
            var first = panel.NewTerminal()!;
            Assert.True(Ui.PumpUntil(() => first.Pane is not null && first.Pane.Parent is not null, 20));
            panel.Split();
            Assert.True(Ui.PumpUntil(() => panel.ActiveGroup!.Terminals.All(t => t.Pane?.Parent is not null), 20));
            var panes = view.GetLogicalDescendants().OfType<TerminalPane>().ToList();
            Assert.Equal(2, panes.Count);
            panel.NewTerminal();
            Assert.True(Ui.PumpUntil(() => panel.ActiveTerminal?.Pane?.Parent is not null, 20));
            Assert.Single(view.GetLogicalDescendants().OfType<TerminalPane>());
            window.Close();
            return 0;
        });
    }
}

internal static class LogicalExtensions
{
    public static IEnumerable<Avalonia.LogicalTree.ILogical> GetLogicalDescendants(this Avalonia.LogicalTree.ILogical root)
    {
        foreach (var child in root.LogicalChildren)
        {
            yield return child;
            foreach (var d in child.GetLogicalDescendants()) yield return d;
        }
    }
}
