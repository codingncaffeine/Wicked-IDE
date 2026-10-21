using Avalonia.Controls;
using Wicked.Editor.Model;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>files.hotExit: unsaved files kept when the window closes, and back, still unsaved, in the next window.</summary>
public sealed class HotExitTests
{
    [Trait("Tier", "Window")]
    [Fact]
    public async Task UnsavedFilesAreKeptWhenTheWindowClosesAndComeBack()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "on disk\n");
        var backups = Path.Combine(profile.Paths.State, "backups");
        await Ui.Run(() =>
        {
            // An edited file and an untitled one; closing the window asks nothing and keeps both.
            var (window, shell) = OpenWorkbench(profile, root);
            var start = shell.StartAsync();
            // Starting reopens the folder just opened (git, watchers): on a two-CPU Windows runner that took longer than 10 s.
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 30), "Wicked did not finish starting");
            var a = OpenByClicks(window, shell, "a.txt");
            Edits.Type(a.Document, "edited ", EditOptions.Default);
            var untitled = shell.Workbench.NewFile();
            Edits.Type(untitled.Document, "scratch notes", EditOptions.Default);
            window.Close();
            Assert.True(Ui.PumpUntil(() => !window.IsVisible, 5), "the window asked instead of closing");
            Assert.True(Ui.PumpUntil(() => Directory.Exists(backups) && Directory.GetFiles(backups, "*.json").Length == 2, 5), "the unsaved files were not kept");
            return 0;
        });

        await Ui.Run(() =>
        {
            // The next window on the folder: both are back, unsaved, with their text; the file on disk is untouched.
            var (window, shell) = Ui.Start(profile.Paths);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted && shell.Workbench.AllTabs.Count() == 2, 10), string.Join(",", shell.Workbench.AllTabs.Select(t => t.Title)));
            var a = shell.Workbench.AllTabs.Single(t => t.Title == "a.txt");
            var notes = shell.Workbench.AllTabs.Single(t => t.Path is null);
            Assert.Equal("edited on disk\n", a.Document.Text.ToString());
            Assert.Equal("scratch notes", notes.Document.Text.ToString());
            Assert.True(a.IsDirty && notes.IsDirty);
            Assert.Equal("on disk\n", File.ReadAllText(Path.Combine(root, "a.txt")));

            // Saved, a.txt's backup goes; the untitled one's stays while it is unsaved.
            var save = shell.Workbench.SaveAsync(a);
            Assert.True(Ui.PumpUntil(() => save.IsCompleted && !a.IsDirty, 5));
            Assert.True(Ui.PumpUntil(() => Directory.GetFiles(backups, "*.json").Length == 1, 5), $"{Directory.GetFiles(backups, "*.json").Length} backups");

            // With files.hotExit off, closing asks again.
            var off = shell.UpdateSettingsAsync(s => s with { HotExit = "off" });
            Assert.True(Ui.PumpUntil(() => off.IsCompleted, 5));
            window.Close();
            var dialog = window.FindControl<Border>("DialogLayer")!;
            Assert.True(Ui.PumpUntil(() => dialog.IsVisible, 5), "closing with hot exit off did not ask");
            return 0;
        });
    }
}
