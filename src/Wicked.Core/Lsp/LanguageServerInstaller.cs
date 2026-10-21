using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Wicked.Core.Engines;

namespace Wicked.Core.Lsp;

/// <summary>A server install could not be done; the message says why in the user's terms.</summary>
public sealed class ServerInstallException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// Installs language servers into Wicked's own servers folder, never system-wide (D14): a GitHub release asset for this
/// platform, checked against the sha256 GitHub publishes for it and unpacked; or the server's own package manager (npm,
/// dotnet tool, go install) pointed at the same folder. Afterwards <c>servers/&lt;id&gt;/bin/&lt;program&gt;</c> runs it, which
/// is where <see cref="ServerLocator"/> looks first. Everything runs on workers; progress is reported as lines.
/// </summary>
public sealed class LanguageServerInstaller(HttpClient http, string serversFolder, Uri? githubApi = null)
{
    private readonly Uri _api = githubApi ?? new Uri("https://api.github.com/");

    /// <summary>This machine's platform as the catalog's assets name it (linux-x64, linux-arm64, win-x64, osx-arm64).</summary>
    public static string Platform
    {
        get
        {
            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            if (OperatingSystem.IsWindows()) return $"win-{arch}";
            if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
            return $"linux-{arch}";
        }
    }

    /// <summary>Whether this kind of install can run here (npm needs Node, a tool install needs its SDK).</summary>
    public static string? MissingTool(LanguageServerSpec spec, Func<string, string?>? env = null)
    {
        var locator = new ServerLocator("", env);
        string? Need(string program, string what) =>
            locator.Find(new LanguageServerSpec("tool", program, [], [program], [], InstallKind.System)) is null ? $"{spec.Name} installs with {what}, which is not installed" : null;
        return spec.Install switch
        {
            InstallKind.Npm => Need("npm", "npm (Node.js)"),
            InstallKind.DotnetTool => Need("dotnet", "the .NET SDK"),
            InstallKind.Go => Need("go", "Go"),
            InstallKind.System => $"{spec.Name} comes from your system's packages" + (spec.SystemPackages.Count == 0 ? "" : ": " + string.Join(", ", spec.SystemPackages.Select(kv => $"{kv.Key} {kv.Value}"))),
            _ => null,
        };
    }

    public async Task InstallAsync(LanguageServerSpec spec, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (MissingTool(spec) is { } missing) throw new ServerInstallException(missing);
        var folder = Path.Combine(serversFolder, spec.Id);
        Directory.CreateDirectory(folder);
        switch (spec.Install)
        {
            case InstallKind.GitHubRelease:
                await FromGitHubAsync(spec, folder, progress, ct).ConfigureAwait(false);
                break;
            case InstallKind.Npm:
                await RunAsync("npm", ["install", "--prefix", folder, "--no-fund", "--no-audit", .. spec.Package!.Split(' ')], folder, progress, ct).ConfigureAwait(false);
                LinkPrograms(spec, folder, Path.Combine(folder, "node_modules", ".bin"));
                break;
            case InstallKind.DotnetTool:
                await RunAsync("dotnet", ["tool", "install", spec.Package!, "--tool-path", Path.Combine(folder, "bin")], folder, progress, ct).ConfigureAwait(false);
                break;
            case InstallKind.Go:
                await RunAsync("go", ["install", spec.Package!], folder, progress, ct, new Dictionary<string, string> { ["GOBIN"] = Path.Combine(folder, "bin") }).ConfigureAwait(false);
                break;
            default:
                throw new ServerInstallException($"{spec.Name} comes from your system's packages");
        }

        if (new ServerLocator(serversFolder, _ => null).Find(spec) is null) throw new ServerInstallException($"{spec.Name} installed, but its program was not found in {folder}");
        progress?.Report($"{spec.Name} is installed.");
    }

