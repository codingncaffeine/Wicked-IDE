using Avalonia.Headless;
using Avalonia.Input;
using Wicked.App.Editor;
using Wicked.Core;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Wicked.Tests;

/// <summary>Settings through the window: the keys, the sheet, and settings.json edited by hand, each applied live.</summary>
public sealed class SettingsWindowTests
{
    private static string Folder(TempProfile profile)
    {
        var root = Path.Combine(profile.Root, "p");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.cs"), "class A\n{\n}\n");
        return root;
    }

    private static CodeEditor OpenFile(Avalonia.Controls.Window window, App.ViewModels.ShellViewModel shell, string path)
    {
        var start = shell.StartAsync();
        Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
        var opening = shell.Workbench.OpenAsync(path);
        Assert.True(Ui.PumpUntil(() => opening.IsCompleted && shell.Workbench.ActiveTab?.Editor?.IsAttachedToVisualTree() == true, 5));
        var editor = shell.Workbench.ActiveTab!.Editor!;
        Input.Click(window, editor);
        return editor;
    }

    [Fact]
    public async Task ZoomKeysChangeTheFontAndAreKept()
    {
        using var profile = new TempProfile();
        var root = Folder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var editor = OpenFile(window, shell, Path.Combine(root, "a.cs"));
            var services = EditorServices.Ready.Result;
            var before = services.Fonts.Size;

            Input.Chord(window, Key.OemPlus, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => services.Fonts.Size == before + 1, 5), $"font size {services.Fonts.Size}");
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains($"\"editor.fontSize\": {before + 1}"), 5));

            Input.Chord(window, Key.D0, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => services.Fonts.Size == 14, 5));
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task SettingsJsonEditedByHandAppliesAndABrokenOneSaysWhere()
    {
        using var profile = new TempProfile();
        var root = Folder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var editor = OpenFile(window, shell, Path.Combine(root, "a.cs"));
            Assert.Equal(4, editor.Options.TabSize);

            File.WriteAllText(profile.Paths.SettingsFile, "{\n  // two, as this project likes\n  \"editor.tabSize\": 2,\n}\n");
            Assert.True(Ui.PumpUntil(() => editor.Options.TabSize == 2, 5), "the hand edit was not applied");

            File.WriteAllText(profile.Paths.SettingsFile, "{ \"editor.tabSize\": ");
            Assert.True(Ui.PumpUntil(() => shell.Notice?.Kind == NoticeKind.Error, 5), "a broken file was not reported");
            Assert.Equal($"tab/{profile.Paths.SettingsFile}", shell.Notice!.Route);
            Assert.Equal(2, editor.Options.TabSize); // the last good settings stay in force
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheColourPickerPreviewsAndEscapeTakesItBack()
    {
        using var profile = new TempProfile();
        var root = Folder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            OpenFile(window, shell, Path.Combine(root, "a.cs"));
            var syntax = EditorServices.Ready.Result.Syntax;
            var before = syntax.Theme;

            shell.Commands.First(c => c.Id == "workbench.action.selectTheme").Run();
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.Count > 5, 5));
            shell.QuickInput.Selected = shell.QuickInput.Items.ToList().FindIndex(i => i.Label == "Dracula");
            Assert.True(Ui.PumpUntil(() => syntax.Theme == TextMateSharp.Grammars.ThemeName.Dracula, 5), "no live preview");

            shell.QuickInput.Close();
            Assert.True(Ui.PumpUntil(() => syntax.Theme == before, 5), "Escape did not take the preview back");
            Assert.Equal("", shell.Settings.CodeColorTheme);
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task AutoSaveAfterADelayWritesTheFile()
    {
        using var profile = new TempProfile();
        var root = Folder(profile);
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(profile.Paths.SettingsFile, "{ \"files.autoSave\": \"afterDelay\", \"files.autoSaveDelay\": 200 }");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var editor = OpenFile(window, shell, Path.Combine(root, "a.cs"));
            window.KeyTextInput("// saved by itself");
            Assert.True(Ui.PumpUntil(() => File.ReadAllText(Path.Combine(root, "a.cs")).Contains("// saved by itself", StringComparison.Ordinal), 5), "auto save did not write");
            // The file reaches the disk a moment before the tab is marked clean.
            Assert.True(Ui.PumpUntil(() => !shell.Workbench.ActiveTab!.IsDirty, 5), "the tab stayed dirty after the save");
            window.Close();
            return 0;
        });
    }

    [Fact]
    public async Task TheSettingsSheetOpensWithCtrlCommaAndItsControlsApply()
    {
        using var profile = new TempProfile();
        var root = Folder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            var editor = OpenFile(window, shell, Path.Combine(root, "a.cs"));
            Input.Chord(window, Key.OemComma, RawInputModifiers.Control);
            Assert.True(shell.SettingsOpen);
            Assert.True(window.FindControl<Border>("SettingsLayer")!.IsVisible);
            if (Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_SETTINGS") is { Length: > 0 } shot)
            {
                Ui.Pump(0.3);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }


            shell.SettingsSheet.InsertSpaces = false;
            Assert.True(Ui.PumpUntil(() => !editor.Options.InsertSpaces, 5));
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains("\"editor.insertSpaces\": false"), 5));

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Ui.Pump(0.1);
            Assert.False(shell.SettingsOpen);
            window.Close();
            return 0;
        });
    }
}
