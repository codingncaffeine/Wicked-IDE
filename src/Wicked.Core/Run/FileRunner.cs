using System.Collections.Immutable;
using Wicked.Core.Terminal;

namespace Wicked.Core.Run;

/// <summary>
/// The command line that runs one file with no launch.json or tasks.json ("Run Current File", as VS Code's Python and
/// Code Runner extensions and JetBrains' context run do): one or more steps, each a program and its arguments, run in
/// order while the last succeeded. Every path is absolute, so the steps work from any directory.
/// </summary>
/// <param name="Steps">The programs and their arguments; a later step runs only when the one before succeeded.</param>
/// <param name="Tool">The program a user must have installed, named in the message when it is missing.</param>
public sealed record FileRunPlan(ImmutableArray<ImmutableArray<string>> Steps, string Tool);

public static class FileRunner
{
    /// <summary>What runs each kind of file: the language id (or extension) and the interpreter for it.</summary>
    private static readonly Dictionary<string, (string Linux, string Windows)> Interpreters = new(StringComparer.Ordinal)
    {
        ["python"] = ("python3", "python"),
        ["javascript"] = ("node", "node"),
        // Node strips TypeScript's types itself since 22.18 / 23.6.
        ["typescript"] = ("node", "node"),
        ["shellscript"] = ("bash", "bash"),
        ["powershell"] = ("pwsh", "pwsh"),
        ["ruby"] = ("ruby", "ruby"),
        ["perl"] = ("perl", "perl"),
        ["php"] = ("php", "php"),
        ["lua"] = ("lua", "lua"),
        ["r"] = ("Rscript", "Rscript"),
        ["julia"] = ("julia", "julia"),
        ["dart"] = ("dart", "dart"),
        ["swift"] = ("swift", "swift"),
        ["java"] = ("java", "java"),
    };

    private static readonly Dictionary<string, string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".py"] = "python", [".pyw"] = "python",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript",
        [".ts"] = "typescript", [".mts"] = "typescript", [".cts"] = "typescript",
        [".sh"] = "shellscript", [".bash"] = "shellscript",
        [".ps1"] = "powershell",
        [".rb"] = "ruby", [".pl"] = "perl", [".php"] = "php", [".lua"] = "lua", [".r"] = "r", [".jl"] = "julia",
        [".dart"] = "dart", [".swift"] = "swift", [".java"] = "java",
        [".cs"] = "csharp", [".rs"] = "rust", [".go"] = "go", [".c"] = "c",
        [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp",
    };

    /// <summary>The language a file runs as: its editor language when Wicked knows how to run that, else its extension's.</summary>
    public static string? LanguageOf(string file, string? languageId)
    {
        var id = languageId switch
        {
            "javascriptreact" => "javascript",
            "typescriptreact" => null,
            _ => languageId,
        };
        if (id is not null && (Interpreters.ContainsKey(id) || id is "csharp" or "rust" or "go" or "c" or "cpp")) return id;
        return Extensions.GetValueOrDefault(Path.GetExtension(file));
    }

    /// <summary>
    /// How to run <paramref name="file"/>, or null when Wicked has no way (a language it does not know, a Rust file outside
    /// a Cargo project). It looks for the project file above the file, so call it from a worker.
    /// </summary>
    public static FileRunPlan? Plan(string file, string? languageId, string? workspace, bool windows)
    {
        if (LanguageOf(file, languageId) is not { } language) return null;
        if (Interpreters.TryGetValue(language, out var interpreter))
        {
            var program = windows ? interpreter.Windows : interpreter.Linux;
            ImmutableArray<string> step = language switch
            {
                "powershell" => [program, "-NoProfile", "-File", file],
                "dart" => [program, "run", file],
                _ => [program, file],
            };
            return new FileRunPlan([step], program);
        }

        switch (language)
        {
            case "go":
                return new FileRunPlan([["go", "run", file]], "go");
            case "csharp":
                // The nearest project above the file runs it; with none, .NET 10 runs the file on its own (dotnet run app.cs).
                return FindUp(file, workspace, "*.csproj") is { } project
                    ? new FileRunPlan([["dotnet", "run", "--project", project]], "dotnet")
                    : new FileRunPlan([["dotnet", "run", file]], "dotnet");
            case "rust":
                return FindUp(file, workspace, "Cargo.toml") is { } manifest ? new FileRunPlan([["cargo", "run", "--manifest-path", manifest]], "cargo") : null;
            case "c" or "cpp":
                {
                    var compiler = language == "c" ? "cc" : "c++";
                    if (windows) compiler = language == "c" ? "gcc" : "g++";
                    var output = Path.ChangeExtension(file, windows ? ".exe" : null);
                    return new FileRunPlan([[compiler, "-g", file, "-o", output], [output]], compiler);
                }

            default:
                return null;
        }
    }

    /// <summary>The first file matching <paramref name="pattern"/> in the file's folder or above it, stopping at the workspace.</summary>
    private static string? FindUp(string file, string? workspace, string pattern)
    {
        var stop = workspace is null ? null : Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(file)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            try
            {
                if (Directory.EnumerateFiles(dir, pattern).Order(StringComparer.Ordinal).FirstOrDefault() is { } found) return found;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (stop is not null && string.Equals(dir.TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.Ordinal)) break;
        }

        return null;
    }

    /// <summary>The plan as one line typed into a terminal running <paramref name="shell"/>, quoted the way that shell reads it.</summary>
    public static string Format(FileRunPlan plan, ShellKind shell)
    {
        var steps = plan.Steps.Select(step => FormatStep(step, shell)).ToList();
        if (shell == ShellKind.PowerShell)
        {
            // Windows PowerShell 5 has no &&: each later step runs only when the one before succeeded.
            var line = steps[0];
            foreach (var next in steps.Skip(1)) line += $"; if ($?) {{ {next} }}";
            return line;
        }

        return string.Join(" && ", steps);
    }

    private static string FormatStep(ImmutableArray<string> step, ShellKind shell)
    {
        var words = step.Select(w => Quote(w, shell)).ToList();
        // PowerShell runs a quoted program only through the call operator.
        if (shell == ShellKind.PowerShell && words[0] != step[0]) words[0] = "& " + words[0];
        return string.Join(' ', words);
    }

    /// <summary>A word quoted only when it needs it: single quotes in POSIX shells, fish and PowerShell, double quotes in cmd.</summary>
    public static string Quote(string word, ShellKind shell)
    {
        if (word.Length > 0 && word.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '+' or '=' or ',' or '@' || c == '\\' && shell is ShellKind.Cmd or ShellKind.PowerShell)) return word;
        return shell switch
        {
            ShellKind.Cmd => "\"" + word.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
            ShellKind.PowerShell => "'" + word.Replace("'", "''", StringComparison.Ordinal) + "'",
            ShellKind.Fish => "'" + word.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'",
            _ => "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'",
        };
    }
}
