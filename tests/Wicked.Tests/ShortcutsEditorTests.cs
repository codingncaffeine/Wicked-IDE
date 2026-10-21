using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.Core.Settings;
using Wicked.Editor.View;

namespace Wicked.Tests;

/// <summary>What the Keyboard Shortcuts editor writes to keybindings.json, the way VS Code writes it.</summary>
public sealed class KeybindingsEditTests
{
    private static List<(string? Key, string Command, string? When)> Lines(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return [.. doc.RootElement.EnumerateArray().Select(e => (e.TryGetProperty("key", out var k) ? k.GetString() : null, e.GetProperty("command").GetString()!,
            e.TryGetProperty("when", out var w) ? w.GetString() : null))];
    }

    [Theory]
    [InlineData("Ctrl+Shift+K", "ctrl+shift+k")]
    [InlineData("Ctrl+K Ctrl+S", "ctrl+k ctrl+s")]
    [InlineData("Ctrl+Oem2", "ctrl+/")]
    [InlineData("Ctrl+OemPlus", "ctrl+=")]
    [InlineData("Alt+PageDown", "alt+pagedown")]
    [InlineData("Shift+F3", "shift+f3")]
    [InlineData("Ctrl+D1", "ctrl+1")]
    [InlineData("Ctrl+NumPad5", "ctrl+numpad5")]
    public void KeysAreWrittenInVsCodesNamesAndReadBack(string avalonia, string vscode)
    {
        Assert.Equal(vscode, Keybindings.ToVsCode(avalonia));
        Assert.Equal(avalonia, Keybindings.Translate(vscode));
    }

    [Fact]
    public void ANewKeyForADefaultAddsALineAndTakesTheDefaultAway()
    {
        var json = KeybindingsEdit.Change("// mine\n[ { \"key\": \"ctrl+q\", \"command\": \"other\" } ]", "editor.action.toggleWordWrap", "Ctrl+Alt+W", "Alt+Z", oldIsDefault: true);
        Assert.Equal([("ctrl+q", "other", null), ("ctrl+alt+w", "editor.action.toggleWordWrap", null), ("alt+z", "-editor.action.toggleWordWrap", null)], Lines(json));
        Assert.StartsWith("//", json, StringComparison.Ordinal);
        Assert.Contains("\"key\": \"ctrl+alt+w\"", json, StringComparison.Ordinal); // written as it reads, not escaped
        // Read back, the default is gone and the new key is there.
        var (bindings, problem) = Keybindings.Parse(json);
        Assert.Null(problem);
        Assert.Contains(new Keybinding("Alt+Z", "editor.action.toggleWordWrap", true), bindings);
    }

    [Fact]
    public void ANewKeyForTheUsersOwnLineRewritesItAndKeepsItsWhen()
    {
        var json = KeybindingsEdit.Change("[ { \"key\": \"ctrl+e\", \"command\": \"a\", \"when\": \"editorTextFocus\" } ]", "a", "Ctrl+R", "Ctrl+E", oldIsDefault: false);
        Assert.Equal([("ctrl+r", "a", "editorTextFocus")], Lines(json));
        Assert.Equal("editorTextFocus", Assert.Single(Keybindings.Parse(json).Bindings).When);
    }

    [Fact]
    public void RemoveTakesADefaultAwayOrDeletesTheUsersLineAndResetDeletesEveryLineForTheCommand()
    {
        var json = KeybindingsEdit.Remove("[]", "a", "Ctrl+E", isDefault: true);
        Assert.Equal([("ctrl+e", "-a", null)], Lines(json));
        json = KeybindingsEdit.Change(json, "a", "Ctrl+R", null, oldIsDefault: false);
        json = KeybindingsEdit.Change(json, "b", "Ctrl+B", null, oldIsDefault: false);
        Assert.Equal(3, Lines(json).Count);
        Assert.Equal([("ctrl+e", "-a", null), ("ctrl+b", "b", null)], Lines(KeybindingsEdit.Remove(json, "a", "Ctrl+R", isDefault: false)));
        Assert.Equal([("ctrl+b", "b", null)], Lines(KeybindingsEdit.Reset(json, "a")));
    }
}

/// <summary>The Keyboard Shortcuts editor the way a user meets it.</summary>
public sealed class ShortcutsEditorWindowTests
{
    private static ListBox List(Window window) => window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ShortcutRows");

    private static ShortcutsViewModel Model(Window window) => (ShortcutsViewModel)window.GetVisualDescendants().OfType<ShortcutsView>().Single().DataContext!;

