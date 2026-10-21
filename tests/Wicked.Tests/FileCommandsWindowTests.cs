using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.Editor.Model;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>The File menu's basics through the window: closing in bulk, reopening, revert, paths, compare, recent, language, line breaks, encoding.</summary>
public sealed class FileCommandsWindowTests
{
    private static void Run(ShellViewModel shell, string id)
    {
        var run = shell.RunCommandAsync(id);
        Assert.True(Ui.PumpUntil(() => run.IsCompleted, 10), $"{id} did not finish");
        Ui.Pump(0.1);
    }

    /// <summary>Answers the window's dialog from the keyboard (focus, Space): the headless hit test can miss the dialog's buttons.</summary>
    private static void Answer(Window window, string button)
    {
        var target = window.FindControl<Button>(button)!;
        target.Focus();
        Input.Chord(window, Avalonia.Input.Key.Space, Avalonia.Input.RawInputModifiers.None);
    }

    private static string Root(TempProfile profile)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(root, name), $"{name} on disk\n");
        return root;
    }

    /// <summary>How many handlers the document's Changed event has (an editor that is gone must have taken its own off).</summary>
    private static int ChangedHandlers(Document document) =>
        (typeof(Document).GetField(nameof(Document.Changed), BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(document) as Delegate)?.GetInvocationList().Length ?? 0;

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ClosingInBulkReopeningAndReverting()
    {
        using var profile = new TempProfile();
        var root = Root(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            string Titles() => string.Join(",", bench.ActiveGroup.Tabs.Select(t => t.Title));
            void Open(string name)
            {
                var opened = bench.OpenAsync(Path.Combine(root, name));
                Assert.True(Ui.PumpUntil(() => opened.IsCompleted, 5));
            }

            Open("a.txt");
            Open("b.txt");
            Open("c.txt");
            Assert.Equal("a.txt,b.txt,c.txt", Titles());

            // Close to the Right of a, then Reopen Closed Editor twice brings c, then b, back.
            bench.ActiveGroup.Activate(bench.ActiveGroup.Tabs[0]);
            Run(shell, "workbench.action.closeEditorsToTheRight");
            Assert.Equal("a.txt", Titles());
            Run(shell, "workbench.action.reopenClosedEditor");
            Run(shell, "workbench.action.reopenClosedEditor");
            Assert.Equal(3, bench.ActiveGroup.Tabs.Count);

            // Close Saved leaves only the edited one; Revert drops the edit; Close Others leaves one.
            var b = bench.ActiveGroup.Tabs.Single(t => t.Title == "b.txt");
            Edits.Type(b.Document, "edited ", EditOptions.Default);
            Run(shell, "workbench.action.closeUnmodifiedEditors");
            Assert.Equal("b.txt", Titles());
            Run(shell, "workbench.action.files.revert");
            Assert.False(b.IsDirty);
            Assert.Equal("b.txt on disk\n", b.Document.Text.ToString());
            Open("a.txt");
            Run(shell, "workbench.action.closeOtherEditors");
            Assert.Equal("a.txt", Titles());

            // Close All with an unsaved file asks once; Don't Save closes everything.
            Open("c.txt");
            Edits.Type(bench.ActiveTab!.Document, "x", EditOptions.Default);
            var closing = shell.RunCommandAsync("workbench.action.closeAllEditors");
            var dialog = window.FindControl<Border>("DialogLayer")!;
            Assert.True(Ui.PumpUntil(() => dialog.IsVisible, 5), "no question about the unsaved file");
            Answer(window, "DialogDontSave");
            Assert.True(Ui.PumpUntil(() => closing.IsCompleted && bench.ActiveGroup.Tabs.Count == 0, 5), $"{Titles()}, dialog {dialog.IsVisible}, closing {closing.Status}, groups {bench.Groups.Count}, dirty {string.Join(",", bench.AllTabs.Where(t => t.IsDirty).Select(t => t.Title))}");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task PathsCompareRecentCloseFolderAndQuit()
    {
        using var profile = new TempProfile();
        var root = Root(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            var copied = new ConcurrentQueue<string>();
            shell.Clipboard = text =>
            {
                copied.Enqueue(text);
                return Task.CompletedTask;
            };
            var a = OpenByClicks(window, shell, "a.txt");

            Run(shell, "workbench.action.files.copyPathOfActiveFile");
            Run(shell, "workbench.action.files.copyRelativePathOfActiveFile");
            Assert.Equal([Path.Combine(root, "a.txt"), "a.txt"], copied);

            // Compare with Saved: a diff tab of the file on disk against the edited text.
            Edits.Type(a.Document, "new ", EditOptions.Default);
            Run(shell, "workbench.files.action.compareWithSaved");
            Assert.Equal("a.txt (on disk) ↔ a.txt", bench.ActiveTab!.Title);

            // Open Recent lists the folder opened before; Close Folder leaves no folder and none to reopen next time.
            Assert.Contains(root, bench.RecentFolders);
            var closing = shell.RunCommandAsync("workbench.action.closeFolder");
            var dialog = window.FindControl<Border>("DialogLayer")!;
            Assert.True(Ui.PumpUntil(() => dialog.IsVisible, 5), "no question about the unsaved file");
            Answer(window, "DialogDontSave");
            Assert.True(Ui.PumpUntil(() => closing.IsCompleted && bench.Root is null, 5), "the folder is still open");
            Assert.True(Ui.PumpUntil(() => File.ReadAllText(profile.Paths.SessionFile).Contains("\"LastFolder\": null", StringComparison.Ordinal), 5));
            Run(shell, "workbench.action.openRecent");
            Assert.True(shell.QuickInput.IsOpen);
            Assert.Contains(shell.QuickInput.Items, i => i.Detail == root);
            shell.QuickInput.Close();

            // Quit asks the window to close.
            var quit = 0;
            shell.QuitRequested += () => quit++;
            Run(shell, "workbench.action.quit");
            Assert.Equal(1, quit);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task LanguageLineBreaksAndEncodingCanBeChanged()
    {
        using var profile = new TempProfile();
        var root = Root(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var bench = shell.Workbench;
            var a = OpenByClicks(window, shell, "a.txt");
            var handlers = ChangedHandlers(a.Document);
            var before = a.Editor!;

            // Change Language Mode: the file is Python now, in a new editor (and a language server's document); back to plain
            // text, every handler the editors and the server put on the document has come off again.
            bench.ChangeLanguage(a, Wicked.Editor.Syntax.Languages.ForName("python"));
            Assert.True(Ui.PumpUntil(() => a.Editor is { } now && now != before && now.IsAttachedToVisualTree(), 5), "no new editor");
            Assert.Equal("python", a.Language!.Id);
            Assert.Equal("python", a.Editor!.Language!.Id);
            Assert.EndsWith("Python", shell.DocumentText, StringComparison.Ordinal);
            var python = a.Editor;
            bench.ChangeLanguage(a, null);
            Assert.True(Ui.PumpUntil(() => a.Editor is { } plain && plain != python && plain.IsAttachedToVisualTree(), 5), "no plain-text editor");
            Assert.Null(a.Language);
            Assert.Equal(handlers, ChangedHandlers(a.Document));

            // Change End of Line Sequence: CRLF makes the file unsaved, and saving writes it.
            Run(shell, "workbench.action.editor.changeEOL");
            Assert.True(shell.QuickInput.IsOpen);
            var crlf = shell.QuickInput.Items.Single(i => i.Label == "CRLF").Choose();
            Assert.True(Ui.PumpUntil(() => crlf.IsCompleted && a.IsDirty, 5));
            shell.QuickInput.Close();
            Input.Chord(window, Avalonia.Input.Key.S, Avalonia.Input.RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => !a.IsDirty, 5));
            Assert.Equal("a.txt on disk\r\n", File.ReadAllText(Path.Combine(root, "a.txt")));

            // Save with Encoding UTF-16 LE: the file starts with its byte-order mark.
            a.Encoding = new Wicked.Core.Files.FileEncoding("utf-16le", true);
            var save = bench.SaveAsync(a);
            Assert.True(Ui.PumpUntil(() => save.IsCompleted, 5));
            Assert.Equal([0xFF, 0xFE], File.ReadAllBytes(Path.Combine(root, "a.txt"))[..2]);

            // Reopen with Encoding latin1 reads the same bytes another way.
            File.WriteAllBytes(Path.Combine(root, "b.txt"), [0x63, 0x61, 0x66, 0xE9]); // "café" in latin1
            var b = OpenByClicks(window, shell, "b.txt");
            var reopened = bench.ReopenWithEncodingAsync(b, new Wicked.Core.Files.FileEncoding("latin1", false));
            Assert.True(Ui.PumpUntil(() => reopened.IsCompleted, 5));
            Assert.Equal("café", b.Document.Text.ToString());
            window.Close();
            return 0;
        });
    }
}
