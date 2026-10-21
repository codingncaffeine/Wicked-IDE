using System.Net.Sockets;
using Wicked.App.Shell;

namespace Wicked.Tests;

/// <summary>The command line (wicked ., wicked FILE, -g, -n, -w), one Wicked per profile, New Window and drops.</summary>
public sealed class LaunchTests
{
    private static LaunchRequest? Parse(params string[] args) =>
        LaunchRequest.Parse(args, OperatingSystem.IsWindows() ? @"C:\work" : "/work", path => path.EndsWith("dir", StringComparison.Ordinal), out _);

    [Fact]
    public void ArgumentsAreReadAsVSCodeReadsThem()
    {
        var sep = Path.DirectorySeparatorChar;
        var work = OperatingSystem.IsWindows() ? @"C:\work" : "/work";
        var request = Parse("notes.md", "somedir", "-g", "src/main.cs:12:5", "-g", "a.txt:3")!;
        Assert.Equal([$"{work}{sep}somedir"], request.Folders);
        Assert.Equal(
        [
            new OpenedFile($"{work}{sep}notes.md"),
            new OpenedFile(Path.GetFullPath("src/main.cs", work), 12, 5),
            new OpenedFile($"{work}{sep}a.txt", 3),
        ], request.Files);
        Assert.False(request.NewWindow || request.Wait);

        Assert.True(Parse("-n")!.NewWindow);
        Assert.True(Parse("--wait", "COMMIT_EDITMSG")!.Wait);
        Assert.True(Parse()!.IsEmpty);

        // Without -g a colon is part of the name; an unknown option and --wait with nothing to wait for are errors.
        Assert.Equal(new OpenedFile($"{work}{sep}a.txt:3"), Parse("a.txt:3")!.Files.Single());
        Assert.Null(LaunchRequest.Parse(["--frobnicate"], work, _ => false, out var unknown));
        Assert.Contains("--frobnicate", unknown, StringComparison.Ordinal);
        Assert.Null(LaunchRequest.Parse(["--wait"], work, _ => false, out var nothing));
        Assert.Contains("--wait", nothing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALaunchIsHandedToTheRunningWicked()
    {
        using var profile = new TempProfile();
        var asked = new List<LaunchRequest>();
        var closed = new TaskCompletionSource();
        using (var running = SingleInstance.Listen(profile.Paths, request =>
        {
            lock (asked) asked.Add(request);
            var answer = request.Folders.Count > 0 ? LaunchAnswer.OpenHere : LaunchAnswer.Opened;
            return Task.FromResult<(LaunchAnswer, Task?)>((answer, request.Wait ? closed.Task : null));
        })!)
        {
            Assert.NotNull(running);
            var file = new LaunchRequest([], [new OpenedFile("/x/a.txt", 3)], false, false);
            Assert.Equal((0, false), await SingleInstance.HandOffAsync(profile.Paths, file, TimeSpan.FromSeconds(5)));
            Assert.Equal(file.Files, asked.Single().Files);

            // A folder the running window does not have: the launch is told to open a window of its own.
            var folder = new LaunchRequest(["/elsewhere"], [], false, false);
            Assert.Equal((null, true), await SingleInstance.HandOffAsync(profile.Paths, folder, TimeSpan.FromSeconds(5)));

            // --wait: the launch is held until the running window says the files are closed.
            var waiting = SingleInstance.HandOffAsync(profile.Paths, new LaunchRequest([], [new OpenedFile("/x/msg")], false, true), TimeSpan.FromSeconds(5));
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.False(waiting.IsCompleted);
            closed.SetResult();
            Assert.Equal((0, false), await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }

        // Nobody listening now: the launch becomes the window.
        Assert.Equal((null, false), await SingleInstance.HandOffAsync(profile.Paths, LaunchRequest.Empty, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task ASocketLeftBehindIsTakenOver()
    {
        using var profile = new TempProfile();
        var path = SingleInstance.SocketPath(profile.Paths);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A socket file nobody listens on, as a Wicked that was killed leaves it (bound, never listening; .NET would remove
        // the file if this socket were closed, which a killed process never does).
        using var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        dead.Bind(new UnixDomainSocketEndPoint(path));
        Assert.True(File.Exists(path));
        Assert.Equal((null, false), await SingleInstance.HandOffAsync(profile.Paths, LaunchRequest.Empty, TimeSpan.FromSeconds(2)));
        using var running = SingleInstance.Listen(profile.Paths, _ => Task.FromResult<(LaunchAnswer, Task?)>((LaunchAnswer.Opened, null)));
        Assert.NotNull(running);
        Assert.Equal((0, false), await SingleInstance.HandOffAsync(profile.Paths, LaunchRequest.Empty, TimeSpan.FromSeconds(5)));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AWindowOpensWhatALaunchNamesAndWaitsForItsFiles()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        var other = Path.Combine(profile.Root, "other");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(root, "a.txt"), "one\ntwo\nthree\n");
        await Ui.Run(() =>
        {
            // Started with a folder and a file at a line: that folder, not the last session's, and the caret on the line.
            var (window, shell) = Ui.Start(profile.Paths);
            var started = shell.StartAsync(new LaunchRequest([root], [new OpenedFile(Path.Combine(root, "a.txt"), 2, 3)], false, false));
            Assert.True(Ui.PumpUntil(() => started.IsCompleted && shell.Workbench.ActiveTab?.Title == "a.txt", 10), "the launch was not opened");
            Assert.Equal(root, shell.Workbench.Root);
            var doc = shell.Workbench.ActiveTab!.Document;
            Assert.Equal(doc.Text.Offset(1, 2), doc.Primary.Active);

            // Handed over later: a file opens here; another folder is for a window of its own.
            var handled = shell.HandleLaunchAsync(new LaunchRequest([], [new OpenedFile(Path.Combine(root, "new.md"))], false, true));
            Assert.True(Ui.PumpUntil(() => handled.IsCompleted, 5));
            Assert.Equal(LaunchAnswer.Opened, handled.Result.Answer);
            var made = shell.Workbench.ActiveTab!;
            Assert.Equal("new.md", made.Title); // not on disk yet: made on save
            Assert.False(handled.Result.Closed!.IsCompleted);
            var close = shell.Workbench.CloseAsync(made, shell.Workbench.ActiveGroup);
            Assert.True(Ui.PumpUntil(() => close.IsCompleted && handled.Result.Closed.IsCompleted, 5), "--wait was not answered when the file closed");

            var elsewhere = shell.HandleLaunchAsync(new LaunchRequest([other], [], false, false));
            Assert.True(Ui.PumpUntil(() => elsewhere.IsCompleted, 5));
            Assert.Equal(LaunchAnswer.OpenHere, elsewhere.Result.Answer);
            Assert.Equal(root, shell.Workbench.Root);

            // New Window (Ctrl+Shift+N) starts another window; a dropped folder other than this one goes to one too.
            var started2 = new List<IReadOnlyList<string>>();
            shell.StartWindow = args =>
            {
                started2.Add(args);
                return true;
            };
            Input.Chord(window, Avalonia.Input.Key.N, Avalonia.Input.RawInputModifiers.Control | Avalonia.Input.RawInputModifiers.Shift);
            Assert.Equal(["--new-window"], Assert.Single(started2));
            File.WriteAllText(Path.Combine(other, "b.txt"), "b");
            var dropped = shell.OpenDroppedAsync([other, Path.Combine(other, "b.txt")]);
            Assert.True(Ui.PumpUntil(() => dropped.IsCompleted, 5));
            Assert.Equal(["--new-window", other], started2[1]);
            Assert.Equal("b.txt", shell.Workbench.ActiveTab!.Title);
            window.Close();
            return 0;
        });
    }
}
