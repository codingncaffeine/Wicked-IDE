using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.App.Editor;
using Wicked.App.Language;
using Wicked.App.Shell;
using Wicked.App.ViewModels;
using Wicked.App.Views;
using Wicked.App.Workbench;
using Wicked.Editor.Features;
using Wicked.Editor.Model;

namespace Wicked.Tests.Lsp;

/// <summary>
/// The language server in the window, the way a user meets it: a C file opened from a folder, its problem squiggled and
/// listed, a suggestion typed and accepted, a definition jumped to, a symbol renamed — against a real clangd.
/// </summary>
public sealed class LanguageWindowTests
{
    private static string? Clangd => new[] { "/usr/bin/clangd", "/usr/local/bin/clangd" }.FirstOrDefault(File.Exists);

    private const string Source = """
        #include <stdio.h>

        static int twice(int value) { return value * 2; }

        int main(void) {
            int result = twice(21);
            printf("%d\n", result);
            return undefined_name;
        }
        """;

    private static (MainWindow Window, ShellViewModel Shell, EditorTab Tab, CodeEditor Editor) Open(TempProfile profile, string source = Source)
    {
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.c"), source);
        var (window, shell) = WorkbenchWindowTests.OpenWorkbench(profile, root);
        var tab = WorkbenchWindowTests.OpenByClicks(window, shell, "main.c");
        return (window, shell, tab, tab.Editor!);
    }

    private static bool Squiggled(CodeEditor editor, string word)
    {
        var text = editor.Document.Text;
        return editor.Decorations[LanguageFeatures.DiagnosticsLayer].Ranges.Any(r => r.Style == RangeStyle.Error && r.End > r.Start && text.Substring(r.Start, r.End - r.Start) == word);
    }

