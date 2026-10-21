using System.Text.RegularExpressions;
using Wicked.Core.Testing;

namespace Wicked.Core.Projects;

/// <summary>One thing a template does: run a program (in the project folder, or the folder that will hold it) or write a file.</summary>
public abstract record ProjectStep;

/// <summary>Runs a program; <paramref name="InParent"/> runs it in the location, for tools that make the project folder themselves.</summary>
public sealed record RunStep(string Program, IReadOnlyList<string> Args, bool InParent = false) : ProjectStep;

/// <summary>Writes a file, relative to the project folder.</summary>
public sealed record WriteStep(string Path, string Text) : ProjectStep;

/// <summary>A project to make: its name, the folder it goes in, and whether it starts as a git repository.</summary>
public sealed record ProjectRequest(string Name, string Location, bool Git)
{
    public string Root => System.IO.Path.Combine(Location, Name);
}

/// <summary>
/// A kind of project, as Visual Studio and Rider list them: the language, what it makes, the program it needs (null: Wicked
/// writes every file itself), the steps, the file opened once it exists, and the lines its .gitignore starts with.
/// </summary>
public sealed record ProjectTemplate(
    string Id,
    string Language,
    string Name,
    string Description,
    string? Tool,
    Func<ProjectRequest, IReadOnlyList<ProjectStep>> Steps,
    Func<ProjectRequest, string?> OpenFile,
    string GitIgnore);

/// <summary>The templates Wicked offers. Each language's own tool makes its projects (dotnet new, cargo new, go mod init),
/// as an IDE that ships them does; where a language has no such tool the few starter files are written here.</summary>
public static partial class ProjectTemplates
{
    private const string DotnetIgnore = "bin/\nobj/\n.vs/\n*.user\n";

    public static IReadOnlyList<ProjectTemplate> All { get; } =
    [
        Dotnet("dotnet-console", "Console App", "A command-line program, in a solution (.slnx) of its own.", "console", "Program.cs"),
        Dotnet("dotnet-classlib", "Class Library", "A library other projects reference, in a solution (.slnx).", "classlib", "Class1.cs"),
        Dotnet("dotnet-webapi", "ASP.NET Core Web API", "An HTTP API with minimal APIs, in a solution (.slnx).", "webapi", "Program.cs"),
        Dotnet("dotnet-xunit", "xUnit Test Project", "Tests run with xUnit, in a solution (.slnx).", "xunit", "UnitTest1.cs"),
        new("rust-bin", "Rust", "Binary", "A program built with Cargo (cargo new).", "cargo",
            r => [new RunStep("cargo", ["new", r.Name, "--vcs", "none"], InParent: true)], _ => Path.Combine("src", "main.rs"), "/target\n"),
        new("rust-lib", "Rust", "Library", "A crate other crates depend on (cargo new --lib).", "cargo",
            r => [new RunStep("cargo", ["new", r.Name, "--lib", "--vcs", "none"], InParent: true)], _ => Path.Combine("src", "lib.rs"), "/target\n"),
        new("go-module", "Go", "Module", "A Go module with a main package (go mod init).", "go",
            r => [new RunStep("go", ["mod", "init", ModuleName(r.Name)]), new WriteStep("main.go", "package main\n\nimport \"fmt\"\n\nfunc main() {\n\tfmt.Println(\"Hello from " + r.Name + "\")\n}\n")],
            _ => "main.go", ""),
        new("node", "JavaScript", "Node.js App", "A package.json and an index.js, run with node.", "node",
            r => [new WriteStep("package.json", PackageJson(r.Name)), new WriteStep("index.js", $"console.log(\"Hello from {r.Name}\");\n")],
            _ => "index.js", "node_modules/\n"),
        new("python", "Python", "Python Package", "A pyproject.toml and a package to start from.", null,
            r => [new WriteStep("pyproject.toml", PyProject(r.Name)), new WriteStep(Path.Combine("src", PackageName(r.Name), "__init__.py"), ""),
                  new WriteStep(Path.Combine("src", PackageName(r.Name), "__main__.py"), $"def main() -> None:\n    print(\"Hello from {r.Name}\")\n\n\nif __name__ == \"__main__\":\n    main()\n")],
            r => Path.Combine("src", PackageName(r.Name), "__main__.py"), "__pycache__/\n.venv/\n*.egg-info/\n"),
        new("cmake", "C++", "CMake Project", "A CMakeLists.txt and a main.cpp, built with CMake.", null,
            r => [new WriteStep("CMakeLists.txt", $"cmake_minimum_required(VERSION 3.20)\nproject({CIdentifier(r.Name)} LANGUAGES CXX)\n\nset(CMAKE_CXX_STANDARD 20)\nset(CMAKE_CXX_STANDARD_REQUIRED ON)\nset(CMAKE_EXPORT_COMPILE_COMMANDS ON)\n\nadd_executable({CIdentifier(r.Name)} src/main.cpp)\n"),
                  new WriteStep(Path.Combine("src", "main.cpp"), $"#include <iostream>\n\nint main() {{\n    std::cout << \"Hello from {r.Name}\\n\";\n    return 0;\n}}\n")],
            _ => Path.Combine("src", "main.cpp"), "build/\n"),
        new("empty", "Other", "Empty Folder", "A new folder and nothing in it.", null, _ => [], _ => null, ""),
    ];

    /// <summary>A .NET project in a solution, the way Visual Studio lays it out: Location/Name/Name.slnx and Location/Name/Name/Name.csproj.</summary>
    private static ProjectTemplate Dotnet(string id, string name, string description, string template, string openFile) =>
        new(id, "C#", name, description, "dotnet",
            r =>
            [
                new RunStep("dotnet", ["new", "sln", "--name", r.Name, "--format", "slnx"]),
                new RunStep("dotnet", ["new", template, "--name", r.Name, "--output", r.Name]),
                new RunStep("dotnet", ["sln", r.Name + ".slnx", "add", Path.Combine(r.Name, r.Name + ".csproj")]),
            ],
            r => Path.Combine(r.Name, openFile),
            DotnetIgnore);

    public static ProjectTemplate? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Why a name cannot be a project's, or null when it can.</summary>
    public static string? CheckName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the project a name";
        if (!NameRule().IsMatch(name)) return "Use letters, digits, '.', '-' and '_', starting with a letter";
        return name.Length > 100 ? "That name is too long" : null;
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]*$")]
    private static partial Regex NameRule();

    private static string ModuleName(string name) => name.ToLowerInvariant();

    private static string PackageName(string name) => new([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_')]);

    private static string CIdentifier(string name) => new([.. name.Select(c => char.IsLetterOrDigit(c) ? c : '_')]);

    private static string PackageJson(string name) =>
        "{\n  \"name\": \"" + name.ToLowerInvariant() + "\",\n  \"version\": \"0.1.0\",\n  \"private\": true,\n  \"main\": \"index.js\",\n  \"scripts\": {\n    \"start\": \"node index.js\"\n  }\n}\n";

    private static string PyProject(string name) =>
        "[project]\nname = \"" + name.ToLowerInvariant() + "\"\nversion = \"0.1.0\"\nrequires-python = \">=3.10\"\n\n[build-system]\nrequires = [\"setuptools>=68\"]\nbuild-backend = \"setuptools.build_meta\"\n";

    /// <summary>Whether a template can be used on this machine: its program is on the PATH (or it needs none).</summary>
    public static bool IsAvailable(ProjectTemplate template, ToolEnv env, string workingDirectory) =>
        template.Tool is null || env.Find(template.Tool, workingDirectory) is not null;
}
