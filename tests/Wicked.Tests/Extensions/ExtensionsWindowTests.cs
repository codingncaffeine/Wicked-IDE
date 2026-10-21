using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using TextMateSharp.Registry;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.App.Workbench;
using Wicked.Core.Extensions;
using Wicked.Core.Lsp;
using Wicked.Editor.Syntax;

namespace Wicked.Tests.Extensions;

/// <summary>The extension host through the real (headless) window: the panel, the palette, the editor, the rail.</summary>
public sealed class ExtensionsWindowTests
{
    /// <summary>The window's own dialogs, except that the file picker (which headless cannot show) answers with a folder.</summary>
    private sealed class PickingDialogs(MainWindow window, string folder) : IWorkbenchDialogs
    {
        public Task<SaveChoice> AskSaveAsync(string message, string detail) => window.AskSaveAsync(message, detail);

        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(folder);

        public Task<IReadOnlyList<string>> PickFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> PickSaveFileAsync(string suggestedName) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message, string detail, string confirm) => window.ConfirmAsync(message, detail, confirm);

        public Task<string?> AskNameAsync(string prompt, string initial) => window.AskNameAsync(prompt, initial);
    }

    private static T Await<T>(Task<T> task, string what)
    {
        Assert.True(Ui.PumpUntil(() => task.IsCompleted, 15), what);
        return task.Result;
    }

    private static void Await(Task task, string what) => Assert.True(Ui.PumpUntil(() => task.IsCompleted, 15), what);

    private static TControl Find<TControl>(Window window, string name)
        where TControl : Control => window.GetVisualDescendants().OfType<TControl>().First(c => c.Name == name);

    private static Button ButtonLabelled(Window window, string content) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && b.Content as string == content);

    private static (MainWindow Window, ShellViewModel Shell) Start(TempProfile profile)
    {
        var (window, shell) = Ui.Start(profile.Paths);
        Await(shell.Extensions.StartAsync(), "the extension host did not start");
        return (window, shell);
    }

    private static string Chosen(Window window) => ((TextBlock)Find<TextBlock>(window, "DialogMessage")).Text ?? "";

    private static void Answer(Window window, string button)
    {
        Assert.True(Ui.PumpUntil(() => Find<Border>(window, "DialogLayer").IsVisible, 10), "no question was asked");
        Input.Click(window, Find<Button>(window, button));
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task InstallFromAFolderThroughThePanelThenDisableAndEnableAndUninstall()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        var source = ExtensionKit.HelloFolder(scratch);
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            shell.Workbench.Dialogs = new PickingDialogs(window, source);

            Input.Click(window, Find<Button>(window, "RailExtensions"));
            Assert.True(shell.IsExtensions);
            Assert.True(Find<TextBlock>(window, "NoExtensions").IsEffectivelyVisible);

            Input.Click(window, Find<Button>(window, "InstallFolder"));
            Assert.True(Ui.PumpUntil(() => Find<Border>(window, "DialogLayer").IsVisible, 10), "the install did not ask first");
            Assert.Contains("Install", Chosen(window), StringComparison.Ordinal);
            Assert.Contains("runs code on your computer", Find<TextBlock>(window, "DialogDetail").Text, StringComparison.Ordinal);
            Assert.Empty(shell.Extensions.Manager.Installed); // nothing is installed before the answer
            Input.Click(window, Find<Button>(window, "DialogSave"));

            Assert.True(Ui.PumpUntil(() => shell.Extensions.Rows.Count == 1 && shell.Extensions.Rows[0].StateText.StartsWith("Active", StringComparison.Ordinal), 15), shell.Extensions.Status);
            Assert.Equal("hello extension", shell.Extensions.Rows[0].Name);
            Assert.False(Find<TextBlock>(window, "NoExtensions").IsEffectivelyVisible);

            // The command is in the palette, and runs.
            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("say hello");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.Any(i => i.Label == "Hello: Say Hello"), 5));
            Assert.Equal("Ctrl+Alt+H", shell.QuickInput.Items.First(i => i.Label == "Hello: Say Hello").Keys);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => shell.Notices.All.Any(n => n.Text == "Hello from the extension"), 5));

            // Disabled, the command is gone and the panel says so.
            Input.Click(window, ButtonLabelled(window, "Disable"));
            Assert.True(Ui.PumpUntil(() => shell.Extensions.Rows.Count == 1 && shell.Extensions.Rows[0].StateText == "Disabled", 10));
            Assert.DoesNotContain(shell.Commands, c => c.Id == "hello.say");
            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("say hello");
            Ui.Pump(0.4);
            Assert.DoesNotContain(shell.QuickInput.Items, i => i.Label == "Hello: Say Hello");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

            Input.Click(window, ButtonLabelled(window, "Enable"));
            Assert.True(Ui.PumpUntil(() => shell.Commands.Any(c => c.Id == "hello.say"), 10));

            // Uninstalling asks, then deletes the folder.
            var folder = shell.Extensions.Manager.Installed.Single().Folder;
            Input.Click(window, ButtonLabelled(window, "Uninstall"));
            Assert.Contains("Uninstall", Chosen(window), StringComparison.Ordinal);
            Answer(window, "DialogSave");
            Assert.True(Ui.PumpUntil(() => shell.Extensions.Rows.Count == 0, 10));
            Assert.False(Directory.Exists(folder));
            Assert.DoesNotContain(shell.Commands, c => c.Id == "hello.say");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheExtensionsPanelRendersAFrame()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        var shot = Environment.GetEnvironmentVariable("WICKED_SCREENSHOT_EXTENSIONS");
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            Await(shell.Extensions.Manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), ExtensionKit.Yes), "install");
            Await(shell.Extensions.Manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch, "broken-src", id: "broken", source: "public sealed class Broken : Wicked.Core.Extensions.IWickedExtension { public System.Threading.Tasks.Task ActivateAsync(Wicked.Core.Extensions.IWickedApi api, System.Threading.CancellationToken ct) => throw new System.InvalidOperationException(\"cannot start\"); }"), ExtensionKit.Yes), "install broken");
            Input.Click(window, Find<Button>(window, "RailExtensions"));
            Assert.True(Ui.PumpUntil(() => shell.Extensions.Rows.Count == 2, 10));
            Ui.Pump(0.4);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            if (!string.IsNullOrEmpty(shot)) frame.Save(shot, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task DecliningTheQuestionInstallsNothing()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        var source = ExtensionKit.HelloFolder(scratch);
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            shell.Workbench.Dialogs = new PickingDialogs(window, source);
            Input.Click(window, Find<Button>(window, "RailExtensions"));
            Input.Click(window, Find<Button>(window, "InstallFolder"));
            Answer(window, "DialogCancel");
            Assert.True(Ui.PumpUntil(() => shell.Extensions.Status == "Nothing was installed.", 10));
            Assert.Empty(shell.Extensions.Rows);
            Assert.DoesNotContain(shell.Commands, c => c.Id == "hello.say");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AnExtensionsKeysRunItsCommandAndTheUsersKeybindingsStillWin()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            Await(shell.Extensions.Manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), ExtensionKit.Yes), "install");
            Ui.PumpUntil(() => shell.Commands.Any(c => c.Id == "hello.say"), 5);
            Input.Chord(window, Key.H, RawInputModifiers.Control | RawInputModifiers.Alt);
            Assert.True(Ui.PumpUntil(() => shell.Notices.All.Any(n => n.Text == "Hello from the extension"), 5), "the shortcut did not run the command");
            Assert.Equal("Ctrl+Alt+H", shell.Keys.KeysFor("hello.say"));

            // A second extension binds the same command to another key: its key is ahead of the default.
            var binder = scratch.Sub("binder-src");
            File.WriteAllText(Path.Combine(binder, Wicked.Core.Extensions.ExtensionManifest.FileName), ExtensionKit.Manifest(id: "binder", main: null, contributes: """{ "keybindings": [ { "command": "hello.say", "key": "ctrl+alt+8" } ] }"""));
            Await(shell.Extensions.Manager.InstallFromFolderAsync(binder, ExtensionKit.Yes), "install binder");
            Assert.True(Ui.PumpUntil(() => shell.Keys.KeysFor("hello.say") == "Ctrl+Alt+D8", 5), shell.Keys.KeysFor("hello.say"));

            // keybindings.json rebinds it: the user's key is first.
            Directory.CreateDirectory(profile.Paths.Config);
            File.WriteAllText(profile.Paths.KeybindingsFile, """[ { "key": "ctrl+alt+j", "command": "hello.say" } ]""");
            Await(shell.ReloadKeybindingsAsync(), "keybindings");
            Assert.Equal("Ctrl+Alt+J", shell.Keys.KeysFor("hello.say"));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task AnExtensionsDecorationAppearsInTheEditorOfAnOpenedFile()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        var file = Path.Combine(scratch.Sub("project"), "notes.txt");
        File.WriteAllText(file, "hello world\nsecond line\n");
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            Await(shell.Extensions.Manager.InstallFromFolderAsync(ExtensionKit.HelloFolder(scratch), ExtensionKit.Yes), "install");
            var tab = Await(shell.Workbench.OpenAsync(file), "open")!;
            Assert.True(Ui.PumpUntil(() => tab.Editor is { } e && e.IsAttachedToVisualTree(), 10), "no editor");
            var layer = "ext:acme.hello:hello";
            Assert.True(Ui.PumpUntil(() => tab.Editor!.Decorations[layer].Ranges.Length == 1, 10), "the extension's mark never arrived");
            var mark = tab.Editor!.Decorations[layer].Ranges[0];
            Assert.Equal((0, 5), (mark.Start, mark.End));

            // Typing before it moves it along with the text.
            tab.Document.SetSelections([Wicked.Editor.Model.Selection.Caret(0)]);
            tab.Document.Apply([new Wicked.Editor.Model.TextChange(0, 0, ">> ")], [Wicked.Editor.Model.Selection.Caret(3)]);
            Assert.Equal((3, 8), (tab.Editor.Decorations[layer].Ranges[0].Start, tab.Editor.Decorations[layer].Ranges[0].End));

            // Disabling the extension takes the mark off.
            Await(shell.Extensions.Manager.SetEnabledAsync("acme.hello", false), "disable");
            Assert.True(Ui.PumpUntil(() => tab.Editor.Decorations[layer].IsEmpty, 10), "the mark stayed after the extension was disabled");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheApiEditsTextReadsSnapshotsAndRefusesAStaleVersion()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        var file = Path.Combine(scratch.Sub("project"), "edit.txt");
        File.WriteAllText(file, "one two three\n");
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            var api = new WickedApi("test.edit", scratch.Root, shell.Extensions.Bridge, shell.Notices, shell.Extensions.Manager.Registry);
            var tab = Await(shell.Workbench.OpenAsync(file), "open")!;
            Ui.PumpUntil(() => tab.Editor is not null, 10);
            tab.Document.SetSelections([new Wicked.Editor.Model.Selection(4, 7)]);

            var snapshot = Await(api.Editors.GetActiveAsync(), "snapshot")!;
            Assert.Equal(file, snapshot.Id);
            Assert.Equal("one two three\n", snapshot.Text);
            Assert.Equal(new TextRange(4, 7), snapshot.Selections[0]);
            Assert.False(snapshot.IsDirty);

            Assert.True(Await(api.Editors.ReplaceAsync(file, new TextRange(4, 7), "2", snapshot.Version), "replace"));
            Assert.Equal("one 2 three", tab.Document.Text.Line(0));
            Assert.True(tab.Document.IsDirty);
            // The same version again is stale now.
            Assert.False(Await(api.Editors.ReplaceAsync(file, new TextRange(0, 3), "ONE", snapshot.Version), "stale"));
            Assert.Equal("one 2 three", tab.Document.Text.Line(0));

            // One undo step takes the whole replacement back.
            Assert.True(tab.Document.Undo());
            Assert.Equal("one two three", tab.Document.Text.Line(0));

            tab.Document.SetSelections([Wicked.Editor.Model.Selection.Caret(3), Wicked.Editor.Model.Selection.Caret(7)]);
            Assert.True(Await(api.Editors.InsertAtCaretsAsync("|"), "insert"));
            Assert.Equal("one| two| three", tab.Document.Text.Line(0));
            Assert.Equal([4, 9], tab.Document.Selections.Select(s => s.Active));

            Assert.Equal(2, Await(api.Editors.GetOpenAsync(), "open list").Count + 1); // this one, and the window's welcome has none
            api.Dispose();
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task EventsReachTheExtensionOffTheInterfaceThread()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        var file = Path.Combine(scratch.Sub("project"), "events.txt");
        File.WriteAllText(file, "text\n");
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            var api = new WickedApi("test.events", scratch.Root, shell.Extensions.Bridge, shell.Notices, shell.Extensions.Manager.Registry);
            var seen = new List<string>();
            var threads = new List<bool>();
            var uiThread = Environment.CurrentManagedThreadId;
            void Note(string what, int thread)
            {
                lock (seen)
                {
                    seen.Add(what);
                    threads.Add(thread != uiThread);
                }
            }

            api.Events.DocumentOpened += e => Note("opened " + Path.GetFileName(e.Document.Path), Environment.CurrentManagedThreadId);
            api.Events.DocumentSaved += e => Note("saved " + Path.GetFileName(e.Document.Path), Environment.CurrentManagedThreadId);
            api.Events.DocumentClosed += e => Note("closed " + Path.GetFileName(e.Document.Path), Environment.CurrentManagedThreadId);
            api.Events.ActiveEditorChanged += e => Note("active " + (e is null ? "none" : Path.GetFileName(e.Document.Path)), Environment.CurrentManagedThreadId);

            var tab = Await(shell.Workbench.OpenAsync(file), "open")!;
            Assert.True(Ui.PumpUntil(() => { lock (seen) return seen.Contains("opened events.txt") && seen.Contains("active events.txt"); }, 10));
            tab.Document.Apply([new Wicked.Editor.Model.TextChange(0, 0, "x")], [Wicked.Editor.Model.Selection.Caret(1)]);
            Await(shell.Workbench.SaveAsync(tab), "save");
            Assert.True(Ui.PumpUntil(() => { lock (seen) return seen.Contains("saved events.txt"); }, 10));
            Await(shell.Workbench.CloseAsync(tab, shell.Workbench.ActiveGroup), "close");
            Assert.True(Ui.PumpUntil(() => { lock (seen) return seen.Contains("closed events.txt"); }, 10));
            lock (seen) Assert.All(threads, onWorker => Assert.True(onWorker, "a handler ran on the interface thread"));

            // Taken back, an extension hears nothing more.
            api.Dispose();
            var before = seen.Count;
            Await(shell.Workbench.OpenAsync(file), "reopen");
            Ui.Pump(0.5);
            lock (seen) Assert.Equal(before, seen.Count);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task APanelsAndABottomTabAreAddedAndTakenBack()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            var api = new WickedApi("test.views", scratch.Root, shell.Extensions.Bridge, shell.Notices, shell.Extensions.Manager.Registry);
            Await(api.Views.AddSidePanelAsync("side", "Side Thing", "puzzle", () => new TextBlock { Name = "SideContent", Text = "inside the side panel" }), "side");
            Await(api.Views.AddBottomTabAsync("tab", "Mine", () => new TextBlock { Name = "TabContent", Text = "inside the tab" }), "tab");

            Assert.Single(shell.CustomPanels);
            var rail = Find<ItemsControl>(window, "RailCustomPanels");
            Ui.Pump(0.2);
            var button = rail.GetVisualDescendants().OfType<Button>().Single();
            Input.Click(window, button);
            Assert.True(shell.IsCustomPanel);
            Assert.Equal("SIDE THING", shell.PanelTitle);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "SideContent" && t.IsEffectivelyVisible), 5));

            Await(api.Views.ShowAsync("tab"), "show");
            Assert.True(shell.BottomPanel.IsVisible);
            Assert.Equal("ext:test.views:tab", shell.BottomPanel.Active?.Id);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "TabContent" && t.IsEffectivelyVisible), 5));

            api.Dispose();
            Assert.True(Ui.PumpUntil(() => shell.CustomPanels.Count == 0 && shell.BottomPanel["ext:test.views:tab"] is null, 5));
            Assert.False(shell.IsCustomPanel);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task SettingsAreReadWrittenToSettingsJsonAndAnnounced()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            var registry = shell.Extensions.Manager.Registry;
            registry.SetDefaults("test.settings", new Dictionary<string, System.Text.Json.JsonElement> { ["testsettings.greeting"] = System.Text.Json.JsonSerializer.SerializeToElement("Hi") });
            var api = new WickedApi("test.settings", scratch.Root, shell.Extensions.Bridge, shell.Notices, registry);

            Assert.Equal(14d, Await(api.Settings.GetAsync<double>("editor.fontSize"), "builtin"));
            Assert.Equal("Hi", Await(api.Settings.GetAsync<string>("testsettings.greeting"), "default"));

            var changed = new List<string>();
            api.Settings.Changed += keys =>
            {
                lock (changed) changed.AddRange(keys);
            };
            Await(shell.Extensions.Bridge.GetSettingAsync("editor.fontSize"), "prime"); // the first applied settings are the baseline
            Await(shell.ApplySettingsAsync(shell.Settings), "baseline");
            Await(api.Settings.SetAsync("testsettings.greeting", "Hey"), "set own key");
            Await(api.Settings.SetAsync("editor.fontSize", 17d), "set builtin key");

            Assert.Equal("Hey", Await(api.Settings.GetAsync<string>("testsettings.greeting"), "read back"));
            Assert.Equal(17d, shell.Settings.FontSize);
            var written = File.ReadAllText(profile.Paths.SettingsFile);
            Assert.Contains("\"testsettings.greeting\": \"Hey\"", written, StringComparison.Ordinal);
            Assert.Contains("\"editor.fontSize\": 17", written, StringComparison.Ordinal);
            Assert.True(Ui.PumpUntil(() => { lock (changed) return changed.Contains("editor.fontSize") && changed.Contains("testsettings.greeting"); }, 10), "the change was not announced");
            api.Dispose();
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task DeclaredContributionsReachTheEditorsRegistries()
    {
        using var scratch = new ScratchFolder();
        using var profile = new TempProfile();
        var folder = scratch.Sub("decl-src");
        File.WriteAllText(Path.Combine(folder, "zzz.tmLanguage.json"), """{ "scopeName": "source.zzz", "patterns": [ { "match": "\\bwidget\\b", "name": "keyword.zzz" } ] }""");
        File.WriteAllText(Path.Combine(folder, "zzz-config.json"), """{ "comments": { "lineComment": "--" }, "autoClosingPairs": [["<", ">"]] }""");
        File.WriteAllText(Path.Combine(folder, "zzz-snippets.json"), """{ "Widget": { "prefix": "wdg", "body": ["widget $1"] } }""");
        File.WriteAllText(Path.Combine(folder, "dusk.json"), "{ \"name\": \"dusk-internal\", \"colors\": {} }"); // the label is the name shown
        File.WriteAllText(Path.Combine(folder, ExtensionManifest.FileName), ExtensionKit.Manifest(id: "zzzpack", main: null, contributes: """
            { "themes": [ { "label": "Dusk", "path": "dusk.json" } ],
              "grammars": [ { "language": "zzz", "scopeName": "source.zzz", "path": "zzz.tmLanguage.json", "extensions": [".zzz"], "configuration": "zzz-config.json" } ],
              "snippets": [ { "language": "zzz", "path": "zzz-snippets.json" } ],
              "languageServers": [ { "id": "zzz-ls", "name": "Zzz", "languages": ["zzz"], "programs": ["zzz-ls"] } ],
              "keybindings": [ { "command": "workbench.action.files.save", "key": "ctrl+alt+9" } ] }
            """));
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            Await(shell.Extensions.Manager.InstallFromFolderAsync(folder, ExtensionKit.Yes), "install");

            var language = Languages.ForFile("/x/file.zzz");
            Assert.Equal(("zzz", "source.zzz"), (language?.Id, language?.Scope));
            Assert.Equal("--", language!.Options.LineComment);
            Assert.Contains(language.Options.Pairs, p => p.Open == "<" && p.Close == ">");
            var registry = new Registry(Languages.Options);
            var grammar = registry.LoadGrammar("source.zzz");
            Assert.NotNull(grammar);
            Assert.Contains("keyword.zzz", grammar.TokenizeLine("a widget b").Tokens.SelectMany(t => t.Scopes));

            Assert.True(Ui.PumpUntil(() => shell.Snippets.For("zzz").Any(s => s.Prefixes.Contains("wdg")), 10), "the snippet never arrived");
            Assert.Contains(LanguageServerCatalog.For("zzz"), s => s.Id == "zzz-ls");
            Assert.Single(shell.Extensions.Bridge.ThemeFiles.Files);
            Assert.True(Ui.PumpUntil(() => shell.Themes.All.Any(t => t.Name == "Dusk"), 10), "the contributed theme is not in the theme list");
            Assert.Equal("Ctrl+Alt+D9", shell.Keys.KeysFor("workbench.action.files.save"));

            // A file in the new language opens and is highlighted by the new grammar.
            var file = Path.Combine(scratch.Sub("project"), "thing.zzz");
            File.WriteAllText(file, "widget one\nwidget two\n");
            var tab = Await(shell.Workbench.OpenAsync(file), "open")!;
            Assert.Equal("zzz", tab.Language?.Id);
            Assert.True(Ui.PumpUntil(() => tab.Editor?.View.Syntax is { } syntax && syntax.ValidLines == tab.Document.Text.LineCount, 10), "the grammar never highlighted the file");

            Await(shell.Extensions.Manager.UninstallAsync("acme.zzzpack"), "uninstall");
            Assert.Null(Languages.ForFile("/x/file.zzz"));
            Assert.Empty(LanguageServerCatalog.For("zzz"));
            Assert.Empty(shell.Extensions.Bridge.ThemeFiles.Files);
            Assert.True(Ui.PumpUntil(() => !shell.Themes.All.Any(t => t.Name == "Dusk"), 10), "the theme stayed after its extension went");
            Assert.True(Ui.PumpUntil(() => !shell.Snippets.For("zzz").Any(), 10));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task InitScriptCommandsRunFromThePaletteAndReloadFromIt()
    {
        using var profile = new TempProfile();
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(Path.Combine(profile.Paths.Config, "init.csx"), """
            wicked.Commands.Register("mine.first", "First", () => wicked.Notices.Post("first ran", NoticeLevel.Success, "mine"), null, "Mine");
            """);
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            Assert.True(Ui.PumpUntil(() => shell.Commands.Any(c => c.Id == "mine.first"), 15), string.Join('\n', shell.Notices.All.Select(n => n.Text)));

            File.WriteAllText(Path.Combine(profile.Paths.Config, "init.csx"), """
                wicked.Commands.Register("mine.second", "Second", () => { }, null, "Mine");
                """);
            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("reload init");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "Preferences: Reload init.csx", 5));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => shell.Commands.Any(c => c.Id == "mine.second") && shell.Commands.All(c => c.Id != "mine.first"), 15), "reload did not swap the commands");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task OpenInitScriptWritesTheStarterAndOpensItAsCSharp()
    {
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Start(profile);
            Input.Chord(window, Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("open init");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "Preferences: Open init.csx", 5));
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            var path = Path.Combine(profile.Paths.Config, "init.csx");
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Path == path, 10), "init.csx was not opened");
            Assert.Contains("wicked.Commands.Register", shell.Workbench.ActiveTab!.Document.Text.ToString(), StringComparison.Ordinal);
            Assert.Equal("csharp", shell.Workbench.ActiveTab.Language?.Id);
            window.Close();
            return 0;
        });
    }
}
