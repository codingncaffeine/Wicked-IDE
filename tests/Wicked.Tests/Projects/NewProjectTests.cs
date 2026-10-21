using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Wicked.Core.Projects;
using Wicked.Core.Testing;

namespace Wicked.Tests.Projects;

/// <summary>New Project: the templates, what each makes, and the dialog walked as a user walks it.</summary>
public sealed class NewProjectTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (List<string> Log, Action<string> Write) Log()
    {
        var lines = new List<string>();
        return (lines, line => { lock (lines) lines.Add(line); });
    }

    [Theory]
    [InlineData("MyApp", null)]
    [InlineData("my-app.core_2", null)]
    [InlineData("", "Give the project a name")]
    [InlineData("2fast", "Use letters, digits, '.', '-' and '_', starting with a letter")]
    [InlineData("has space", "Use letters, digits, '.', '-' and '_', starting with a letter")]
    [InlineData("../escape", "Use letters, digits, '.', '-' and '_', starting with a letter")]
    public void ANameIsCheckedBeforeAnythingIsMade(string name, string? problem) => Assert.Equal(problem, ProjectTemplates.CheckName(name));

    [Fact]
    public async Task AWrittenTemplateMakesItsFilesAGitignoreAndARepository()
    {
        using var profile = new TempProfile();
        var (log, write) = Log();
        var request = new ProjectRequest("Shapes", profile.Root, Git: true);

        var result = await ProjectCreator.CreateAsync(ProjectTemplates.Find("cmake")!, request, new ToolEnv(), write, Ct);

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.Combine(profile.Root, "Shapes", "src", "main.cpp"), result.OpenFile);
        Assert.Contains("project(Shapes LANGUAGES CXX)", File.ReadAllText(Path.Combine(request.Root, "CMakeLists.txt")), StringComparison.Ordinal);
        Assert.Equal("build/\n", File.ReadAllText(Path.Combine(request.Root, ".gitignore")));
        if (new ToolEnv().Find("git", profile.Root) is not null) Assert.True(Directory.Exists(Path.Combine(request.Root, ".git")), string.Join('\n', log));
    }

    [Fact]
    public async Task AFolderThatHoldsSomethingIsNeverWrittenInto()
    {
        using var profile = new TempProfile();
        var request = new ProjectRequest("Taken", profile.Root, Git: false);
        Directory.CreateDirectory(request.Root);
        File.WriteAllText(Path.Combine(request.Root, "mine.txt"), "the user's");

        var result = await ProjectCreator.CreateAsync(ProjectTemplates.Find("node")!, request, new ToolEnv(), _ => { }, Ct);

        Assert.False(result.Ok);
        Assert.Contains("already exists and is not empty", result.Error, StringComparison.Ordinal);
        Assert.Equal(["mine.txt"], Directory.GetFiles(request.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task ADotnetConsoleAppIsAProjectInASolutionAsVisualStudioLaysItOut()
    {
        var env = new ToolEnv();
        Assert.SkipWhen(env.Find("dotnet", Environment.CurrentDirectory) is null, "no dotnet on the PATH");
        using var profile = new TempProfile();
        var (log, write) = Log();
        var request = new ProjectRequest("Hello", profile.Root, Git: false);

        var result = await ProjectCreator.CreateAsync(ProjectTemplates.Find("dotnet-console")!, request, env, write, Ct);

        Assert.True(result.Ok, result.Error + "\n" + string.Join('\n', log));
        var solution = Path.Combine(request.Root, "Hello.slnx");
        Assert.True(File.Exists(solution), "no .slnx");
        Assert.True(File.Exists(Path.Combine(request.Root, "Hello", "Hello.csproj")), "no project");
        Assert.Contains("Hello.csproj", File.ReadAllText(solution), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(request.Root, "Hello", "Program.cs"), result.OpenFile);
        Assert.Contains(log, l => l.StartsWith("> dotnet new console", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CtrlShiftNOpensTheDialogAndCreateOpensTheNewProjectWithItsMainFile()
    {
        using var profile = new TempProfile();
        var location = Path.Combine(profile.Root, "work");
        await Ui.Run(() =>
        {
            var (window, shell) = Ui.Start(profile.Paths);
            try
            {
                Input.Chord(window, Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.True(Ui.PumpUntil(() => shell.NewProject.IsOpen && shell.NewProject.Templates.Count > 0, 10), "Ctrl+Shift+N did not open New Project");

                // A template that needs no tool, so the test does not depend on the machine.
                var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TemplateList");
                list.SelectedItem = shell.NewProject.Templates.Single(t => t.Template.Id == "python");
                window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "ProjectNameBox").Text = "Garden";
                window.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "ProjectLocationBox").Text = location;
                Ui.Pump(0.2);
                Assert.Equal("Creates " + Path.Combine(location, "Garden"), shell.NewProject.TargetText);
                Wicked.Tests.Models.ModelsUi.Shot(window, "new-project");
                var create = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CreateProject");
                Assert.True(create.IsEffectivelyEnabled);
                Input.Click(window, create);

                var main = Path.Combine(location, "Garden", "src", "garden", "__main__.py");
                Assert.True(Ui.PumpUntil(() => shell.Workbench.Root == Path.Combine(location, "Garden") && shell.Workbench.ActiveTab?.Path == main, 15),
                    $"the project did not open: root {shell.Workbench.Root}, tab {shell.Workbench.ActiveTab?.Path}, error '{shell.NewProject.Error}'");
                Assert.False(shell.NewProject.IsOpen);
                Assert.True(File.Exists(Path.Combine(location, "Garden", "pyproject.toml")));

                // Next time the dialog offers the same place.
                Input.Chord(window, Key.N, RawInputModifiers.Control | RawInputModifiers.Shift);
                Assert.True(Ui.PumpUntil(() => shell.NewProject.IsOpen, 5));
                Assert.Equal(location, shell.NewProject.Location);
                Input.Chord(window, Key.Escape, RawInputModifiers.None);
                Assert.True(Ui.PumpUntil(() => !shell.NewProject.IsOpen, 5), "Escape did not close the dialog");
            }
            finally
            {
                window.Close();
            }

            return 0;
        });
    }
}
