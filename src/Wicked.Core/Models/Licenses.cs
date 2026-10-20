namespace Wicked.Core.Models;

/// <summary>
/// Which licences install without asking (D8). MIT, Apache-2.0, BSD and the other permissive ones are accepted by the
/// user's choosing to install; any other licence (a community or "open weights" licence with terms of use, or none stated)
/// is shown first and accepted on purpose.
/// </summary>
public static class Licenses
{
    private static readonly HashSet<string> Permissive = new(StringComparer.OrdinalIgnoreCase)
    {
        "mit", "mit-0", "apache-2.0", "isc", "unlicense", "cc0-1.0", "zlib", "0bsd", "bsl-1.0", "wtfpl", "ncsa", "postgresql",
    };

    public static bool IsPermissive(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var key = id.Trim();
        return Permissive.Contains(key) || key.StartsWith("bsd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The licence text to read: the catalog's own link, else the SPDX page for an SPDX id, else the model's page on the Hub.</summary>
    public static string LinkFor(string? id, string? explicitUrl, string? hubRepo)
    {
        if (!string.IsNullOrWhiteSpace(explicitUrl)) return explicitUrl;
        if (hubRepo is not null) return $"https://huggingface.co/{hubRepo}";
        return !string.IsNullOrWhiteSpace(id) ? $"https://spdx.org/licenses/{Uri.EscapeDataString(id)}.html" : "https://huggingface.co/";
    }

    /// <summary>The licence as people write it: <c>apache-2.0</c> becomes <c>Apache-2.0</c>, <c>llama4</c> stays as the author named it.</summary>
    public static string Display(string? id) => string.IsNullOrWhiteSpace(id) ? "no licence stated" : id.Trim();
}
