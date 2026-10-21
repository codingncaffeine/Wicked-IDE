using System.Text.RegularExpressions;

namespace Wicked.Core.Debug;

/// <summary>
/// The launch.json written when a workspace has none ("Create a launch.json"): the configurations that fit the language of the
/// active file (or, without one, what the folder holds), with the paths of the workspace's own program filled in where they
/// can be found. The adapter each configuration needs is named so the caller can offer to install it.
/// </summary>
public static partial class LaunchStarter
{
    public sealed record Starter(string Text, DebugAdapterSpec? Adapter, string Language);

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFramework();

    [GeneratedRegex(@"<AssemblyName>\s*([^<\s]+)\s*</AssemblyName>", RegexOptions.IgnoreCase)]
    private static partial Regex AssemblyName();

    [GeneratedRegex(@"^\s*name\s*=\s*""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex CargoName();

    /// <summary>The language ids the starter has a template for.</summary>
    public static readonly IReadOnlyList<string> Languages = ["csharp", "c", "cpp", "rust", "python", "javascript", "typescript"];

    /// <summary>Picks the language from the active file's, or from what the folder holds; null when neither says.</summary>
    public static async Task<string?> DetectAsync(string workspace, string? activeLanguage, CancellationToken ct = default)
    {
        if (activeLanguage is not null && Languages.Contains(activeLanguage)) return activeLanguage;
        if (activeLanguage is "javascriptreact") return "javascript";
        if (activeLanguage is "typescriptreact") return "typescript";
        return await Task.Run(() =>
        {
            if (FindFile(workspace, "*.csproj") is not null || FindFile(workspace, "*.sln") is not null || FindFile(workspace, "*.slnx") is not null) return "csharp";
            if (File.Exists(Path.Combine(workspace, "Cargo.toml"))) return "rust";
            if (File.Exists(Path.Combine(workspace, "package.json")) || File.Exists(Path.Combine(workspace, "tsconfig.json"))) return "javascript";
            if (File.Exists(Path.Combine(workspace, "pyproject.toml")) || File.Exists(Path.Combine(workspace, "requirements.txt")) || FindFile(workspace, "*.py") is not null) return "python";
            if (File.Exists(Path.Combine(workspace, "CMakeLists.txt")) || File.Exists(Path.Combine(workspace, "Makefile"))) return "cpp";
            return (string?)null;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The text of a launch.json for <paramref name="language"/>. Disk work (it looks for the project file): call from a worker.</summary>
    public static async Task<Starter> CreateAsync(string workspace, string language, CancellationToken ct = default)
    {
        var (configurations, adapterId) = await Task.Run(() => Templates(workspace, language), ct).ConfigureAwait(false);
        var text = "{\n" +
            "  // Debug configurations, in VS Code's format. Start one from the Run and Debug panel (F5).\n" +
            "  // ${workspaceFolder} and the other variables are filled in when a session starts.\n" +
            "  \"version\": \"0.2.0\",\n" +
            "  \"configurations\": [\n" + configurations + "\n  ]\n}\n";
        return new Starter(text, DebugAdapterCatalog.ById(adapterId), language);
    }

    private static (string Configurations, string AdapterId) Templates(string workspace, string language)
    {
        switch (language)
        {
            case "csharp":
                var project = FindFile(workspace, "*.csproj");
                var folder = "";
                var assembly = Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar, '/'));
                var framework = "net10.0";
                var cwdPath = "${workspaceFolder}";
                if (project is not null)
                {
                    var text = File.ReadAllText(project);
                    if (TargetFramework().Match(text) is { Success: true } f) framework = f.Groups[1].Value;
                    assembly = AssemblyName().Match(text) is { Success: true } a ? a.Groups[1].Value : Path.GetFileNameWithoutExtension(project);
                    var relative = Path.GetRelativePath(workspace, Path.GetDirectoryName(project)!);
                    if (relative != ".")
                    {
                        folder = relative.Replace(Path.DirectorySeparatorChar, '/') + "/";
                        cwdPath += "/" + folder.TrimEnd('/'); // not a path: launch.json writes its variables with forward slashes
                    }
                }

                return ($$"""
                    {
                      "name": ".NET: Launch",
                      "type": "coreclr",
                      "request": "launch",
                      // A task from tasks.json that builds the program.
                      "preLaunchTask": "build",
                      "program": "${workspaceFolder}/{{folder}}bin/Debug/{{framework}}/{{assembly}}.dll",
                      "args": [],
                      "cwd": "{{cwdPath}}",
                      "stopAtEntry": false,
                      "console": "internalConsole"
                    },
                    {
                      "name": ".NET: Attach to Process",
                      "type": "coreclr",
                      "request": "attach",
                      "processId": "${command:pickProcess}"
                    }
                """, "netcoredbg");
            case "rust":
                var crate = "program";
                var cargo = Path.Combine(workspace, "Cargo.toml");
                if (File.Exists(cargo) && CargoName().Match(File.ReadAllText(cargo)) is { Success: true } c) crate = c.Groups[1].Value;
                return ($$"""
                    {
                      "name": "Rust: Launch",
                      "type": "lldb",
                      "request": "launch",
                      // A task from tasks.json that runs `cargo build`.
                      "preLaunchTask": "cargo build",
                      "program": "${workspaceFolder}/target/debug/{{crate}}",
                      "args": [],
                      "cwd": "${workspaceFolder}"
                    }
                """, "codelldb");
            case "c" or "cpp":
                return ("""
                    {
                      "name": "C/C++: Launch",
                      "type": "lldb",
                      "request": "launch",
                      // A task from tasks.json that builds the program with debug information.
                      "preLaunchTask": "build",
                      "program": "${workspaceFolder}/a.out",
                      "args": [],
                      "cwd": "${workspaceFolder}"
                    },
                    {
                      "name": "C/C++: Attach to Process",
                      "type": "lldb",
                      "request": "attach",
                      "pid": "${command:pickProcess}"
                    }
                """, "codelldb");
            case "python":
                return ("""
                    {
                      "name": "Python: Current File",
                      "type": "debugpy",
                      "request": "launch",
                      "program": "${file}",
                      "console": "integratedTerminal",
                      "justMyCode": true
                    },
                    {
                      "name": "Python: Attach to Process",
                      "type": "debugpy",
                      "request": "attach",
                      "processId": "${command:pickProcess}"
                    }
                """, "debugpy");
            default:
                return ("""
                    {
                      "name": "Node: Current File",
                      "type": "pwa-node",
                      "request": "launch",
                      "program": "${file}",
                      "skipFiles": ["<node_internals>/**"]
                    },
                    {
                      "name": "Node: Attach to Process",
                      "type": "pwa-node",
                      "request": "attach",
                      "processId": "${command:pickProcess}"
                    }
                """, "js-debug");
        }
    }

    /// <summary>The first file matching a pattern in the folder or up to two levels down, skipping build and dependency folders.</summary>
    private static string? FindFile(string folder, string pattern, int depth = 2)
    {
        try
        {
            var here = Directory.GetFiles(folder, pattern).OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
            if (here is not null || depth == 0) return here;
            foreach (var sub in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith('.') || name is "bin" or "obj" or "node_modules" or "target" or "packages") continue;
                if (FindFile(sub, pattern, depth - 1) is { } found) return found;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }
}
