using System.Net;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Git;
using Wicked.App.ViewModels;
using Wicked.App.Views;

namespace Wicked.Tests.Git;

/// <summary>The Source Control panel walked the way a user walks it, in the headless window.</summary>
public sealed class SourceControlWindowTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (MainWindow Window, ShellViewModel Shell) Open(TempProfile profile, GitSandbox box)
    {
        var (window, shell) = Ui.Start(profile.Paths);
        shell.SourceControl.Environment = box.Env;
        var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted, 10), "the folder did not open");
        Input.Click(window, window.FindControl<Button>("RailSourceControl")!);
        Assert.True(shell.IsSourceControl);
        return (window, shell);
    }

    private static T Find<T>(Window window, string name) where T : Control =>
        window.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static ListBoxItem? FileRow(Window window, string name, ScmGroup group) =>
        window.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(i => i.DataContext is ScmFileRow r && r.Name == name && r.Group == group && i.IsEffectivelyVisible);

    private static void Hover(Window window, Control control)
    {
        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window) ?? throw new InvalidOperationException("the row is not in the window");
        window.MouseMove(centre, RawInputModifiers.None);
        Ui.Pump(0.1);
    }

    /// <summary>Moves the pointer onto the row (the row's buttons appear on hover) and clicks the button with this name.</summary>
    private static void ClickRowButton(Window window, ListBoxItem row, string automationName)
    {
        Hover(window, row);
        var button = row.GetVisualDescendants().OfType<Button>().First(b => AutomationProperties.GetName(b) == automationName);
        Assert.True(button.IsEffectivelyVisible, $"{automationName} is not shown on hover");
        Input.Click(window, button);
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task ChangeAFileSeeItInChangesStageTypeAMessageCtrlEnterAndTheTreeIsClean()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("src/a.txt", "one\n"), ("b.txt", "bee\n"));

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => scm.Status.Branch.Head == "main", 10), "the repository did not load");
            Assert.True(Ui.PumpUntil(() => scm.IsClean && Find<TextBlock>(window, "CleanText").IsEffectivelyVisible, 5));

            // The file changes on disk; the panel hears of it through the watcher, with no refresh asked for.
            File.WriteAllText(box.PathOf("src/a.txt"), "one\ntwo\n");
            Assert.True(Ui.PumpUntil(() => FileRow(window, "a.txt", ScmGroup.Changes) is not null, 10), "the changed file never appeared under Changes");
            Assert.Equal("M", ((ScmFileRow)FileRow(window, "a.txt", ScmGroup.Changes)!.DataContext!).Letter);

            ClickRowButton(window, FileRow(window, "a.txt", ScmGroup.Changes)!, "Stage Changes");
            Assert.True(Ui.PumpUntil(() => FileRow(window, "a.txt", ScmGroup.Staged) is not null && FileRow(window, "a.txt", ScmGroup.Changes) is null, 10), "the file did not move to Staged");

            // Type the message in the box and commit with Ctrl+Enter.
            var commitBox = Find<TextBox>(window, "CommitBox");
            Input.Click(window, commitBox);
            Assert.True(Ui.PumpUntil(() => commitBox.IsFocused, 5));
            window.KeyTextInput("Add a second line");
            Ui.Pump(0.1);
            Assert.Equal("Add a second line", scm.CommitMessage);
            Assert.True(Find<Button>(window, "CommitButton").IsEnabled);
            Input.Chord(window, Key.Enter, RawInputModifiers.Control);

            Assert.True(Ui.PumpUntil(() => scm.Rows.Length == 0 && scm.CommitMessage.Length == 0 && !scm.IsBusy, 10), "the commit did not finish");
            Assert.True(Ui.PumpUntil(() => Find<TextBlock>(window, "CleanText").IsEffectivelyVisible, 5));
            Assert.Equal("", scm.ErrorText);
            window.Close();
            return 0;
        });

        Assert.Equal("Add a second line", (await box.GitAsync("log", "-1", "--format=%s")).Trim());
        Assert.Equal("one\ntwo\n", await box.GitAsync("show", "HEAD:src/a.txt"));
        Assert.Equal("", (await box.GitAsync("status", "--porcelain")).Trim());
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AFolderWithNoRepositoryOffersInitializeAndMakesOne()
    {
        using var profile = new TempProfile();
        using var box = GitSandbox.CreateEmpty();
        File.WriteAllText(box.PathOf("readme.txt"), "hello\n");
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => scm.ShowsInit, 10), "the empty state did not appear");
            Assert.True(Find<Button>(window, "InitButton").IsEffectivelyVisible);
            Assert.False(Find<TextBox>(window, "CommitBox").IsEffectivelyVisible);

            Input.Click(window, Find<Button>(window, "InitButton"));
            Assert.True(Ui.PumpUntil(() => scm.HasRepository && scm.Status.Branch.Head == "main", 10), "git init did not give a repository");
            Assert.True(Ui.PumpUntil(() => FileRow(window, "readme.txt", ScmGroup.Untracked) is not null, 10));
            window.Close();
            return 0;
        });

        Assert.True(Directory.Exists(box.PathOf(".git")));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task DiscardAsksFirstAndCancelLeavesTheFileAlone()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("a.txt", "original\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => scm.Status.Branch.Head == "main", 10));
            File.WriteAllText(box.PathOf("a.txt"), "changed\n");
            Assert.True(Ui.PumpUntil(() => FileRow(window, "a.txt", ScmGroup.Changes) is not null, 10));

            var dialog = window.FindControl<Border>("DialogLayer")!;
            ClickRowButton(window, FileRow(window, "a.txt", ScmGroup.Changes)!, "Discard Changes");
            Assert.True(Ui.PumpUntil(() => dialog.IsVisible, 5), "discarding did not ask first");
            Input.Click(window, window.FindControl<Button>("DialogCancel")!);
            Ui.Pump(0.3);
            Assert.Equal("changed\n", File.ReadAllText(box.PathOf("a.txt")));
            Assert.NotNull(FileRow(window, "a.txt", ScmGroup.Changes));

            ClickRowButton(window, FileRow(window, "a.txt", ScmGroup.Changes)!, "Discard Changes");
            Assert.True(Ui.PumpUntil(() => dialog.IsVisible, 5));
            Input.Click(window, window.FindControl<Button>("DialogSave")!);
            Assert.True(Ui.PumpUntil(() => scm.IsClean, 10), "the discard did not clear the list");
            Assert.Equal("original\n", File.ReadAllText(box.PathOf("a.txt")));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task APushThatNeedsACredentialAsksInADialogAndSendsTheAnswerToGit()
    {
        Assert.SkipUnless(Wicked.Core.Git.AskpassRelay.IsSupported, "Windows git asks through the Git Credential Manager it ships with");
        using var profile = new TempProfile();
        var (box, repo) = await GitSandbox.CreateAsync();
        using var cleanup = box;
        await box.CommitAsync("base", ("a.txt", "one\n"));

        // A stand-in server on loopback that wants Basic credentials.
        using var listener = new HttpListener();
        var port = FreePort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<string>();
        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }

                var auth = context.Request.Headers["Authorization"];
                if (auth is null)
                {
                    context.Response.StatusCode = 401;
                    context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"test\"");
                }
                else
                {
                    lock (seen) seen.Add(auth);
                    context.Response.StatusCode = 403;
                }

                context.Response.Close();
            }
        }, Ct);
        await box.GitAsync("remote", "add", "origin", $"http://127.0.0.1:{port}/repo.git");

        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, box);
            var scm = shell.SourceControl;
            Assert.True(Ui.PumpUntil(() => scm.Status.Branch.Head == "main" && scm.CanSync, 10), "no Publish button");
            Assert.Equal("Publish Branch", scm.SyncLabel);

            Input.Click(window, Find<Button>(window, "SyncButton"));
            var layer = window.FindControl<Border>("GitPromptLayer")!;
            Assert.True(Ui.PumpUntil(() => layer.IsVisible, 20), "git never asked for the username");
            Assert.StartsWith("Username for", Find<TextBlock>(window, "GitPromptText").Text, StringComparison.Ordinal);
            Assert.Equal('\0', Find<TextBox>(window, "GitPromptAnswer").PasswordChar); // a username is shown as typed

            Ui.PumpUntil(() => Find<TextBox>(window, "GitPromptAnswer").IsFocused, 3);
            window.KeyTextInput("alice");
            Input.Click(window, Find<Button>(window, "GitPromptOk"));

            Assert.True(Ui.PumpUntil(() => layer.IsVisible && Find<TextBlock>(window, "GitPromptText").Text!.StartsWith("Password for", StringComparison.Ordinal), 20), "git never asked for the password");
            Assert.NotEqual('\0', Find<TextBox>(window, "GitPromptAnswer").PasswordChar);
            Ui.PumpUntil(() => Find<TextBox>(window, "GitPromptAnswer").IsFocused, 3);
            window.KeyTextInput("s3cret");
            Input.Chord(window, Key.Enter, RawInputModifiers.None);

            Assert.True(Ui.PumpUntil(() => !layer.IsVisible && !scm.IsBusy, 20), "the push did not end");
            // The stand-in refuses the push (403), and the panel says so.
            Assert.True(scm.HasError);
            window.Close();
            return 0;
        });

        listener.Stop();
        await serving;
        lock (seen) Assert.Contains("Basic " + Convert.ToBase64String("alice:s3cret"u8.ToArray()), seen);
        _ = repo;
    }

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }
}
