using System.Text.Json;
using Wicked.Core.Downloads;

namespace Wicked.Core.Engines;

/// <summary>An engine that is installed on disk: <c>engines/&lt;tag&gt;-&lt;backend&gt;/</c> with an <c>engine.json</c> beside the binaries.</summary>
public sealed record EngineInstall(
    string Id,
    string Tag,
    string Backend,
    string Directory,
    string ServerPath,
    DateTimeOffset InstalledAt,
    string MainAsset,
    string? MainSha256,
    string? CudartAsset);

public enum EnginePhase
{
    Downloading,
    Extracting,
    Verifying,
    Done,
}

public sealed record EngineInstallProgress(EnginePhase Phase, double Fraction, string Message);

public sealed class EngineInstallException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>What <c>llama-server</c> reports about itself: the numbers the plan treats as the truth for fit (D9).</summary>
public sealed record EngineReport(EngineVersion? Version, IReadOnlyList<EngineDevice> Devices, string RawVersionOutput, string RawDeviceOutput);

/// <summary>
/// Installs llama.cpp builds (D4): download through the <see cref="DownloadManager"/> (so resume, sha256 and disk-space
/// checks come for free), unpack into a temporary sibling folder, check that the server starts, and only then rename it into
/// place, so a half-installed engine never exists. <c>engines/current.json</c> names the active install; the one before it is
/// kept for rollback and older ones are pruned.
/// </summary>
public sealed class EngineInstaller(DownloadManager downloads, string enginesDirectory, string downloadsDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string CurrentFile => Path.Combine(enginesDirectory, "current.json");

    /// <summary>Downloads, unpacks, checks and (by default) activates <paramref name="choice"/> from <paramref name="release"/>.</summary>
    public async Task<EngineInstall> InstallAsync(
        EngineRelease release,
        EngineChoice choice,
        IProgress<EngineInstallProgress>? progress = null,
        bool activate = true,
        CancellationToken ct = default)
    {
        var main = choice.FindMain(release) ?? throw new EngineInstallException($"{release.Tag} has no build for {choice.Suffix}");
        var cudart = choice.CudartSuffix is null ? null : choice.FindCudart(release) ?? throw new EngineInstallException($"{release.Tag} has no CUDA runtime for {choice.Suffix}");
        var id = $"{release.Tag}-{choice.Backend.Slug}";

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var finalDirectory = Path.Combine(enginesDirectory, id);
            if (ReadInstall(finalDirectory) is { } existing)
            {
                if (activate) await SetCurrentAsync(existing.Id).ConfigureAwait(false);
                progress?.Report(new EngineInstallProgress(EnginePhase.Done, 1, $"{id} is already installed"));
                return existing;
            }

            Directory.CreateDirectory(enginesDirectory);
            Directory.CreateDirectory(downloadsDirectory);
            var assets = cudart is null ? new[] { main } : [main, cudart];
            var archives = await DownloadAsync(assets, progress, ct).ConfigureAwait(false);

            var temp = Path.Combine(enginesDirectory, ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                progress?.Report(new EngineInstallProgress(EnginePhase.Extracting, 0, "Unpacking " + main.Name));
                await ArchiveExtractor.ExtractAsync(archives[0], Path.Combine(temp, "main"), ct).ConfigureAwait(false);
                var root = ArchiveExtractor.FlattenRoot(Path.Combine(temp, "main"));
                if (cudart is not null)
                {
                    progress?.Report(new EngineInstallProgress(EnginePhase.Extracting, 0.5, "Unpacking " + cudart.Name));
                    await ArchiveExtractor.ExtractAsync(archives[1], Path.Combine(temp, "cudart"), ct).ConfigureAwait(false);
                    ArchiveExtractor.MergeInto(ArchiveExtractor.FlattenRoot(Path.Combine(temp, "cudart")), root);
                }

                var server = FindServer(root, choice.Platform.ServerFileName)
                             ?? throw new EngineInstallException($"{main.Name} does not contain {choice.Platform.ServerFileName}");
                MakeExecutable(server);

                progress?.Report(new EngineInstallProgress(EnginePhase.Verifying, 0, "Starting llama-server --version"));
                var version = await EngineCli.RunAsync(server, ["--version"], ProbeTimeout, ct).ConfigureAwait(false);
                if (version.TimedOut || EngineCli.ParseVersion(version.Combined) is null)
                {
                    throw new EngineInstallException($"The new llama-server does not start: {(version.TimedOut ? "timed out" : version.Combined.Trim())}");
                }

                var install = new EngineInstall(
                    id, release.Tag, choice.Backend.Slug, finalDirectory, Path.Combine(finalDirectory, Path.GetRelativePath(root, server)),
                    DateTimeOffset.UtcNow, main.Name, main.Sha256, cudart?.Name);
                await File.WriteAllTextAsync(Path.Combine(root, "engine.json"), JsonSerializer.Serialize(install, Json), ct).ConfigureAwait(false);

                if (System.IO.Directory.Exists(finalDirectory)) System.IO.Directory.Delete(finalDirectory, recursive: true);
                System.IO.Directory.Move(root, finalDirectory);
            }
            finally
            {
                TryDelete(temp);
            }

            foreach (var archive in archives) File.Delete(archive);
            if (activate) await SetCurrentAsync(id).ConfigureAwait(false);
            progress?.Report(new EngineInstallProgress(EnginePhase.Done, 1, "Installed " + id));
            return ReadInstall(finalDirectory)!;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every complete install, newest first.</summary>
    public IReadOnlyList<EngineInstall> List() =>
        !Directory.Exists(enginesDirectory)
            ? []
            : [.. Directory.EnumerateDirectories(enginesDirectory).Select(ReadInstall).OfType<EngineInstall>().OrderByDescending(i => i.InstalledAt)];

    public EngineInstall? Current => ReadState().Current is { } id ? ReadInstall(Path.Combine(enginesDirectory, id)) : null;

    public EngineInstall? Previous => ReadState().Previous is { } id ? ReadInstall(Path.Combine(enginesDirectory, id)) : null;

    /// <summary>Makes <paramref name="id"/> the active engine, remembers the old one for rollback and removes everything older.</summary>
    public async Task SetCurrentAsync(string id)
    {
        if (ReadInstall(Path.Combine(enginesDirectory, id)) is null) throw new EngineInstallException($"No installed engine {id}");
        var state = ReadState();
        if (state.Current == id) return;
        await WriteStateAsync(new State(id, state.Current)).ConfigureAwait(false);
        Prune();
    }

    /// <summary>Swaps back to the previous engine (and the active one becomes the previous).</summary>
    public async Task<EngineInstall> RollbackAsync()
    {
        var state = ReadState();
        if (state.Previous is not { } previous || ReadInstall(Path.Combine(enginesDirectory, previous)) is not { } install)
        {
            throw new EngineInstallException("There is no previous engine to roll back to");
        }

        await WriteStateAsync(new State(previous, state.Current)).ConfigureAwait(false);
        return install;
    }

    /// <summary>Runs <c>--version</c> and <c>--list-devices</c> of an install.</summary>
    public static async Task<EngineReport> ProbeAsync(EngineInstall install, CancellationToken ct = default)
    {
        var version = await EngineCli.RunAsync(install.ServerPath, ["--version"], ProbeTimeout, ct).ConfigureAwait(false);
        var devices = await EngineCli.RunAsync(install.ServerPath, ["--list-devices"], ProbeTimeout, ct).ConfigureAwait(false);
        return new EngineReport(EngineCli.ParseVersion(version.Combined), EngineCli.ParseDevices(devices.Combined), version.Combined, devices.Combined);
    }

    private async Task<string[]> DownloadAsync(EngineAsset[] assets, IProgress<EngineInstallProgress>? progress, CancellationToken ct)
    {
        var ids = new List<string>();
        var total = assets.Sum(a => a.Size);
        void OnChanged(object? sender, DownloadSnapshot s)
        {
            if (!ids.Contains(s.Id)) return;
            var done = ids.Sum(i => downloads.Get(i)?.BytesDone ?? 0);
            progress?.Report(new EngineInstallProgress(EnginePhase.Downloading, total == 0 ? 0 : (double)done / total, $"Downloading {s.Request.Title}"));
        }

        downloads.Changed += OnChanged;
        try
        {
            foreach (var asset in assets)
            {
                var destination = Path.Combine(downloadsDirectory, asset.Name);
                ids.Add(downloads.Enqueue(new DownloadRequest(asset.Url, destination, asset.Size, asset.Sha256, Title: asset.Name)));
            }

            try
            {
                var results = await Task.WhenAll(ids.Select(i => downloads.WaitAsync(i, ct))).ConfigureAwait(false);
                var failed = results.FirstOrDefault(r => r.State != DownloadState.Done);
                if (failed is not null) throw new EngineInstallException($"Downloading {failed.Request.Title} failed: {failed.Error}");
                return [.. results.Select(r => r.Request.Destination)];
            }
            catch (OperationCanceledException)
            {
                foreach (var i in ids) await downloads.CancelAsync(i).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            downloads.Changed -= OnChanged;
        }
    }

    private static string? FindServer(string root, string fileName)
    {
        var direct = Path.Combine(root, fileName);
        if (File.Exists(direct)) return direct;
        return Directory.EnumerateFiles(root, fileName, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2 }).FirstOrDefault();
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute);
    }

    private static EngineInstall? ReadInstall(string directory)
    {
        try
        {
            var file = Path.Combine(directory, "engine.json");
            if (!File.Exists(file)) return null;
            var stored = JsonSerializer.Deserialize<EngineInstall>(File.ReadAllText(file), Json);
            if (stored is null) return null;
            // Folders can be moved (a new data drive), so the paths are recomputed from where the folder is now.
            var server = Path.Combine(directory, Path.GetRelativePath(stored.Directory, stored.ServerPath));
            return File.Exists(server) ? stored with { Directory = directory, ServerPath = server } : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private void Prune()
    {
        var state = ReadState();
        foreach (var directory in Directory.EnumerateDirectories(enginesDirectory))
        {
            var name = Path.GetFileName(directory);
            if (name == state.Current || name == state.Previous || name.StartsWith('.')) continue;
            // Only folders we made (they carry engine.json) are ever removed.
            if (File.Exists(Path.Combine(directory, "engine.json"))) TryDelete(directory);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Could not remove {directory}: {ex.Message}");
        }
    }

    private State ReadState()
    {
        try
        {
            return File.Exists(CurrentFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(CurrentFile), Json) ?? new State(null, null) : new State(null, null);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return new State(null, null);
        }
    }

    private async Task WriteStateAsync(State state)
    {
        Directory.CreateDirectory(enginesDirectory);
        var temp = CurrentFile + ".tmp";
        await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(state, Json)).ConfigureAwait(false);
        File.Move(temp, CurrentFile, overwrite: true);
    }

    private sealed record State(string? Current, string? Previous);
}