    [Trait("Tier", "Smoke")]
    [Fact]
    public async Task AProblemIsSquiggledListedAndCountedAndFollowsTheTextAsItIsEdited()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, editor) = Open(profile);
            Assert.True(Ui.PumpUntil(() => Squiggled(editor, "undefined_name"), 30), "no squiggle under undefined_name");
            Assert.Equal(1, shell.Language.Problems.Errors);
            var problem = shell.Language.Problems.Files.Single().Items.Single(p => p.IsError);
            Assert.Equal(7, problem.Line);

            // The status bar counts it; a click there opens Problems with the file and its problem.
            var count = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ErrorCount");
            Assert.Equal("1", count.Text);
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ProblemsStatus"));
            Assert.True(shell.BottomPanel.IsVisible);
            Assert.Equal(PanelIds.Problems, shell.BottomPanel.Active?.Id);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<ProblemsView>().Any(v => v.IsEffectivelyVisible), 5));

            // Back in the text (the click took the keyboard), a line typed above it: the mark moves with its text at once,
            // before the server has said anything new.
            Input.Click(window, editor);
            Assert.True(Ui.PumpUntil(() => editor.IsFocused, 5));
            var doc = tab.Document;
            doc.SetSelections([Selection.Caret(0)]);
            window.KeyTextInput("// a comment");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Pump(0.05);
            Assert.True(Squiggled(editor, "undefined_name"), "the mark did not follow its text");

            // Fixed: the server takes the problem back and the count drops to nothing.
            var at = doc.Text.ToString().IndexOf("undefined_name", StringComparison.Ordinal);
            doc.SetSelections([new Selection(at, at + "undefined_name".Length)]);
            window.KeyTextInput("result");
            Assert.True(Ui.PumpUntil(() => shell.Language.Problems.Errors == 0, 30), "the problem stayed after the fix");
            Assert.False(Squiggled(editor, "undefined_name"));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TypingOpensSuggestionsAndEnterPutsTheChosenOneIn()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, editor) = Open(profile);
            var doc = tab.Document;
            Assert.True(Ui.PumpUntil(() => shell.Language.DocumentFor(tab)?.Client is not null, 30), "clangd did not take the document");
            var ui = shell.Language.UiFor(editor)!;

            // A new line in main: "tw" brings up twice, Enter puts it in.
            var line = doc.Text.LineStart(5) + doc.Text.LineLength(5);
            doc.SetSelections([Selection.Caret(line)]);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            window.KeyTextInput("tw");
            Assert.True(Ui.PumpUntil(() => ui.SuggestionsOpen && ui.Suggestions.Any(s => s.Label.Contains("twice", StringComparison.Ordinal)), 20), "no suggestion of twice");
            Assert.Contains("twice", ui.Suggestions[0].Label, StringComparison.Ordinal);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<ListBox>().Any(l => l.Name == "SuggestItems" && l.IsEffectivelyVisible), 5), "the list is not on screen");

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => doc.Text.Line(6).Trim().StartsWith("twice(", StringComparison.Ordinal), 5), $"line is '{doc.Text.Line(6)}'");
            Assert.False(ui.SuggestionsOpen);
            Assert.Equal(1, doc.Text.LineCount - Source.Split('\n').Length);          // Enter accepted; it did not also break the line
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task F12JumpsToTheDefinitionAndF2RenamesEveryUse()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, editor) = Open(profile);
            var doc = tab.Document;
            Assert.True(Ui.PumpUntil(() => shell.Language.DocumentFor(tab)?.Client is not null, 30), "clangd did not take the document");

            var call = doc.Text.ToString().IndexOf("twice(21)", StringComparison.Ordinal) + 2;
            doc.SetSelections([Selection.Caret(call)]);
            Input.Chord(window, Key.F12, RawInputModifiers.None);
            Assert.True(Ui.PumpUntil(() => doc.Text.LineOf(doc.Primary.Active) == 2, 15), "F12 did not reach the definition");

            Input.Chord(window, Key.F2, RawInputModifiers.None);
            var ui = shell.Language.UiFor(editor)!;
            Assert.True(Ui.PumpUntil(() => ui.RenameOpen, 15), "the rename box did not open");
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "RenameInput");
            Assert.Equal("twice", box.Text);
            Assert.True(Ui.PumpUntil(() => box.IsFocused, 5));
            box.SelectAll();
            window.KeyTextInput("doubled");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.True(Ui.PumpUntil(() => doc.Text.ToString().Contains("doubled(21)", StringComparison.Ordinal), 15), "the call was not renamed");
            Assert.Contains("static int doubled(int value)", doc.Text.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("twice", doc.Text.ToString(), StringComparison.Ordinal);
            // One undo takes the whole rename back.
            Input.Chord(window, Key.Z, RawInputModifiers.Control);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(doc.Text.ToString(), "twice"));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task TheHoverShowsTheSymbolAndTheProblemUnderThePointer()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, editor) = Open(profile);
            var doc = tab.Document;
            Assert.True(Ui.PumpUntil(() => Squiggled(editor, "undefined_name"), 30));

            // The pointer rests on the call of twice: its signature comes up in a card.
            var call = doc.Text.ToString().IndexOf("twice(21)", StringComparison.Ordinal) + 2;
            var at = editor.RectOf(call).Center;
            var inWindow = editor.TranslatePoint(at, window)!.Value;
            window.MouseMove(inWindow);
            Assert.True(Ui.PumpUntil(() => Card(window)?.IsEffectivelyVisible == true, 10), "no hover card");
            Assert.Contains(Texts(Card(window)!), t => t.Contains("twice", StringComparison.Ordinal));

            // On the problem: the card says what is wrong.
            var bad = doc.Text.ToString().IndexOf("undefined_name", StringComparison.Ordinal) + 3;
            window.MouseMove(editor.TranslatePoint(editor.RectOf(bad).Center, window)!.Value);
            Assert.True(Ui.PumpUntil(() => Card(window) is { IsEffectivelyVisible: true } c && Texts(c).Any(t => t.Contains("undeclared", StringComparison.OrdinalIgnoreCase)), 10), "the card did not show the problem");
            window.Close();
            return 0;
        });

        static Border? Card(Window window) => window.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "HoverCard");

        static IEnumerable<string> Texts(Control c) => c.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? string.Concat(t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text) ?? []));
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task NamesTakeTheServersSemanticColoursOverTheGrammars()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, _, tab, editor) = Open(profile);
            var doc = tab.Document;
            var column = doc.Text.Line(2).IndexOf("twice", StringComparison.Ordinal);
            bool Coloured() => editor.Semantic is { } s && s.Lines.TryGetValue(2, out var line)
                && Enumerable.Range(0, line.Starts.Length).Any(i => line.Starts[i] == column && line.Ends[i] == column + 5);
            Assert.True(Ui.PumpUntil(Coloured, 30), "no semantic colour on twice");

            // An edit on another line keeps them; a fresh set follows the edit.
            doc.SetSelections([Selection.Caret(0)]);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Ui.Pump(0.05);
            Assert.True(Ui.PumpUntil(() => editor.Semantic is { } s && s.Version == doc.Version && s.Lines.ContainsKey(3), 15), "the colours were not asked for again");
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ParameterNamesAreDrawnBeforeTheArgumentsAsHints()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, _, tab, editor) = Open(profile);
            var argument = tab.Document.Text.ToString().IndexOf("twice(21)", StringComparison.Ordinal) + "twice(".Length;
            Assert.True(Ui.PumpUntil(() => editor.Decorations[EditorLanguageUi.InlayLayer].Inline.Any(i => i.Offset == argument && i.Text.StartsWith("value", StringComparison.Ordinal)), 30),
                "no hint for the parameter: " + string.Join(", ", editor.Decorations[EditorLanguageUi.InlayLayer].Inline.Select(i => $"{i.Offset}:{i.Text}")));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task SettingsListTheServersFoundAndTheirSwitchesWriteSettings()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            Input.Chord(window, Key.OemComma, RawInputModifiers.Control);
            Assert.True(shell.SettingsOpen);
            Input.Click(window, window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "JumpLanguages"));
            var rows = shell.SettingsSheet.LanguageServers.Servers;
            Assert.True(Ui.PumpUntil(() => rows.Single(r => r.Spec.Id == "clangd").Installed, 10), "clangd was not found");
            Assert.False(rows.Single(r => r.Spec.Id == "clangd").CanInstall);
            Assert.True(Ui.PumpUntil(() => window.GetVisualDescendants().OfType<ItemsControl>().Any(i => i.Name == "ServerList" && i.IsEffectivelyVisible), 5));

            var formatOnSave = window.GetVisualDescendants().OfType<ToggleSwitch>().Single(t => t.Name == "FormatOnSaveSwitch");
            formatOnSave.BringIntoView();
            Ui.Pump(0.2);
            Input.Click(window, formatOnSave);
            Assert.True(Ui.PumpUntil(() => shell.Settings.FormatOnSave, 5), $"the switch did not turn it on (checked {formatOnSave.IsChecked})");
            Assert.True(Ui.PumpUntil(() => File.Exists(profile.Paths.SettingsFile) && File.ReadAllText(profile.Paths.SettingsFile).Contains("\"editor.formatOnSave\": true", StringComparison.Ordinal), 5));
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task ShiftAltFFormatsTheFileWithTheServer()
    {
        Assert.SkipUnless(Clangd is not null, "clangd is not installed");
        using var profile = new TempProfile();
        await Ui.Run(() =>
        {
            var (window, shell, tab, _) = Open(profile, "int main(void){int x=1;return x;}\n");
            var doc = tab.Document;
            Assert.True(Ui.PumpUntil(() => shell.Language.DocumentFor(tab)?.Client is not null, 30));
            Input.Chord(window, Key.F, RawInputModifiers.Shift | RawInputModifiers.Alt);
            Assert.True(Ui.PumpUntil(() => doc.Text.LineCount > 2, 15), $"not formatted: {doc.Text}");
            Assert.Contains("int x = 1;", doc.Text.ToString(), StringComparison.Ordinal);
            window.Close();
            return 0;
        });
    }
}

