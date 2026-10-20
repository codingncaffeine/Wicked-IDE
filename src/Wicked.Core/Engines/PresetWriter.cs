using System.Text;
using System.Text.RegularExpressions;

namespace Wicked.Core.Engines;

/// <summary>One section of the router's preset file: a model id and CLI options (keys are the flag names without dashes).</summary>
public sealed record PresetSection(string Name, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>
    /// The section for one installed model. The router needs either a model in its models folder or a section that names the
    /// file, and naming the file by absolute path is what lets our own <c>publisher/repo/file.gguf</c> layout work.
    /// </summary>
    public static PresetSection ForModel(string id, string modelPath, string? mmprojPath = null, int? contextSize = null, string? gpuLayers = null, IReadOnlyDictionary<string, string>? extra = null)
    {
        var values = new Dictionary<string, string>(extra ?? new Dictionary<string, string>(), StringComparer.Ordinal) { ["model"] = modelPath };
        if (mmprojPath is not null) values["mmproj"] = mmprojPath;
        if (contextSize is { } c) values["c"] = c.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (gpuLayers is not null) values["n-gpu-layers"] = gpuLayers;
        return new PresetSection(id, values);
    }
}

/// <summary>
/// Writes the INI that <c>llama-server --models-preset</c> reads: <c>version = 1</c>, a <c>[*]</c> block of defaults, then one
/// block per model. Output is deterministic (sections and keys sorted) and the file is only replaced when its text changes,
/// so the router's file watcher sees edits only when something really changed.
/// </summary>
public static partial class PresetWriter
{
    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static string Render(IReadOnlyDictionary<string, string>? globals, IEnumerable<PresetSection> models)
    {
        var text = new StringBuilder("version = 1\n");
        Block(text, "*", globals ?? new Dictionary<string, string>());
        foreach (var model in models.OrderBy(m => m.Name, StringComparer.Ordinal)) Block(text, model.Name, model.Values);
        return text.ToString();
    }

    /// <summary>Writes the file atomically; returns false when it already had exactly this text.</summary>
    public static async Task<bool> WriteAsync(string path, IReadOnlyDictionary<string, string>? globals, IEnumerable<PresetSection> models, CancellationToken ct = default)
    {
        var text = Render(globals, models);
        if (File.Exists(path) && await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) == text) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
        return true;
    }

    private static void Block(StringBuilder text, string name, IReadOnlyDictionary<string, string> values)
    {
        if (name.AsSpan().IndexOfAny("[]\r\n") >= 0 || name.Length == 0) throw new ArgumentException($"Invalid preset section name: '{name}'");
        if (name != "*" && values.Count == 0) return;
        text.Append('\n').Append('[').Append(name).Append("]\n");
        foreach (var (key, value) in values.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!KeyPattern().IsMatch(key)) throw new ArgumentException($"Invalid preset key: '{key}'");
            if (value.AsSpan().IndexOfAny("\r\n") >= 0) throw new ArgumentException($"A preset value cannot span lines (key '{key}')");
            text.Append(key).Append(" = ").Append(value).Append('\n');
        }
    }
}
