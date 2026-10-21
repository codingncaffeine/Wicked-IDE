using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wicked.Core.Debug;

/// <summary>A launch.json that cannot be used, or a configuration that cannot be started; the message says why in the user's terms.</summary>
public sealed class LaunchException(string message) : IOException(message);

/// <summary>One entry of launch.json's <c>configurations</c>: what to start, and the whole object to hand to the adapter.</summary>
public sealed record LaunchConfiguration(string Name, string Type, string Request, JsonObject Properties)
{
    /// <summary>The task to run before starting (a label from tasks.json).</summary>
    public string? PreLaunchTask => Properties["preLaunchTask"]?.GetValueKind() == JsonValueKind.String ? Properties["preLaunchTask"]!.GetValue<string>() : null;

    /// <summary>The task to run after the session ends.</summary>
    public string? PostDebugTask => Properties["postDebugTask"]?.GetValueKind() == JsonValueKind.String ? Properties["postDebugTask"]!.GetValue<string>() : null;
}

/// <summary>An entry of <c>compounds</c>: several configurations started together.</summary>
public sealed record LaunchCompound(string Name, IReadOnlyList<string> Configurations, bool StopAll, string? PreLaunchTask);

/// <summary>An entry of <c>inputs</c>: a value asked of the user when a configuration uses <c>${input:id}</c>.</summary>
public sealed record LaunchInput(string Id, string Type, string? Description, string? Default, IReadOnlyList<string> Options, bool Password, string? Command = null);

/// <summary>
/// A launch.json read the way VS Code reads it: comments and trailing commas allowed, the platform blocks (<c>linux</c>,
/// <c>windows</c>, <c>osx</c>) laid over each configuration, compounds and inputs kept. A file that does not parse is reported,
/// not guessed at.
/// </summary>
public sealed class LaunchFile
{
    public static readonly LaunchFile Empty = new([], [], []);

    public LaunchFile(IReadOnlyList<LaunchConfiguration> configurations, IReadOnlyList<LaunchCompound> compounds, IReadOnlyList<LaunchInput> inputs)
    {
        Configurations = configurations;
        Compounds = compounds;
        Inputs = inputs;
    }

    public IReadOnlyList<LaunchConfiguration> Configurations { get; }

    public IReadOnlyList<LaunchCompound> Compounds { get; }

    public IReadOnlyList<LaunchInput> Inputs { get; }

    /// <summary>Everything the picker lists: configurations, then compounds.</summary>
    public IEnumerable<string> Names => Configurations.Select(c => c.Name).Concat(Compounds.Select(c => c.Name));

    public LaunchConfiguration? Configuration(string name) => Configurations.FirstOrDefault(c => c.Name == name);

    public LaunchCompound? Compound(string name) => Compounds.FirstOrDefault(c => c.Name == name);

    public static string PlatformKey => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux";

    private static readonly string[] PlatformKeys = ["linux", "windows", "osx"];

    /// <summary>Parses the text of a launch.json; throws <see cref="LaunchException"/> naming the line when it is not JSON.</summary>
    public static LaunchFile Parse(string text, string? platform = null)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex)
        {
            throw new LaunchException($"launch.json could not be read: {ex.Message}" + (ex.LineNumber is { } line ? $" (line {line + 1})" : ""));
        }

        if (root is not JsonObject top) return Empty;
        platform ??= PlatformKey;
        var configurations = new List<LaunchConfiguration>();
        if (top["configurations"] is JsonArray list)
        {
            foreach (var item in list)
            {
                if (item is not JsonObject raw) continue;
                var merged = Merge(raw, platform);
                var name = Text(merged, "name") ?? throw new LaunchException("A launch configuration has no \"name\"");
                var type = Text(merged, "type") ?? throw new LaunchException($"The launch configuration \"{name}\" has no \"type\"");
                var request = Text(merged, "request") ?? throw new LaunchException($"The launch configuration \"{name}\" has no \"request\"");
                if (request is not ("launch" or "attach")) throw new LaunchException($"The launch configuration \"{name}\" has the request \"{request}\"; it must be \"launch\" or \"attach\"");
                configurations.Add(new LaunchConfiguration(name, type, request, merged));
            }
        }

        var compounds = new List<LaunchCompound>();
        if (top["compounds"] is JsonArray cs)
        {
            foreach (var item in cs)
            {
                if (item is not JsonObject c || Text(c, "name") is not { } name) continue;
                var members = c["configurations"] is JsonArray m ? m.Select(Member).Where(s => s is not null).Select(s => s!).ToList() : [];
                compounds.Add(new LaunchCompound(name, members, c["stopAll"]?.GetValueKind() == JsonValueKind.True, Text(c, "preLaunchTask")));
            }
        }

        var inputs = new List<LaunchInput>();
        if (top["inputs"] is JsonArray ins)
        {
            foreach (var item in ins)
            {
                if (item is not JsonObject i || Text(i, "id") is not { } id) continue;
                var options = i["options"] is JsonArray o ? o.Select(x => x is JsonObject obj ? Text(obj, "value") : x?.GetValueKind() == JsonValueKind.String ? x.GetValue<string>() : null).Where(s => s is not null).Select(s => s!).ToList() : [];
                inputs.Add(new LaunchInput(id, Text(i, "type") ?? "promptString", Text(i, "description"), Text(i, "default"), options, i["password"]?.GetValueKind() == JsonValueKind.True, Text(i, "command")));
            }
        }

        return new LaunchFile(configurations, compounds, inputs);
    }

    /// <summary>Reads .vscode/launch.json of a workspace; <see cref="Empty"/> when there is none. Disk work: call from a worker.</summary>
    public static async Task<LaunchFile> ReadAsync(string workspace, CancellationToken ct = default)
    {
        var path = PathIn(workspace);
        if (!File.Exists(path)) return Empty;
        return Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
    }

    public static string PathIn(string workspace) => Path.Combine(workspace, ".vscode", "launch.json");

    /// <summary>A configuration with the block for this platform laid over it and the platform blocks removed.</summary>
    private static JsonObject Merge(JsonObject raw, string platform)
    {
        var result = new JsonObject();
        foreach (var (key, value) in raw)
        {
            if (!PlatformKeys.Contains(key)) result[key] = value?.DeepClone();
        }

        if (raw[platform] is JsonObject overrides)
        {
            foreach (var (key, value) in overrides) result[key] = value?.DeepClone();
        }

        return result;
    }

    private static string? Text(JsonObject o, string name) => o[name]?.GetValueKind() == JsonValueKind.String ? o[name]!.GetValue<string>() : null;

    private static string? Member(JsonNode? node) => node switch
    {
        JsonObject o => Text(o, "name"),
        { } n when n.GetValueKind() == JsonValueKind.String => n.GetValue<string>(),
        _ => null,
    };
}
