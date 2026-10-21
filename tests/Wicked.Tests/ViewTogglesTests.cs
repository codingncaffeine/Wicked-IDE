using Avalonia.Controls;
using Avalonia.VisualTree;
using Wicked.App.ViewModels;
using Wicked.Editor.Model;
using Wicked.Editor.View;
using Wicked.Tests.Git;
using static Wicked.Tests.WorkbenchWindowTests;

namespace Wicked.Tests;

/// <summary>The View toggles (minimap, whitespace, line numbers, breadcrumbs) and Go to Next / Previous Change.</summary>
public sealed class ViewTogglesTests
{
    private static Task Run(ShellViewModel shell, string id)
    {
        var run = shell.RunCommandAsync(id);
        Assert.True(Ui.PumpUntil(() => run.IsCompleted, 5), $"{id} did not finish");
        Ui.Pump(0.2);
        return run;
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task EachToggleChangesItsSettingAndTheOpenEditor()
    {
        using var profile = new TempProfile();
        var root = Path.Combine(profile.Root, "project");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), string.Join('\n', Enumerable.Range(0, 30).Select(i => $"line {i}")));
        await Ui.Run(() =>
        {
            var (window, shell) = OpenWorkbench(profile, root);
            var start = shell.StartAsync();
            Assert.True(Ui.PumpUntil(() => start.IsCompleted, 10));
            var tab = OpenByClicks(window, shell, "a.txt");
            var view = tab.Editor!.View;
            var crumbs = window.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Crumbs");
            Assert.True(view.ShowMinimap && crumbs.IsVisible);
            var gutter = view.GutterWidth;

            Run(shell, "editor.action.toggleMinimap");
            Assert.False(view.ShowMinimap);
            Assert.False(shell.Settings.Minimap);
            Assert.Contains("\"editor.minimap.enabled\": false", File.ReadAllText(profile.Paths.SettingsFile), StringComparison.Ordinal);

            Run(shell, "editor.action.toggleRenderWhitespace");
            Assert.Equal("none", shell.Settings.RenderWhitespace);
            Run(shell, "editor.action.toggleRenderWhitespace");
            Assert.Equal("all", shell.Settings.RenderWhitespace);

            // Line numbers off: the gutter narrows to the fold arrows' room and the frame draws no numbers.
            Run(shell, "wicked.toggleLineNumbers");
            Assert.Equal(LineNumbersMode.Off, view.LineNumbers);
            Assert.True(view.GutterWidth < gutter, $"the gutter is {view.GutterWidth}, was {gutter}");
            Assert.True(view.BuildFrame().HideLineNumbers);
            Run(shell, "wicked.toggleLineNumbers");
            Assert.Equal(LineNumbersMode.On, view.LineNumbers);

            // Relative: the caret's line shows its own number, the others their distance from it.
            var relative = shell.UpdateSettingsAsync(s => s with { LineNumbers = "relative" });
            Assert.True(Ui.PumpUntil(() => relative.IsCompleted && view.LineNumbers == LineNumbersMode.Relative, 5));
            tab.Document.SetSelections([Selection.Caret(tab.Document.Text.LineStart(2))]);
            var number = view.BuildFrame().LineNumber!;
            Assert.Equal([2, 1, 3, 1, 2], Enumerable.Range(0, 5).Select(l => number(l)!.Value));

            Run(shell, "breadcrumbs.toggle");
            Assert.False(crumbs.IsVisible);
            Run(shell, "breadcrumbs.toggle");
            Assert.True(crumbs.IsVisible);
            window.Close();
            return 0;
        });
    }

    [Trait("Tier", "Window")]
    [Fact]
    public async Task NextAndPreviousChangeGoToEachBlockOfChangedLinesAndWrap()
    {
        using var profile = new TempProfile();
        var (box, _) = await GitSandbox.CreateAsync();
        using var sandbox = box;
        await box.CommitAsync("first", ("notes.txt", string.Join('\n', Enumerable.Range(0, 40).Select(i => $"line {i}")) + "\n"));
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            shell.SourceControl.Environment = box.Env;
            var opened = shell.OpenFolderAndRememberAsync(box.RepoPath);
            Assert.True(Ui.PumpUntil(() => opened.IsCompleted && Rows(window).Any(), 10), "the folder did not open");
            var tab = OpenByClicks(window, shell, "notes.txt");
            var doc = tab.Document;
            int Line() => doc.Text.LineOf(doc.Primary.Active);

            // Two blocks: lines 5-6 changed, line 30 changed.
            foreach (var line in new[] { 30, 6, 5 })
            {
                doc.SetSelections([Selection.Caret(doc.Text.LineStart(line))]);
                Edits.Type(doc, "X", EditOptions.Default);
            }

            Assert.True(Ui.PumpUntil(() => tab.Editor!.Decorations[ShellViewModel.GitLayer].Gutter.Length >= 3, 10), "no change marks");
            doc.SetSelections([Selection.Caret(0)]);
            Run(shell, "workbench.action.editor.nextChange");
            Assert.Equal(5, Line());
            Run(shell, "workbench.action.editor.nextChange");
            Assert.Equal(30, Line());
            Run(shell, "workbench.action.editor.nextChange");
            Assert.Equal(5, Line()); // wraps to the first
            Run(shell, "workbench.action.editor.previousChange");
            Assert.Equal(30, Line()); // and back round to the last
            window.Close();
            return 0;
        });
    }
}
