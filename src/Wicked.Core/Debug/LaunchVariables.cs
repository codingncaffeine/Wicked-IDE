using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Wicked.Core.Debug;

/// <summary>What the variables of a launch configuration stand for: the workspace, the active file and selection, the environment.</summary>
public sealed record LaunchContext
{
    public required string WorkspaceFolder { get; init; }

    public string? ActiveFile { get; init; }

    public string? SelectedText { get; init; }

    /// <summary>The active editor's caret line, counting from 1.</summary>
    public int? LineNumber { get; init; }

    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The value of a setting (<c>${config:name}</c>); null when it is not set.</summary>
    public Func<string, string?>? Config { get; init; }

    /// <summary>Asks the user for an <c>${input:id}</c> (a prompt, or a pick); null when they cancel. Runs on the UI's terms, never blocking it.</summary>
    public Func<LaunchInput, CancellationToken, Task<string?>>? Ask { get; init; }

    /// <summary>Runs a <c>${command:name}</c> (<c>pickProcess</c>, an extension's command); null when it was cancelled or is unknown.</summary>
    public Func<string, CancellationToken, Task<string?>>? Command { get; init; }

    public string UserHome { get; init; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The inputs launch.json declares, so <c>${input:id}</c> can find its prompt.</summary>
    public IReadOnlyList<LaunchInput> Inputs { get; init; } = [];

    /// <summary>The working directory <c>${cwd}</c> stands for (the folder the IDE was started in).</summary>
    public string Cwd { get; init; } = Environment.CurrentDirectory;
}

/// <summary>
/// Fills in the <c>${…}</c> variables of a launch configuration's strings, VS Code's set: <c>workspaceFolder</c>,
/// <c>file</c> and its parts, <c>relativeFile</c>, <c>lineNumber</c>, <c>selectedText</c>, <c>cwd</c>, <c>userHome</c>,
/// <c>pathSeparator</c>, <c>env:NAME</c>, <c>config:name</c>, <c>input:id</c> and <c>command:name</c>. A variable that cannot be
/// filled in (no file is open for <c>${file}</c>) is an error that says which, not an empty string.
/// </summary>
public static partial class LaunchVariables
{
    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex Pattern();

    /// <summary>A copy of the configuration with every variable in its string values filled in.</summary>
    public static async Task<JsonObject> ResolveAsync(JsonObject configuration, LaunchContext context, CancellationToken ct = default)
    {
        var answers = new Dictionary<string, string>(StringComparer.Ordinal);
        var resolved = await ResolveNodeAsync(configuration, context, answers, ct).ConfigureAwait(false);
        return (JsonObject)resolved!;
    }

    public static async Task<string> ResolveAsync(string text, LaunchContext context, CancellationToken ct = default) =>
        await ResolveStringAsync(text, context, new Dictionary<string, string>(StringComparer.Ordinal), ct).ConfigureAwait(false);

    private static async Task<JsonNode?> ResolveNodeAsync(JsonNode? node, LaunchContext context, Dictionary<string, string> answers, CancellationToken ct)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (key, value) in o) copy[key] = await ResolveNodeAsync(value, context, answers, ct).ConfigureAwait(false);
                return copy;
            case JsonArray a:
                var list = new JsonArray();
                foreach (var item in a) list.Add(await ResolveNodeAsync(item, context, answers, ct).ConfigureAwait(false));
                return list;
            case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                return JsonValue.Create(await ResolveStringAsync(v.GetValue<string>(), context, answers, ct).ConfigureAwait(false));
            default:
                return node?.DeepClone();
        }
    }

    private static async Task<string> ResolveStringAsync(string text, LaunchContext context, Dictionary<string, string> answers, CancellationToken ct)
    {
        if (!text.Contains("${", StringComparison.Ordinal)) return text;
        var result = new StringBuilder();
        var last = 0;
        foreach (Match m in Pattern().Matches(text))
        {
            result.Append(text, last, m.Index - last);
            result.Append(await ValueAsync(m.Groups[1].Value, context, answers, ct).ConfigureAwait(false));
            last = m.Index + m.Length;
        }

        result.Append(text, last, text.Length - last);
        return result.ToString();
    }

    private static async Task<string> ValueAsync(string name, LaunchContext context, Dictionary<string, string> answers, CancellationToken ct)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        var head = colon < 0 ? name : name[..colon];
        var argument = colon < 0 ? "" : name[(colon + 1)..];
        string NeedFile()
        {
            return context.ActiveFile ?? throw new LaunchException($"The variable ${{{name}}} needs a file open in the editor");
        }

        switch (head)
        {
            case "workspaceFolder": return context.WorkspaceFolder;
            case "workspaceFolderBasename": return Path.GetFileName(context.WorkspaceFolder.TrimEnd(Path.DirectorySeparatorChar, '/'));
            case "file": return NeedFile();
            case "fileWorkspaceFolder": return context.WorkspaceFolder;
            case "relativeFile": return Path.GetRelativePath(context.WorkspaceFolder, NeedFile());
            case "relativeFileDirname": return Path.GetDirectoryName(Path.GetRelativePath(context.WorkspaceFolder, NeedFile())) is { Length: > 0 } rd ? rd : ".";
            case "fileBasename": return Path.GetFileName(NeedFile());
            case "fileBasenameNoExtension": return Path.GetFileNameWithoutExtension(NeedFile());
            case "fileExtname": return Path.GetExtension(NeedFile());
            case "fileDirname": return Path.GetDirectoryName(NeedFile()) ?? "";
            case "fileDirnameBasename": return Path.GetFileName(Path.GetDirectoryName(NeedFile())) ?? "";
            case "cwd": return context.Cwd;
            case "lineNumber": return (context.LineNumber ?? throw new LaunchException($"The variable ${{{name}}} needs an editor open")).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "selectedText": return context.SelectedText ?? "";
            case "pathSeparator": case "/": return Path.DirectorySeparatorChar.ToString();
            case "userHome": return context.UserHome;
            case "env": return context.Env(argument) ?? "";
            case "config": return context.Config?.Invoke(argument) ?? throw new LaunchException($"The setting \"{argument}\" in ${{{name}}} is not set");
            case "input":
                if (answers.TryGetValue(argument, out var known)) return known;
                var input = context.Inputs.FirstOrDefault(i => i.Id == argument) ?? throw new LaunchException($"launch.json has no input called \"{argument}\" for ${{{name}}}");
                var asked = input.Type == "command" && input.Command is { } command
                    ? await Run(command).ConfigureAwait(false)
                    : context.Ask is { } ask ? await ask(input, ct).ConfigureAwait(false) : input.Default;
                answers[argument] = asked ?? throw new LaunchException("Starting was cancelled");
                return answers[argument];
            case "command":
                return await Run(argument).ConfigureAwait(false) ?? throw new LaunchException("Starting was cancelled");
            default:
                throw new LaunchException($"The variable ${{{name}}} is not known");
        }

        async Task<string?> Run(string command) =>
            context.Command is { } run ? await run(command, ct).ConfigureAwait(false) : throw new LaunchException($"The command \"{command}\" in ${{{name}}} is not available here");
    }
}