    private static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None) => Input.Chord(window, key, modifiers);

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task ANewKeyIsRecordedForACommandItWorksAndResetPutsTheDefaultBack()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a long line\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "a.txt");

            // Ctrl+K Ctrl+S opens the editor in a tab, its search with the keyboard.
            Press(window, Key.K, RawInputModifiers.Control);
            Press(window, Key.S, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<ShortcutsView>().Any(v => v.IsEffectivelyVisible), 5), "no Keyboard Shortcuts tab");
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ShortcutsSearch");
            Assert.True(Ui.PumpUntil(() => search.IsFocused, 5), "the search does not have the keyboard");
            window.KeyTextInput("toggle word wrap");
            Assert.True(Ui.PumpUntil(() => Model(window).Rows.Count == 1, 5), string.Join(", ", Model(window).Rows.Select(r => r.Title)));
            var model = Model(window);
            var row = model.Rows[0];
            Assert.Equal(("editor.action.toggleWordWrap", "Alt+Z", "Default"), (row.Id, row.DisplayKeys, row.Source));

            // Enter on the row opens the box; the keys pressed show, Enter keeps them.
            List(window).SelectedIndex = 0;
            Ui.Pump(0.1);
            List(window).ContainerFromIndex(0)!.Focus();
            Press(window, Key.Enter);
            var box = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RecordBox");
            Assert.True(Ui.PumpUntil(() => box.IsEffectivelyVisible && box.IsFocused, 5), "the record box did not open");
            Press(window, Key.W, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal("Ctrl+Alt+W", window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RecordedText").Text);
            Press(window, Key.Enter);

            // keybindings.json says so, as VS Code writes it, and the row shows the user's keys.
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.KeybindingsFile) && File.ReadAllText(profile.Paths.KeybindingsFile).Contains("\"ctrl+alt+w\"", StringComparison.Ordinal), 5), "keybindings.json was not written");
            var written = File.ReadAllText(profile.Paths.KeybindingsFile);
            Assert.Contains("\"-editor.action.toggleWordWrap\"", written, StringComparison.Ordinal);
            Assert.True(Ui.PumpUntil(() => Model(window).Rows.Any(r => r.Id == "editor.action.toggleWordWrap" && r.DisplayKeys == "Ctrl+Alt+W" && r.Source == "User"), 5), "the row did not follow");

            // The new keys work in the editor; Alt+Z no longer does.
            shell.Workbench.ActiveGroup.Activate(tab);
            Assert.True(Ui.PumpUntil(() => tab.Editor?.IsAttachedToVisualTree() == true, 5));
            tab.Editor!.Focus();
            Press(window, Key.Z, RawInputModifiers.Alt);
            Assert.Equal(WordWrapMode.Off, tab.Editor.WordWrap.Mode);
            Press(window, Key.W, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.Equal(WordWrapMode.On, tab.Editor.WordWrap.Mode);

            // Reset Keybinding puts the default back.
            var reset = model.ResetAsync(model.Rows.First(r => r.Id == "editor.action.toggleWordWrap"));
            Assert.True(Ui.PumpUntil(() => reset.IsCompleted && shell.Keys.KeysFor("editor.action.toggleWordWrap") == "Alt+Z", 5), shell.Keys.KeysFor("editor.action.toggleWordWrap"));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task KeysPressedInTheSearchFindTheirCommandsAndTheBoxNamesACommandThatHasThemAlready()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a\n");
        await Ui.Run(() =>
        {
            var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
            var started = shell.RunCommandAsync("workbench.action.openGlobalKeybindings");
            Assert.True(Ui.PumpUntil(() => started.IsCompleted && window.GetVisualDescendants().OfType<ShortcutsView>().Any(v => v.IsEffectivelyVisible), 5));
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == "ShortcutsSearch"), 5), "the view was not laid out");
            var search = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ShortcutsSearch");
            Assert.True(Ui.PumpUntil(() => search.IsFocused, 5));

            // Record Keys: Ctrl+B pressed in the search finds the side bar's toggle, not text typed.
            Input.Click(window, window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "RecordKeysToggle"));
            search.Focus();
            Press(window, Key.B, RawInputModifiers.Control);
            Assert.True(Ui.PumpUntil(() => Model(window).Rows.Count > 0 && Model(window).Rows.All(r => r.DisplayKeys == "Ctrl+B"), 5), string.Join(", ", Model(window).Rows.Select(r => r.Title)));
            Assert.Contains(Model(window).Rows, r => r.Id == "workbench.action.toggleSidebarVisibility");
            Assert.True(shell.SidebarVisible, "Ctrl+B reached the window");

            // A command without keys: the box names the command Ctrl+B already runs.
            Input.Click(window, window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Name == "RecordKeysToggle"));
            var view = window.GetVisualDescendants().OfType<ShortcutsView>().Single();
            view.StartRecording(new ShortcutRow(shell.Commands.First(c => c.Id == "workbench.action.tasks.runTask"), null, false, null, false));
            var box = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "RecordBox");
            Assert.True(Ui.PumpUntil(() => box.IsFocused, 5));
            Press(window, Key.B, RawInputModifiers.Control);
            var conflict = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "RecordConflict");
            Assert.True(conflict.IsVisible);
            Assert.Contains("Toggle", conflict.Text, StringComparison.OrdinalIgnoreCase);
            Press(window, Key.Escape);
            Assert.False(box.IsEffectivelyVisible);
            Assert.False(File.Exists(profile.Paths.KeybindingsFile), "Escape wrote keybindings.json");
            window.Close();
            return 0;
        });
    }
}
