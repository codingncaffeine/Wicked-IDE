using Avalonia.Headless;
using Avalonia.Input;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>The user's snippets through the window: picked from the palette, typed as a prefix, filled in with Tab.</summary>
public sealed class SnippetWindowTests
{
    private const string ForLoop = """
        {
          // A loop, as a user might keep one.
          "For loop": {
            "prefix": ["fori"],
            "body": ["for (int ${1:i} = 0; $1 < ${2:count}; $1++)", "{", "\t$0", "}"],
            "description": "Counting loop"
          }
        }
        """;

    private static string MakeFolder(TempProfile profile, string? settings = null)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "App.cs"), "class App\n{\n}\n");
        Directory.CreateDirectory(Path.Combine(profile.Paths.Config, "snippets"));
        File.WriteAllText(Path.Combine(profile.Paths.Config, "snippets", "csharp.json"), ForLoop);
        if (settings is not null) File.WriteAllText(profile.Paths.SettingsFile, settings);
        return root;
    }

    /// <summary>The app as it starts for a user (settings and snippets read), with the folder open.</summary>
    private static (App.Views.MainWindow Window, App.ViewModels.ShellViewModel Shell) Open(TempProfile profile, string folder)
    {
        var (window, shell) = Ui.Start(profile.Paths);
        var start = shell.StartAsync();
        Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10), "settings did not load");
        var opened = shell.OpenFolderAndRememberAsync(folder);
        Assert.True(Ui.PumpUntil(() => opened.IsCompleted && Rows(window).Any(), 10), "the folder did not open");
        return (window, shell);
    }

    private static void Key(Avalonia.Controls.Window window, Avalonia.Input.Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Ui.Pump(0.05);
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task InsertSnippetFillsItsPlaceholdersWithTabAndMirrorsThem()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, folder);
            var tab = OpenByClicks(window, shell, "App.cs");
            Key(window, Avalonia.Input.Key.End, RawInputModifiers.Control);

            Input.Chord(window, Avalonia.Input.Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("insert snippet");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "Snippets: Insert Snippet", 5));
            Key(window, Avalonia.Input.Key.Enter);
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "For loop", 5), "the snippet list did not open");
            Key(window, Avalonia.Input.Key.Enter);
            Assert.True(Ui.PumpUntil(() => tab.Editor!.InSnippet && tab.Editor.IsFocused, 5), "the snippet did not go in");

            // The first placeholder is selected; what is typed there shows in its mirrors.
            Assert.Equal("i", tab.Document.Text.Substring(tab.Document.Primary.Start, tab.Document.Primary.Length));
            window.KeyTextInput("k");
            Ui.Pump(0.1);
            Assert.EndsWith("for (int k = 0; k < count; k++)\n{\n    \n}", tab.Document.Text.ToString());

            Key(window, Avalonia.Input.Key.Tab);
            Assert.Equal("count", tab.Document.Text.Substring(tab.Document.Primary.Start, tab.Document.Primary.Length));
            Key(window, Avalonia.Input.Key.Tab, RawInputModifiers.Shift);
            Assert.Equal("k", tab.Document.Text.Substring(tab.Document.Primary.Start, tab.Document.Primary.Length));
            Key(window, Avalonia.Input.Key.Tab);
            window.KeyTextInput("items.Length");
            Key(window, Avalonia.Input.Key.Tab);

            // The last Tab lands inside the body and ends the snippet; the next Tab indents as usual.
            Assert.False(tab.Editor!.InSnippet);
            window.KeyTextInput("Use(k);");
            Ui.Pump(0.1);
            Assert.Equal("class App\n{\n}\nfor (int k = 0; k < items.Length; k++)\n{\n    Use(k);\n}", tab.Document.Text.ToString());

            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task APrefixAndTabExpandOnlyWhenTabCompletionIsOn()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile, "{ \"editor.tabCompletion\": \"on\" }");
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, folder);
            var tab = OpenByClicks(window, shell, "App.cs");
            Key(window, Avalonia.Input.Key.End, RawInputModifiers.Control);
            window.KeyTextInput("fori");
            Key(window, Avalonia.Input.Key.Tab);
            Assert.True(tab.Editor!.InSnippet);
            Assert.EndsWith("for (int i = 0; i < count; i++)\n{\n    \n}", tab.Document.Text.ToString());
            Key(window, Avalonia.Input.Key.Escape);
            Assert.False(tab.Editor.InSnippet);

            // Off (VS Code's default): Tab after a prefix is only a Tab.
            File.WriteAllText(profile.Paths.SettingsFile, "{ \"editor.tabCompletion\": \"off\" }");
            Assert.True(Ui.PumpUntil(() => !tab.Editor.TabCompletion, 5), "the setting did not apply");
            Key(window, Avalonia.Input.Key.End, RawInputModifiers.Control);
            Key(window, Avalonia.Input.Key.Enter);
            window.KeyTextInput("fori");
            Key(window, Avalonia.Input.Key.Tab);
            Assert.False(tab.Editor.InSnippet);
            Assert.EndsWith("}\nfori    ", tab.Document.Text.ToString());
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ConfigureSnippetsOpensTheLanguagesFileAndItsEditsApplyAtOnce()
    {
        using var profile = new TempProfile();
        var folder = MakeFolder(profile);
        File.Delete(Path.Combine(profile.Paths.Config, "snippets", "csharp.json"));
        await Ui.Run(() =>
        {
            var (window, shell) = Open(profile, folder);
            OpenByClicks(window, shell, "App.cs");
            Input.Chord(window, Avalonia.Input.Key.P, RawInputModifiers.Control | RawInputModifiers.Shift);
            window.KeyTextInput("configure snippets");
            Assert.True(Ui.PumpUntil(() => shell.QuickInput.Items.FirstOrDefault()?.Label == "Snippets: Configure Snippets", 5));
            Key(window, Avalonia.Input.Key.Enter);
            var file = Path.Combine(profile.Paths.Config, "snippets", "csharp.json");
            Assert.True(Ui.PumpUntil(() => shell.Workbench.ActiveTab?.Path == file, 5), "the snippet file did not open");
            Assert.Contains("\"Example\"", shell.Workbench.ActiveTab!.Document.Text.ToString());
            Assert.True(Ui.PumpUntil(() => shell.Snippets.For("csharp").Any(s => s.Name == "Example"), 5), "the starter file was not read");

            // A snippet saved to the file is offered without a restart.
            File.WriteAllText(file, ForLoop);
            Assert.True(Ui.PumpUntil(() => shell.Snippets.For("csharp").Any(s => s.Name == "For loop"), 5), "the edited file was not read again");
            Assert.Empty(shell.Snippets.For("python"));
            window.Close();
            return 0;
        });
    }
}
