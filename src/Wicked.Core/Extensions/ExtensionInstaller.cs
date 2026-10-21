using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wicked.Core.Engines;

namespace Wicked.Core.Extensions;

/// <summary>
/// Gets an extension's files into a staging folder from a folder, a .zip or a GitHub release; the manager reads the manifest
/// there, asks the user, and only then moves them into the extensions folder. Nothing here runs the extension's code.
/// </summary>
internal static partial class ExtensionInstaller
{
    /// <summary>The most an extension may unpack to; a zip that claims more is refused.</summary>
    private const long MaxUnpackedBytes = 512L * 1024 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryName();

    public static void StageFolder(string source, string stage, CancellationToken ct)
    {
        if (!Directory.Exists(source)) throw new ExtensionInstallException($"{source} is not a folder");
        if (!File.Exists(Path.Combine(source, ExtensionManifest.FileName))) throw new ExtensionInstallException($"{source} has no {ExtensionManifest.FileName}");
        Copy(new DirectoryInfo(source), new DirectoryInfo(stage), ct);
    }

    private static void Copy(DirectoryInfo from, DirectoryInfo to, CancellationToken ct)
    {
        to.Create();
        foreach (var entry in from.EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (entry.LinkTarget is not null) continue; // a link could point anywhere on the disk
            if (entry is DirectoryInfo directory)
            {
                if (directory.Name is ".git" or "obj") continue;
                Copy(directory, new DirectoryInfo(Path.Combine(to.FullName, directory.Name)), ct);
            }
            else if (entry is FileInfo file)
            {
                file.CopyTo(Path.Combine(to.FullName, file.Name), overwrite: true);
            }
        }
    }

    public static void StageZip(string zip, string stage, CancellationToken ct)
    {
        if (!File.Exists(zip)) throw new ExtensionInstallException($"{zip} does not exist");
        try
        {
            using (var archive = ZipFile.OpenRead(zip))
            {
                if (archive.Entries.Sum(e => e.Length) > MaxUnpackedBytes) throw new ExtensionInstallException("The package unpacks to more than 512 MB; it was not opened");
            }

            ArchiveExtractor.ExtractZip(zip, stage, ct);
        }
        catch (InvalidDataException ex)
        {
            throw new ExtensionInstallException($"{Path.GetFileName(zip)} is not a usable package: {ex.Message}", ex);
        }

        // A package may hold its files in one folder (how most tools zip them): lift them out.
        if (!File.Exists(Path.Combine(stage, ExtensionManifest.FileName)))
        {
            var inner = ArchiveExtractor.FlattenRoot(stage);
            if (inner == stage || !File.Exists(Path.Combine(inner, ExtensionManifest.FileName))) throw new ExtensionInstallException($"The package has no {ExtensionManifest.FileName} at its top");
            var lifted = stage + ".lift";
            Directory.Move(inner, lifted);
            Directory.Delete(stage, recursive: true);
            Directory.Move(lifted, stage);
        }
    }

    /// <summary>Downloads the newest release's <c>*.wicked.zip</c> into <paramref name="folder"/>, checked against the sha256 GitHub (or a <c>.sha256</c> asset beside it) publishes.</summary>
    public static async Task<(string Path, bool Verified)> DownloadReleaseAsync(HttpClient http, Uri api, string repository, string folder, CancellationToken ct)
    {
        repository = repository.Trim().Trim('/');
        if (repository.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) repository = repository["https://github.com/".Length..].Trim('/');
        if (!RepositoryName().IsMatch(repository)) throw new ExtensionInstallException("Write the repository as owner/name, for example acme/wicked-hello");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api, $"repos/{repository}/releases/latest"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new ExtensionInstallException($"GitHub answered {(int)response.StatusCode} for {repository}'s latest release");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var assets = doc.RootElement.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
            var asset = assets.FirstOrDefault(x => x.TryGetProperty("name", out var n) && n.GetString() is { } name && name.EndsWith(".wicked.zip", StringComparison.OrdinalIgnoreCase));
            if (asset.ValueKind != JsonValueKind.Object) throw new ExtensionInstallException($"{repository} {tag} has no asset named *.wicked.zip");
            var assetName = Path.GetFileName(asset.GetProperty("name").GetString()!);
            var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            var digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } x1 && x1.StartsWith("sha256:", StringComparison.Ordinal) ? x1[7..].ToLowerInvariant() : null;
            if (digest is null)
            {
                var sibling = assets.FirstOrDefault(x => x.TryGetProperty("name", out var n) && n.GetString() == assetName + ".sha256");
                if (sibling.ValueKind == JsonValueKind.Object)
                {
                    var text = await http.GetStringAsync(new Uri(sibling.GetProperty("browser_download_url").GetString()!), ct).ConfigureAwait(false);
                    var first = text.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant();
                    if (first is { Length: 64 } && first.All(Uri.IsHexDigit)) digest = first;
                }
            }

            var path = Path.Combine(folder, assetName);
            await using (var source = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
            await using (var file = File.Create(path))
            {
                await source.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            if (digest is null) return (path, false);
            await using var check = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
            if (actual != digest)
            {
                check.Close();
                File.Delete(path);
                throw new ExtensionInstallException($"{assetName} did not match the checksum published for it; nothing was installed");
            }

            return (path, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or UriFormatException)
        {
            throw new ExtensionInstallException($"Could not get {repository}'s latest release: {ex.Message}", ex);
        }
    }
}
