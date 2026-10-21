using System.Collections.Immutable;
using System.Text.RegularExpressions;

namespace Wicked.Core.Tasks;

/// <summary>
/// What <c>${...}</c> variables are worked out from: the open folder, the file in the active editor, and the answers to
/// the task's inputs. Taken on the UI thread when a task is started, so the task sees the editor as it was then.
/// </summary>
/// <param name="WorkspaceFolder">The open folder, or empty.</param>
/// <param name="File">The active editor's file; null with no file open.</param>
/// <param name="LineNumber">The caret's line (1-based); 0 with no editor.</param>
/// <param name="SelectedText">The selected text; empty with none.</param>
/// <param name="Inputs">The answers to the task's inputs, by id.</param>
/// <param name="Environment">Reads an environment variable (the process's own when null).</param>
public sealed record VariableContext(string WorkspaceFolder, string? File = null, int LineNumber = 0, string SelectedText = "", ImmutableDictionary<string, string>? Inputs = null, Func<string, string?>? Environment = null)
{
    public VariableContext WithInputs(ImmutableDictionary<string, string> inputs) => this with { Inputs = inputs };
}

/// <summary>The result of expanding variables: the text, and every variable that could not be worked out.</summary>
public sealed record Expanded(string Text, ImmutableArray<string> Unresolved);

/// <summary>
/// VS Code's predefined variables in task strings: <c>${workspaceFolder}</c>, <c>${file}</c>, <c>${relativeFile}</c>,
/// <c>${fileDirname}</c>, <c>${lineNumber}</c>, <c>${env:NAME}</c>, <c>${input:id}</c> and the rest of that family. A
/// variable that cannot be worked out (<c>${file}</c> with no editor open) is left as written and reported.
/// </summary>
public static partial class TaskVariables
{
    [GeneratedRegex(@"\$\{([^}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();

    /// <summary>The ids of the inputs a string asks for.</summary>
    public static IEnumerable<string> InputsIn(string text) =>
        Variable().Matches(text).Select(m => m.Groups[1].Value).Where(v => v.StartsWith("input:", StringComparison.Ordinal)).Select(v => v["input:".Length..]);

    public static Expanded Expand(string text, VariableContext context)
    {
        if (!text.Contains("${", StringComparison.Ordinal)) return new Expanded(text, []);
        var unresolved = new List<string>();
        var result = Variable().Replace(text, m =>
        {
            var value = Resolve(m.Groups[1].Value, context);
            if (value is not null) return value;
            unresolved.Add(m.Value);
            return m.Value;
        });
        return new Expanded(result, [.. unresolved.Distinct()]);
    }

    private static string? Resolve(string name, VariableContext c)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0)
        {
            var kind = name[..colon];
            var arg = name[(colon + 1)..];
            switch (kind)
            {
                case "env":
                    return (c.Environment ?? Environment.GetEnvironmentVariable)(arg) ?? "";
                case "input":
                    return c.Inputs is not null && c.Inputs.TryGetValue(arg, out var answer) ? answer : null;
                default:
                    return null;
            }
        }

        var workspace = c.WorkspaceFolder;
        var file = c.File;
        switch (name)
        {
            case "workspaceFolder" or "fileWorkspaceFolder": return workspace.Length > 0 ? workspace : null;
            case "workspaceFolderBasename": return workspace.Length > 0 ? Path.GetFileName(workspace.TrimEnd(Path.DirectorySeparatorChar)) : null;
            case "cwd": return workspace.Length > 0 ? workspace : Directory.GetCurrentDirectory();
            case "userHome": return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            case "pathSeparator" or "/": return Path.DirectorySeparatorChar.ToString();
            case "lineNumber": return c.LineNumber > 0 ? c.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            case "selectedText": return c.File is null ? null : c.SelectedText;
        }

        if (file is null) return null;
        switch (name)
        {
            case "file": return file;
            case "fileBasename": return Path.GetFileName(file);
            case "fileBasenameNoExtension": return Path.GetFileNameWithoutExtension(file);
            case "fileExtname": return Path.GetExtension(file);
            case "fileDirname": return Path.GetDirectoryName(file) ?? "";
            case "fileDirnameBasename": return Path.GetFileName(Path.GetDirectoryName(file) ?? "");
            case "relativeFile": return workspace.Length > 0 ? Path.GetRelativePath(workspace, file) : file;
            case "relativeFileDirname":
                return workspace.Length > 0 ? Path.GetDirectoryName(Path.GetRelativePath(workspace, file)) ?? "" : Path.GetDirectoryName(file) ?? "";
            default: return null;
        }
    }
}