/// <summary>Semantic tokens: the protocol's relative integers decoded into colours by line, against a real theme.</summary>
public sealed class SemanticDecoderTests
{
    [Fact]
    public void RelativeTokensBecomeStretchesOnTheirLinesInTheThemesColours()
    {
        var text = new Wicked.Editor.Model.Document("int twice(int value);\nint main(void) { return twice(2); }").Text;
        var legend = new Wicked.Core.Lsp.SemanticTokensLegend(["function", "parameter", "unknownType"], ["declaration", "readonly"]);
        // twice (line 0, column 4, 5 long, a function's declaration); value (same line, 10 on, a parameter); twice on the
        // next line at 24; a token of a type the theme has no colour for.
        int[] data = [0, 4, 5, 0, 1, 0, 10, 5, 1, 0, 1, 24, 5, 0, 0, 0, 6, 1, 2, 0];
        var palette = Wicked.Editor.Syntax.SemanticPalette.For(TextMateSharp.Grammars.ThemeName.DarkPlus);
        var colors = SemanticDecoder.Decode(data, legend, palette, text, 7);
        Assert.Equal(7, colors.Version);
        var first = colors.Lines[0];
        Assert.Equal([4, 14], first.Starts);
        Assert.Equal([9, 19], first.Ends);
        var function = palette.ColorFor("function", []);
        Assert.NotEqual(0u, function);
        Assert.Equal(function, first.Colors[0]);
        Assert.NotEqual(function, first.Colors[1]);                     // a parameter is coloured as one
        Assert.Equal([24], colors.Lines[1].Starts);                    // the unknown type got no colour: the grammar's stays
        Assert.Equal("int main(void) { return twice(2); }", colors.Lines[1].Text);
    }
}