    private async Task FromGitHubAsync(LanguageServerSpec spec, string folder, IProgress<string>? progress, CancellationToken ct)
    {
        var platform = Platform;
        var wanted = spec.Assets?.FirstOrDefault(a => a.Platform == platform) ?? throw new ServerInstallException($"{spec.Name} has no build for {platform}");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_api, $"repos/{spec.Repository}/releases/latest"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
        if (Environment.GetEnvironmentVariable("GITHUB_TOKEN") is { Length: > 0 } token) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new ServerInstallException($"GitHub answered {(int)response.StatusCode} for {spec.Repository}'s latest release");
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? throw new ServerInstallException("the release has no tag");
        var version = tag.TrimStart('v');
        var name = wanted.Pattern.Replace("{version}", version, StringComparison.Ordinal);
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray().FirstOrDefault(a => a.GetProperty("name").GetString() == name);
        if (asset.ValueKind != JsonValueKind.Object) throw new ServerInstallException($"{spec.Repository} {tag} has no {name}");
        var url = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        var digest = asset.TryGetProperty("digest", out var d) && d.GetString() is { } x && x.StartsWith("sha256:", StringComparison.Ordinal) ? x[7..].ToLowerInvariant() : null;

        progress?.Report($"Downloading {name} ({tag})…");
        var download = Path.Combine(folder, ".download-" + name);
        await using (var source = await http.GetStreamAsync(url, ct).ConfigureAwait(false))
        await using (var file = File.Create(download))
        {
            await source.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        if (digest is not null)
        {
            await using var check = File.OpenRead(download);
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(check, ct).ConfigureAwait(false));
            if (actual != digest)
            {
                File.Delete(download);
                throw new ServerInstallException($"{name} did not match the checksum GitHub publishes for it");
            }
        }

        progress?.Report($"Unpacking {name}…");
        var target = Path.Combine(folder, version);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);
        var program = wanted.Program.Replace("{version}", version, StringComparison.Ordinal);
        switch (wanted.Packing)
        {
            case "zip" or "tar.gz":
                await ArchiveExtractor.ExtractAsync(wanted.Packing == "zip" ? Rename(download, ".zip") : Rename(download, ".tar.gz"), target, ct).ConfigureAwait(false);
                break;
            case "gz":
                await using (var gz = new GZipStream(File.OpenRead(download), CompressionMode.Decompress))
                await using (var output = File.Create(Path.Combine(target, program)))
                {
                    await gz.CopyToAsync(output, ct).ConfigureAwait(false);
                }

                break;
            default:
                File.Copy(download, Path.Combine(target, program), overwrite: true);
                break;
        }

        foreach (var leftover in Directory.GetFiles(folder, ".download-*")) File.Delete(leftover);
        var executable = Path.Combine(target, program);
        if (!File.Exists(executable)) throw new ServerInstallException($"{name} has no {program} inside");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        WriteLauncher(folder, Path.GetFileName(spec.Programs[0]) + (OperatingSystem.IsWindows() ? ".exe" : ""), executable);
        File.WriteAllText(Path.Combine(folder, "version.txt"), tag + "\n");
    }

    private static string Rename(string download, string extension)
    {
        var to = download + extension;
        File.Move(download, to, overwrite: true);
        return to;
    }

    /// <summary>servers/&lt;id&gt;/bin/&lt;name&gt;: a symbolic link to the real program (a copy on Windows).</summary>
    private static void WriteLauncher(string folder, string name, string program)
    {
        var bin = Path.Combine(folder, "bin");
        Directory.CreateDirectory(bin);
        var link = Path.Combine(bin, name);
        if (File.Exists(link) || new FileInfo(link).LinkTarget is not null) File.Delete(link);
        if (OperatingSystem.IsWindows()) File.Copy(program, link);
        else File.CreateSymbolicLink(link, program);
    }

    private static void LinkPrograms(LanguageServerSpec spec, string folder, string from)
    {
        foreach (var name in spec.Programs)
        {
            var source = Path.Combine(from, name);
            if (File.Exists(source) || File.Exists(source + ".cmd")) WriteLauncher(folder, Path.GetFileName(File.Exists(source) ? source : source + ".cmd"), File.Exists(source) ? source : source + ".cmd");
        }
    }

    internal static async Task RunAsync(string program, IReadOnlyList<string> args, string cwd, IProgress<string>? progress, CancellationToken ct, IReadOnlyDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo(program) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) info.ArgumentList.Add(a);
        if (env is not null)
        {
            foreach (var (k, v) in env) info.Environment[k] = v;
        }

        progress?.Report($"{program} {string.Join(' ', args)}");
        using var process = Process.Start(info) ?? throw new ServerInstallException($"{program} did not start");
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line) progress?.Report(line);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { Length: > 0 } line) progress?.Report(line);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode != 0) throw new ServerInstallException($"{program} failed (exit {process.ExitCode}); the Output panel has its messages");
    }
}
